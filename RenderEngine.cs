using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ImageMagick;

using WpfApp = System.Windows.Application;

namespace UltraVideoEditor
{
    public class RenderEngine
    {
        private string _ffmpegPath;

        public RenderEngine(bool useHardwareAcceleration = true)
        {
            _ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
        }

        public async Task RenderSimpleAsync(
            List<TimelineItem> items,
            string outputPath,
            string format,
            IProgress<int> progress,
            List<SubtitleItem> subtitles = null,
            ExportSettingsData exportSettings = null,
            CancellationToken cancellationToken = default,
            bool useGPU = true,
            string resolution = "1920x1080",
            bool fastRender = false)
        {
            LogToMainWindow("RenderEngine: Počinjem renderovanje...");

            // Odabir enkodera — NVENC (GPU) je 10-20x brži od libx264 (CPU)
            // Automatski proveravamo da li je h264_nvenc dostupan na ovom sistemu.
            // Na testnom laptopu bez NVIDIA pada na CPU automatski.
            bool nvencAvailable = false;
            if (useGPU)
            {
                try
                {
                    // Brza proba — ako NVENC nije dostupan, FFmpeg vraca gresku
                    string testArgs = $"-f lavfi -i color=c=black:s=64x64:d=0.1 -c:v h264_nvenc -f null -";
                    string testOut  = await RunFFmpegGetOutputAsync(testArgs, CancellationToken.None);
                    nvencAvailable  = !testOut.Contains("No NVENC capable devices") &&
                                      !testOut.Contains("Cannot load") &&
                                      !testOut.Contains("Unknown encoder");
                }
                catch { nvencAvailable = false; }
            }

            string vEncArgs = nvencAvailable
                ? "-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0"   // NVENC: RTX 2060+ brz
                : "-c:v libx264 -preset veryfast -crf 23";               // CPU fallback
            string pixFmt = "-pix_fmt yuv420p";
            LogToMainWindow($"RenderEngine: Enkoder: {(nvencAvailable ? "h264_nvenc (GPU)" : "libx264 (CPU)")} | FastRender={fastRender}");

            if (!File.Exists(_ffmpegPath))
            {
                LogToMainWindow("RenderEngine: FFmpeg NIJE pronađen!");
                throw new FileNotFoundException($"FFmpeg nije pronađen: {_ffmpegPath}");
            }

            var sortedItems = items.OrderBy(i => i.Start).ToList();

            LogToMainWindow($"RenderEngine: Ukupno klipova prije filtriranja: {sortedItems.Count}");

            // Loguj sve klipove da vidimo šta imamo
            foreach (var item in sortedItems)
            {
                LogToMainWindow($"  Klip: Type={item.Type}, Name={item.Name}, Path={(string.IsNullOrEmpty(item.Path) ? "EMPTY" : item.Path)}");
            }

            // Sve slike (uključujući i one sa praznim Path)
            var allImageItems = sortedItems.Where(i => i.Type == "Image").ToList();
            LogToMainWindow($"RenderEngine: Ukupno Image klipova: {allImageItems.Count}");

            // Obične slike (imaju putanju do fajla i nisu tekstualne)
            var images = allImageItems.Where(i =>
                !string.IsNullOrEmpty(i.Path) &&
                File.Exists(i.Path) &&
                !i.Name.Contains("Najavni") &&
                !i.Name.Contains("Odjavni")).ToList();

            // Tekstualni slojevi (imaju prazan Path ili ime sadrži Najavni/Odjavni)
            var textImages = allImageItems.Where(i =>
                string.IsNullOrEmpty(i.Path) ||
                i.Name.Contains("Najavni") ||
                i.Name.Contains("Odjavni")).ToList();

            // Glavni audio: preferiramo TrackIndex=0, duži klip, ne tranziciju/pop
            var audio = sortedItems
                .Where(i => (i.Type == "Audio" || i.IsAudio) &&
                            !i.Name.StartsWith("🔊"))  // isključi tranzicione/pop zvukove
                .OrderByDescending(i => i.Duration)    // najduži = glavna muzika
                .FirstOrDefault()
                ?? sortedItems.FirstOrDefault(i => i.Type == "Audio" || i.IsAudio);
            var videos = sortedItems.Where(i => i.Type == "Video" || i.IsVideo).ToList();

            LogToMainWindow($"RenderEngine: Pronađeno {images.Count} slika, {textImages.Count} tekstualnih slojeva, {videos.Count} video klipova");
            LogToMainWindow($"RenderEngine: Odabrana rezolucija: {resolution}");
            LogToMainWindow($"RenderEngine: Sortirano {sortedItems.Count} klipova po vremenskoj liniji");

            if (images.Count == 0 && videos.Count == 0 && textImages.Count == 0)
                throw new Exception("Nema slika, tekstova ili videa za render");

            string tempDir = Path.Combine(Path.GetTempPath(), "UVE_Render_") + Guid.NewGuid().ToString().Substring(0, 8);
            Directory.CreateDirectory(tempDir);
            LogToMainWindow($"RenderEngine: Privremeni folder: {tempDir}");

            string[] res = resolution.Split('x');
            int targetWidth = int.Parse(res[0]);
            int targetHeight = int.Parse(res[1]);

            try
            {
                // Jedna petlja po TIMELINE REDOSLEDU (sortedItems je sortiran po Start).
                // Ranije su bile 3 odvojene petlje (images→text→video) što je narušavalo redosled:
                // "Odjavni tekst" (textImage) bi završio na poziciji 2 u concat listi, pre svih videa.
                // Sada svaki item ide u concat.txt tačno na svom mestu po Start poziciji.
                var videoFiles = new List<string>();

                // Mapa item → tempVideo putanja, za slučaj da neki item padne
                var itemToFile = new Dictionary<TimelineItem, string>();

                int total = sortedItems.Count(i => i.Type == "Image" || i.Type == "Video" || i.IsVideo || i.IsAudio == false);
                int current = 0;
                int fileIdx  = 0; // globalni brojač za jedinstvena imena temp fajlova

                foreach (var item in sortedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool isVideo    = item.Type == "Video" || item.IsVideo;
                    bool isImage    = item.Type == "Image";
                    bool isAudio    = item.Type == "Audio" || item.IsAudio;
                    bool isTextItem = isImage && (string.IsNullOrEmpty(item.Path) ||
                                                  item.Name.Contains("Najavni") ||
                                                  item.Name.Contains("Odjavni") ||
                                                  (item.Path != null && !File.Exists(item.Path)));

                    // Audio klipove preskačemo — obrađuju se posebno ispod
                    if (isAudio) continue;

                    string tempVideo = Path.Combine(tempDir, $"clip_{fileIdx++:D4}.mp4");
                    string durationStr = item.Duration.ToString(CultureInfo.InvariantCulture);
                    bool success = false;

                    if (isTextItem)
                    {
                        // ── Tekstualni sloj (najavni/odjavni tekst) ──────────────────
                        string displayText = !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path)
                            ? ExtractTextFromName(item.Name)   // ima PNG — izvuci ime
                            : item.Name;                        // nema PNG — koristi Name direktno
                        LogToMainWindow($"RenderEngine: Tekstualni sloj '{item.Name}' (Start={item.Start:F1}s, trajanje: {item.Duration:F2}s)");

                        string escapedText = EscapeText(displayText);
                        int fontSize = item.Name.Contains("Najavni") ? 60 : 34;

                        // Ako postoji PNG slika (CreateTextImage je napravio) — koristi je direktno
                        if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                        {
                            string preparedImage = await PrepareImageWithMagick(item.Path, tempDir);
                            if (preparedImage != null)
                            {
                                string scaleF = $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=1,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2";
                                string argsImg = $"-nostdin -loop 1 -i \"{preparedImage}\" {vEncArgs} -t {durationStr} {pixFmt} -vf \"{scaleF}\" -y \"{tempVideo}\"";
                                success = await RunFFmpegAsync(argsImg, cancellationToken);
                            }
                        }

                        // Fallback: generiši iz teksta direktno
                        if (!success)
                        {
                            string argsText = $"-nostdin -f lavfi -i color=c=black:s={targetWidth}x{targetHeight}:d={durationStr} " +
                                              $"-vf \"drawtext=text='{escapedText}':fontcolor=white:fontsize={fontSize}:x=(w-text_w)/2:y=(h-text_h)/2\" " +
                                              $"{vEncArgs} {pixFmt} -y \"{tempVideo}\"";
                            LogToMainWindow($"RenderEngine: FFmpeg komanda: {argsText.Substring(0, Math.Min(200, argsText.Length))}...");
                            success = await RunFFmpegAsync(argsText, cancellationToken);
                        }
                    }
                    else if (isImage)
                    {
                        // ── Obična slika ─────────────────────────────────────────────
                        if (!File.Exists(item.Path))
                        {
                            LogToMainWindow($"RenderEngine: Slika ne postoji: {item.Path}");
                            continue;
                        }
                        string preparedImage = await PrepareImageWithMagick(item.Path, tempDir);
                        if (preparedImage == null)
                        {
                            LogToMainWindow($"RenderEngine: Ne mogu obraditi sliku: {item.Name} – preskačem");
                            continue;
                        }
                        LogToMainWindow($"RenderEngine: Slika '{item.Name}' (Start={item.Start:F1}s, trajanje: {item.Duration:F2}s)");
                        string scaleF = $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=1,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2";
                        string argsImg = $"-nostdin -loop 1 -i \"{preparedImage}\" {vEncArgs} -t {durationStr} {pixFmt} -vf \"{scaleF}\" -y \"{tempVideo}\"";
                        success = await RunFFmpegAsync(argsImg, cancellationToken);
                        if (!success) LogToMainWindow($"RenderEngine: FFmpeg neuspješan za sliku {item.Name}");
                    }
                    else if (isVideo)
                    {
                        // ── Video klip ────────────────────────────────────────────────
                        if (!File.Exists(item.Path))
                        {
                            LogToMainWindow($"RenderEngine: Video ne postoji: {item.Path}");
                            continue;
                        }

                        string scaleFilter = $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=1,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2";

                        // Funkcija 4: Mood color grading — vizuelni ton prema raspoloženju scene
                        // AudioDescription nosi mood info ako je AI postavio
                        string moodTag    = item.AudioDescription ?? "";
                        string moodFilter = AIVideoCreator.GetMoodColorFilter(
                            ExtractTag(moodTag, "mood"), ExtractTag(moodTag, "context"));
                        string videoVf = string.IsNullOrEmpty(moodFilter)
                            ? scaleFilter
                            : $"{scaleFilter},{moodFilter}";

                        // Ken Burns (Smart Crop) — samo za kratke klipove, i samo ako nije brzi render
                        if (!fastRender)
                        {
                            try
                            {
                                if (item.Duration <= 8.0)
                                {
                                    var cropReg = await SmartCrop.AnalyzeVideo(item.Path, _ffmpegPath, cancellationToken);
                                    if (cropReg.Score >= 0.5)
                                    {
                                        videoVf = SmartCrop.BuildKenBurnsFilter(
                                            cropReg, targetWidth, targetHeight,
                                            1920, 1080, item.Duration, "zoom_in");
                                        LogToMainWindow($"RenderEngine: Smart crop zona: {cropReg.Zone}");
                                    }
                                }
                            }
                            catch { /* Smart crop nije kritičan */ }
                        }
                        else
                        {
                            LogToMainWindow($"RenderEngine: ⚡ Brzi render — Ken Burns preskočen za '{Path.GetFileName(item.Path)}'");
                        }

                        LogToMainWindow($"RenderEngine: Obrada videa '{Path.GetFileName(item.Path)}' (Start={item.Start:F1}s, trajanje: {item.Duration:F2}s)");

                        // ── KRITIČNO: -stream_loop -1 i zoompan su nekompatibilni ─────────────
                        // Sa -stream_loop -1, FFmpeg prima beskonačan ulazni stream.
                        // trim filter u teoriji zastavlja čitanje, ali u praksi FFmpeg
                        // mora da dekoduje i buffer-uje frejmove jer stream nema "kraj".
                        // Rezultat: 5s zoompan klip traje 10+ minuta jer se procesira
                        // stotine sekundi ulaza.
                        //
                        // REŠENJE:
                        // 1. Zoompan → bez -stream_loop, koristimo loop VF filter unutar pipeline-a.
                        //    FFmpeg tada zna tačan kraj streama i zoompan staje na d={frames}.
                        // 2. Obični klip → -stream_loop -1 kao i ranije, brzo i pouzdano.
                        bool isZoompan = videoVf.Contains("zoompan");
                        string argsVid;

                        if (isZoompan)
                        {
                            // loop=loop=-1:size=32767:start=0 loopuje video unutar VF grafa
                            // Ovo je dramatično brže — FFmpeg zna tačan kraj ulaznog streama
                            string loopedVf = $"loop=loop=-1:size=32767:start=0,{videoVf}";
                            argsVid = $"-nostdin -t {durationStr} -i \"{item.Path}\" -vf \"{loopedVf}\" {vEncArgs} {pixFmt} -an -y \"{tempVideo}\"";
                        }
                        else
                        {
                            // Obični klipovi: -stream_loop -1 sa -t kao i pre
                            argsVid = $"-nostdin -stream_loop -1 -t {durationStr} -i \"{item.Path}\" -vf \"{videoVf}\" {vEncArgs} {pixFmt} -an -y \"{tempVideo}\"";
                        }

                        success = await RunFFmpegAsync(argsVid, cancellationToken);
                        if (!success) LogToMainWindow($"RenderEngine: FFmpeg neuspešan za video {item.Name}");
                    }
                    else
                    {
                        continue; // nepoznat tip — preskoči
                    }

                    if (success)
                    {
                        videoFiles.Add(tempVideo);
                        itemToFile[item] = tempVideo;
                        LogToMainWindow($"RenderEngine: ✅ clip_{fileIdx - 1:D4} → {item.Name.Substring(0, Math.Min(40, item.Name.Length))} (Start={item.Start:F1}s)");
                    }

                    current++;
                    progress?.Report(Math.Min(85, current * 85 / Math.Max(1, total)));
                }

                if (videoFiles.Count == 0)
                    throw new Exception("Nijedna slika, tekst ili video nije uspešno konvertovan");

                string concatFile = Path.Combine(tempDir, "concat.txt");
                using (var sw = new StreamWriter(concatFile, false, new UTF8Encoding(false)))
                {
                    foreach (var vf in videoFiles)
                        await sw.WriteLineAsync($"file '{vf.Replace("\\", "/")}'");
                }

                string finalOutput = outputPath;
                string argsFinal;

                if (audio != null && File.Exists(audio.Path))
                {
                    string tempAudioPath = Path.Combine(tempDir, "audio" + Path.GetExtension(audio.Path));
                    File.Copy(audio.Path, tempAudioPath, true);

                    // Provjeri da li postoji ambient zvuk za miksovanje (legacy)
                    var ambientItem = items.FirstOrDefault(i =>
                        !string.IsNullOrEmpty(i.AmbientSoundPath) &&
                        File.Exists(i.AmbientSoundPath));

                    if (ambientItem != null)
                    {
                        LogToMainWindow("RenderEngine: Primjenjujem audio ducking...");
                        string mixedAudio = Path.Combine(tempDir, "mixed_audio.aac");
                        string mixedPath;

                        mixedPath = await AudioDucking.ApplyDucking(
                            tempAudioPath,
                            ambientItem.AmbientSoundPath,
                            audio.Duration,
                            mixedAudio,
                            _ffmpegPath,
                            cancellationToken);

                        if (mixedPath == tempAudioPath)
                        {
                            LogToMainWindow("RenderEngine: Ducking fallback - koristim jednostavan mix...");
                            string mixedAudio2 = Path.Combine(tempDir, "mixed_audio2.aac");
                            mixedPath = await AudioDucking.ApplySimpleDucking(
                                tempAudioPath,
                                ambientItem.AmbientSoundPath,
                                audio.Duration,
                                mixedAudio2,
                                _ffmpegPath,
                                cancellationToken);
                        }
                        tempAudioPath = mixedPath;
                    }

                    // Prikupi sve sekundarne Audio klipove (tranzicije, pop zvukovi)
                    // Oni su na TrackIndex=1 ili imaju ime koje počinje sa 🔊
                    var secondaryAudioClips = sortedItems
                        .Where(i => i.Type == "Audio" &&
                                    i != audio &&
                                    !string.IsNullOrEmpty(i.Path) &&
                                    File.Exists(i.Path))
                        .OrderBy(i => i.Start)
                        // Deduplikacija: ako postoji više zvukova na istoj poziciji (±0.05s), zadrži samo prvi
                        .GroupBy(i => Math.Round(i.Start, 1))
                        .Select(g => g.First())
                        // Sigurnosni limit: max 50 sekundarnih klipova (FFmpeg ograničenje args)
                        .Take(50)
                        .ToList();

                    if (secondaryAudioClips.Count > 0)
                    {
                        LogToMainWindow($"RenderEngine: Miksam {secondaryAudioClips.Count} tranzicionih zvukova u audio...");
                        string mixedWithTransitions = Path.Combine(tempDir, "audio_with_transitions.aac");
                        string mixResult = await MixSecondaryAudioClips(
                            tempAudioPath,
                            secondaryAudioClips,
                            mixedWithTransitions,
                            cancellationToken);
                        if (mixResult != null)
                        {
                            tempAudioPath = mixResult;
                            LogToMainWindow("RenderEngine: Tranzicioni zvukovi uspješno umiksani.");
                        }
                        else
                        {
                            LogToMainWindow("RenderEngine: Miksanje tranzicija neuspješno, koristim samo głównu muziku.");
                        }
                    }

                    LogToMainWindow($"RenderEngine: Dodajem audio: {Path.GetFileName(audio.Path)} (trajanje: {audio.Duration:F2}s)");

                    // Funkcija 3: Karaoke subtitle burn-in
                    // Ako postoje titlovi, upalimo ih direktno u video (ASS format, žuti tekst + crna senka)
                    string subtitleFile = await CreateSubtitlesFile(subtitles, tempDir);
                    if (!string.IsNullOrEmpty(subtitleFile) && File.Exists(subtitleFile))
                    {
                        // Mora re-encode video da bi upalili subtitle
                        // Koristimo libx264 veryfast — jedini siguran način za burn-in
                        string escapedSub = subtitleFile.Replace("\\", "/").Replace(":", "\\:");
                        LogToMainWindow($"RenderEngine: 🎤 Subtitle burn-in: {Path.GetFileName(subtitleFile)}");
                        argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" -i \"{tempAudioPath}\" " +
                                    $"-vf \"subtitles='{escapedSub}':force_style='FontSize=22,PrimaryColour=&H00FFFF00,OutlineColour=&H00000000,Outline=2,Shadow=1,Alignment=2'\" " +
                                    $"-c:v libx264 -preset veryfast -crf 20 -c:a aac -map 0:v -map 1:a -shortest -y \"{finalOutput}\"";
                    }
                    else
                    {
                        argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" -i \"{tempAudioPath}\" -c:v copy -c:a aac -map 0:v -map 1:a -shortest -y \"{finalOutput}\"";
                    }
                }
                else
                {
                    argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" -c:v copy -y \"{finalOutput}\"";
                }

                LogToMainWindow("RenderEngine: Pokrećem finalnu konkatenaciju...");
                await RunFFmpegAsync(argsFinal, cancellationToken);

                progress?.Report(100);

                long fileSize = new FileInfo(finalOutput).Length;
                LogToMainWindow($"RenderEngine: Renderovanje uspešno završeno! Veličina: {fileSize / 1024 / 1024} MB");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                    LogToMainWindow($"RenderEngine: Privremeni folder očišćen");
                }
                catch { }
            }
        }

        /// <summary>
        /// Miksuje sekundarne audio klipove (tranzicioni zvukovi, pop efekti) sa glavnim audiom.
        /// Koristi sekvencijalno miksovanje u batch-evima od 8.
        /// ISPRAVAN REDOSLED FILTERA: adelay → atrim (ne obrnuto!)
        /// </summary>
        private async Task<string> MixSecondaryAudioClips(
            string mainAudioPath,
            List<TimelineItem> secondaryClips,
            string outputPath,
            CancellationToken ct)
        {
            try
            {
                string currentAudio = mainAudioPath;
                string tempDir = Path.GetDirectoryName(outputPath);
                const int batchSize = 8;
                int batchNum = 0;

                var batches = secondaryClips
                    .Select((clip, i) => new { clip, i })
                    .GroupBy(x => x.i / batchSize)
                    .Select(g => g.Select(x => x.clip).ToList())
                    .ToList();

                foreach (var batch in batches)
                {
                    batchNum++;
                    bool isLast = batchNum == batches.Count;
                    string batchOutput = isLast
                        ? outputPath
                        : Path.Combine(tempDir, $"mix_batch_{batchNum}_{Guid.NewGuid().ToString().Substring(0, 6)}.aac");

                    var inputs = new StringBuilder();
                    inputs.Append($"-i \"{currentAudio}\" ");

                    var filterParts = new List<string>();
                    int idx = 1;

                    foreach (var clip in batch)
                    {
                        inputs.Append($"-i \"{clip.Path}\" ");
                        long delayMs = (long)(clip.Start * 1000);
                        double vol = Math.Max(0.5, Math.Min(clip.Volume / 100.0 * 2.0, 4.0));
                        double clipDur = Math.Max(0.05, clip.Duration > 0 ? clip.Duration : 0.5);

                        // ISPRAVAN REDOSLED:
                        // 1. adelay — pomjeri zvuk na tačnu poziciju u timeline-u
                        // 2. atrim — odsijeci sve iza clipDur sekundi od POČETKA streama
                        //    (ne od 0, jer je stream već pomjeren adelay-om)
                        // 3. volume — pojačaj
                        // Napomena: atrim=end_sample mora biti u apsolutnom vremenu (pos+dur)
                        double trimEnd = clip.Start + clipDur;
                        filterParts.Add(
                            $"[{idx}:a]" +
                            $"adelay={delayMs}|{delayMs}," +
                            $"atrim=end={trimEnd.ToString("F3", CultureInfo.InvariantCulture)}," +
                            $"volume={vol.ToString("F2", CultureInfo.InvariantCulture)}" +
                            $"[sa{idx}]");
                        idx++;
                    }

                    int numInputs = batch.Count + 1;
                    var mixInputs = "[0:a]" + string.Join("", Enumerable.Range(1, batch.Count).Select(i => $"[sa{i}]"));
                    filterParts.Add($"{mixInputs}amix=inputs={numInputs}:duration=first:normalize=0[aout]");

                    string filterComplex = string.Join("; ", filterParts);
                    string args = $"-nostdin {inputs}-filter_complex \"{filterComplex}\" " +
                                  $"-map \"[aout]\" -c:a aac -b:a 192k -y \"{batchOutput}\"";

                    LogToMainWindow($"RenderEngine: FFmpeg komanda: {args.Substring(0, Math.Min(args.Length, 200))}...");
                    bool ok = await RunFFmpegAsync(args, ct);

                    if (!ok || !File.Exists(batchOutput) || new FileInfo(batchOutput).Length < 1000)
                    {
                        LogToMainWindow($"RenderEngine: ⚠️ Batch {batchNum} neuspješan (filesize={( File.Exists(batchOutput) ? new FileInfo(batchOutput).Length : 0)}), preskačem.");
                        // Ne mijenjamo currentAudio — nastavljamo s prethodnim
                        continue;
                    }

                    // Obriši prethodni temp fajl (ne originalni)
                    if (currentAudio != mainAudioPath && File.Exists(currentAudio))
                        try { File.Delete(currentAudio); } catch { }

                    currentAudio = batchOutput;
                    LogToMainWindow($"RenderEngine: ✅ Batch {batchNum} uspješan → {Path.GetFileName(batchOutput)}");
                }

                // Ako output nije bio posljednji batch, kopiraj currentAudio na outputPath
                if (currentAudio != outputPath && File.Exists(currentAudio))
                    File.Copy(currentAudio, outputPath, true);

                return File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000
                    ? outputPath : null;
            }
            catch (Exception ex)
            {
                LogToMainWindow($"RenderEngine: Greška pri miksanju: {ex.Message}");
                return null;
            }
        }

        private string ExtractTextFromName(string name)
        {
            // Iz imena fajla izvlači tekst (npr. "Najavni tekst: 🎵 Nova pjesmica" -> "🎵 Nova pjesmica")
            if (name.Contains(":"))
            {
                return name.Substring(name.IndexOf(':') + 1).Trim();
            }
            // Ukloni "Najavni tekst: " ili "Odjavni tekst: " prefiks ako postoji
            if (name.StartsWith("Najavni tekst:"))
                return name.Substring("Najavni tekst:".Length).Trim();
            if (name.StartsWith("Odjavni tekst:"))
                return name.Substring("Odjavni tekst:".Length).Trim();
            return name;
        }
        private string EscapeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // Ukloni ili zamijeni problematične karaktere za FFmpeg drawtext
            string result = text
                .Replace("\\", "\\\\\\\\")
                .Replace("'", "'\\\\\\''")
                .Replace("\"", "\\\"")
                .Replace(":", "\\:")
                .Replace("@", "\\@")
                .Replace("č", "c")
                .Replace("ć", "c")
                .Replace("š", "s")
                .Replace("đ", "dj")
                .Replace("ž", "z")
                .Replace("Č", "C")
                .Replace("Ć", "C")
                .Replace("Š", "S")
                .Replace("Đ", "Dj")
                .Replace("Ž", "Z");

            return result;
        }

        private async Task<string> CreateSubtitlesFile(List<SubtitleItem> subtitles, string tempDir)
        {
            try
            {
                if (subtitles == null || subtitles.Count == 0) return null;

                string srtFile = Path.Combine(tempDir, "subtitles.srt");
                using (var sw = new StreamWriter(srtFile, false, Encoding.UTF8))
                {
                    int index = 1;
                    foreach (var sub in subtitles.OrderBy(s => s.Start))
                    {
                        sw.WriteLine(index);
                        sw.WriteLine($"{FormatTime(sub.Start)} --> {FormatTime(sub.End)}");
                        sw.WriteLine(sub.Text);
                        sw.WriteLine();
                        index++;
                    }
                }
                LogToMainWindow($"RenderEngine: Kreirano {subtitles.Count} titlova");
                return srtFile;
            }
            catch (Exception ex)
            {
                LogToMainWindow($"RenderEngine: Greška pri kreiranju titlova: {ex.Message}");
                return null;
            }
        }

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
        }

        private async Task<string> PrepareImageWithMagick(string imagePath, string tempDir)
        {
            try
            {
                if (!File.Exists(imagePath)) return null;
                using (var image = new MagickImage(imagePath))
                {
                    image.Format = MagickFormat.Jpeg;
                    image.Quality = 95;
                    string tempJpg = Path.Combine(tempDir, Guid.NewGuid().ToString() + ".jpg");
                    await image.WriteAsync(tempJpg);
                    return tempJpg;
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow($"RenderEngine: Magick.NET ne može učitati {imagePath}: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> RunFFmpegAsync(string arguments, CancellationToken ct)
        {
            LogToMainWindow($"RenderEngine: FFmpeg komanda: {arguments.Substring(0, Math.Min(200, arguments.Length))}...");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    WorkingDirectory = Path.GetTempPath()
                }
            };

            process.Start();
            process.StandardInput.Close();

            var errorBuilder = new StringBuilder();
            var outputBuilder = new StringBuilder();

            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            try
            {
                await process.WaitForExitAsync(ct);
                LogToMainWindow($"RenderEngine: FFmpeg završen, exit code: {process.ExitCode}");

                if (process.ExitCode != 0)
                {
                    LogToMainWindow($"RenderEngine: FFmpeg greška: {errorBuilder.ToString()}");
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                LogToMainWindow($"RenderEngine: Operacija otkazana");
                return false;
            }
            catch (Exception ex)
            {
                LogToMainWindow($"RenderEngine: Izuzetak: {ex.Message}");
                return false;
            }
        }

        private async Task<string> RunFFmpegGetOutputAsync(string arguments, CancellationToken ct)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName        = _ffmpegPath,
                    Arguments       = arguments,
                    CreateNoWindow  = true,
                    UseShellExecute = false,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput  = true,
                    WorkingDirectory = Path.GetTempPath()
                }
            };

            process.Start();
            process.StandardInput.Close();

            string stderr = await process.StandardError.ReadToEndAsync();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(ct);

            return stderr + stdout;
        }

        /// <summary>Parsira tag iz AudioDescription stringa: "mood:happy|context:music"</summary>
        private static string ExtractTag(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return "";
            foreach (var part in text.Split('|'))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kv[1].Trim();
            }
            return "";
        }

        private void LogToMainWindow(string message)
        {
            try
            {
                if (WpfApp.Current != null && WpfApp.Current.Dispatcher != null)
                {
                    WpfApp.Current.Dispatcher.Invoke(() =>
                    {
                        if (WpfApp.Current.MainWindow is MainWindow main)
                            main.LogMessage(message, true);
                    });
                }
            }
            catch { }
        }
    }
}