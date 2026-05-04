using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;        // ← DODAJ OVO
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // 1. AUDIO DUCKING (Sidechain)
    //    Analizira glasnoću vokala i automatski utišava ambijent:
    //    - Dok peva: ambijent na 10-15%
    //    - Instrumentalne pauze: ambijent fade-in na 25%
    // ═══════════════════════════════════════════════════════════════

    public static class AudioDucking
    {
        /// <summary>
        /// Kreira finalni audio miks sa automatskim duckingom:
        /// vokali guraju ambijent dole, pauze ga vraćaju gore.
        /// </summary>
        public static async Task<string> ApplyDucking(
            string vocalPath,       // pesma (glas + instrumentala)
            string ambientPath,     // Freesound ambient fajl
            double totalDuration,
            string outputPath,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(ffmpegPath) || !File.Exists(ambientPath))
                return vocalPath;

            // FFmpeg sidechaincompress filter:
            // 1. Detektuje glasnoću vokala (sidechain)
            // 2. Kada vokali pređe threshold (-18dB), ambient se compresuje na 12%
            // 3. attack=50ms (brzo reaguje), release=800ms (polako se vraća)
            // 4. U pauzama se ambient vraća na 25% (0.25)
            //
            // Filter graf:
            // [0:a] = vocal/music (sidechain izvor)
            // [1:a] = ambient (koji se duckuje)
            // sidechaincompress: threshold pri -18dB, ratio 8:1
            // Nakon toga: makeup gain vraća ambient na željeni nivo

            string dur = totalDuration.ToString("F2", CultureInfo.InvariantCulture);

            string filter =
                "[1:a]" +
                // Loop ambient da pokrije cijelo trajanje
                $"aloop=loop=-1:size=2147483647," +
                // Trim na trajanje pesme
                $"atrim=duration={dur}," +
                // Ducking: sidechaincompress
                // threshold: -18dB = kada vokali pjevaju
                // ratio: 8 = agresivno kompresovanje ambienta
                // attack: 50ms = brzo reaguje na glas
                // release: 800ms = polako se vraća (prirodan fade-in)
                // makeup: 0.25 = maksimalni nivo ambienta u pauzama (25%)
                "[sc_amb];" +
                "[0:a]asplit=2[sc_src][main_vocal];" +
                "[sc_src][sc_amb]sidechaincompress=" +
                "threshold=0.02:" +    // ~-34dB threshold
                "ratio=8:" +
                "attack=50:" +
                "release=800:" +
                "makeup=0.25" +
                "[ducked_amb];" +
                // Miksuj originalnu pesmu (100%) + ducked ambient
                "[main_vocal][ducked_amb]amix=" +
                "inputs=2:" +
                "duration=first:" +
                "dropout_transition=2" +
                "[final_mix]";

            string args =
                $"-nostdin " +
                $"-i \"{vocalPath}\" " +
                $"-stream_loop -1 -i \"{ambientPath}\" " +
                $"-filter_complex \"{filter}\" " +
                $"-map \"[final_mix]\" " +
                $"-c:a aac -b:a 256k " +
                $"-t {dur} " +
                $"-y \"{outputPath}\"";

            bool ok = await RunFFmpeg(ffmpegPath, args, ct);
            return ok && File.Exists(outputPath) ? outputPath : vocalPath;
        }

        /// <summary>
        /// Jednostavniji ducking bez sidechaincompress (fallback za stariji FFmpeg).
        /// Koristi volume filter sa ebur128 analizom.
        /// </summary>
        public static async Task<string> ApplySimpleDucking(
            string vocalPath,
            string ambientPath,
            double totalDuration,
            string outputPath,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(ffmpegPath) || !File.Exists(ambientPath))
                return vocalPath;

            string dur = totalDuration.ToString("F2", CultureInfo.InvariantCulture);

            // Jednostavan pristup: ambient na fiksnih 15% tokom cijele pesme
            // sa fade-in na početku i fade-out na kraju scene
            string filter =
                $"[1:a]aloop=loop=-1:size=2147483647," +
                $"atrim=duration={dur}," +
                $"volume=0.15," +           // 15% glasnoće ambienta
                $"afade=t=in:st=0:d=1," +   // 1s fade-in na početku
                $"afade=t=out:st={Math.Max(0, totalDuration - 2):F1}:d=2" +  // 2s fade-out
                $"[amb_faded];" +
                $"[0:a][amb_faded]amix=inputs=2:duration=first[out]";

            string args =
                $"-nostdin " +
                $"-i \"{vocalPath}\" " +
                $"-stream_loop -1 -i \"{ambientPath}\" " +
                $"-filter_complex \"{filter}\" " +
                $"-map \"[out]\" -c:a aac -b:a 256k " +
                $"-t {dur} -y \"{outputPath}\"";

            bool ok = await RunFFmpeg(ffmpegPath, args, ct);
            return ok && File.Exists(outputPath) ? outputPath : vocalPath;
        }

        private static async Task<bool> RunFFmpeg(
            string ffmpegPath, string args, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName              = ffmpegPath,
                    Arguments             = args,
                    CreateNoWindow        = true,
                    UseShellExecute       = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. SMART CROP LOGIC
    //    Analizira video i pronalazi najaktivniji region
    //    za Ken Burns efekat usmjeren ka pravom subjektu
    // ═══════════════════════════════════════════════════════════════

    public static class SmartCrop
    {
        public class CropRegion
        {
            public int X         { get; set; }
            public int Y         { get; set; }
            public int Width     { get; set; }
            public int Height    { get; set; }
            public double Score  { get; set; }  // 0-1, viši = aktivniji
            public string Zone   { get; set; }  // "left", "center", "right", "top", "bottom"
        }

        /// <summary>
        /// Analizira video i vraća najaktivniji region.
        /// Koristi samo cropdetect — motion analiza je uklonjena jer je bila
        /// sporija, pisala u temp fajl, i vraćala samo Zone="center" bez
        /// korisnih koordinata (što je pogrešno pobijalo cropdetect koji ima prave X/Y).
        /// </summary>
        public static async Task<CropRegion> AnalyzeVideo(
            string videoPath,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(videoPath) || !File.Exists(ffmpegPath))
                return DefaultCrop();

            try
            {
                return await RunCropDetect(videoPath, ffmpegPath, ct);
            }
            catch { return DefaultCrop(); }
        }

        /// <summary>
        /// Generiše FFmpeg Ken Burns filter usmjeren ka aktivnom regionu.
        ///
        /// KLJUČNA ISPRAVKA — trim pre zoompan-a:
        /// Bez trim filtera, zoompan ignorise -t ogranicenje i procesira
        /// ceo ulazni video (npr. 52s Pixabay klip) frejm po frejm na CPU,
        /// cak i ako trazimo samo 7s izlaza. Sa trim=duration=X, FFmpeg
        /// prestaje da cita ulaz nakon X sekundi — dramaticno ubrzanje.
        /// </summary>
        public static string BuildKenBurnsFilter(
            CropRegion region,
            int targetW, int targetH,
            int sourceW, int sourceH,
            double duration,
            string style = "zoom_in")
        {
            // Zona određuje smjer kretanja
            string zone = region.Zone ?? "center";

            double zoomStart, zoomEnd;
            string panDir;

            switch (style)
            {
                case "zoom_in":
                    zoomStart = 1.0;
                    zoomEnd   = 1.15;
                    panDir    = zone switch
                    {
                        "left"   => "left",
                        "right"  => "right",
                        "top"    => "up",
                        "bottom" => "down",
                        _        => "none"
                    };
                    break;
                case "zoom_out":
                    zoomStart = 1.15;
                    zoomEnd   = 1.0;
                    panDir    = "none";
                    break;
                case "pan":
                    zoomStart = 1.05;
                    zoomEnd   = 1.05;
                    panDir    = zone switch
                    {
                        "left"  => "right",
                        "right" => "left",
                        _       => "right"
                    };
                    break;
                default:
                    zoomStart = 1.0;
                    zoomEnd   = 1.1;
                    panDir    = "none";
                    break;
            }

            double fps    = 25.0;
            int    frames = (int)(duration * fps);
            if (frames < 1) frames = 1;
            double zStep  = (zoomEnd - zoomStart) / frames;

            // x offset baziran na zoni aktivnosti
            string xExpr = panDir switch
            {
                "left"  => $"if(gte(iw*on/{frames},iw),iw,iw*on/{frames})",
                "right" => $"if(lte(iw-iw*on/{frames},0),0,iw-iw*on/{frames})",
                _       => "(iw-iw/zoom)/2"
            };

            // y offset
            string yExpr = panDir switch
            {
                "up"   => $"if(gte(ih*on/{frames},ih),ih,ih*on/{frames})",
                "down" => "0",
                _      => "(ih-ih/zoom)/2"
            };

            string durStr = duration.ToString("F3", CultureInfo.InvariantCulture);

            // FIX A: trim+setpts PRE zoompan-a
            // Bez ovoga, zoompan bi citao ceo ulazni video (npr. 52s original)
            // frejm po frejm, ignoriseci -t ogranicenje spolja.
            // trim=duration=X tera FFmpeg da prestane citati ulaz nakon X sekundi,
            // setpts=PTS-STARTPTS resetuje timestamps da zoompan ne bude zbunjen.
            // FIX: nema s=WxH unutar zoompan-a — scale dolazi posle kao poseban filter
            // FIX: scale koristi lanczos za kvalitetan upscale na 4K
            return $"trim=duration={durStr},setpts=PTS-STARTPTS," +
                   $"zoompan=" +
                   $"z='min(zoom+{zStep.ToString("F6", CultureInfo.InvariantCulture)},{zoomEnd.ToString("F2", CultureInfo.InvariantCulture)})':" +
                   $"x='{xExpr}':" +
                   $"y='{yExpr}':" +
                   $"d={frames}:" +
                   $"fps={fps}," +
                   $"scale={targetW}:{targetH}:flags=lanczos";
        }

        // ── Privatne metode ───────────────────────────────────────────

        private static async Task<CropRegion> RunCropDetect(
            string videoPath, string ffmpegPath, CancellationToken ct)
        {
            // FIX B: -t 3 umesto -t 10 — 3 sekunde su dovoljne za cropdetect,
            // a FFmpeg mora dekodovati te sekunde na CPU bez akceleracije.
            // Na 4K videu razlika je 3x manje posla pre prve analize.
            string args = $"-nostdin -t 3 -i \"{videoPath}\" " +
                          $"-vf \"cropdetect=24:16:0\" " +
                          $"-f null -";

            string output = await RunFFmpegGetOutput(ffmpegPath, args, ct);

            // Parsiraj POSLEDNJI crop=W:H:X:Y iz outputa
            // (poslednji je najtačniji — cropdetect se stabilizuje tokom analize)
            var matches = System.Text.RegularExpressions.Regex.Matches(
                output, @"crop=(\d+):(\d+):(\d+):(\d+)");

            if (matches.Count > 0)
            {
                var m = matches[matches.Count - 1]; // uzmi poslednji
                int w = int.Parse(m.Groups[1].Value);
                int h = int.Parse(m.Groups[2].Value);
                int x = int.Parse(m.Groups[3].Value);
                int y = int.Parse(m.Groups[4].Value);

                // FIX C: Normalizuj zonu na stvarnu sirinu/visinu videa, ne na hardkodovano 1920x1080.
                // Pixabay videi mogu biti 1280x720, 1920x1080 ili nesto drugo —
                // poredenje X sa 0.33*1920=634 je pogresno za 720p video gde je
                // max X=1280, pa svaki X ispod 634 pada u "left" (gotovo uvek).
                string zone;
                if      (x < w * 0.33)  zone = "left";
                else if (x > w * 0.66)  zone = "right";
                else if (y < h * 0.33)  zone = "top";
                else if (y > h * 0.66)  zone = "bottom";
                else                    zone = "center";

                // FIX D: cropdetect Score = 0.8 (viši od motion analize koja je uklonjena).
                // Ima konkretne koordinate X/Y — vredniji je od pretpostavke "center".
                return new CropRegion
                {
                    Width  = w,
                    Height = h,
                    X      = x,
                    Y      = y,
                    Score  = 0.8,
                    Zone   = zone
                };
            }

            return DefaultCrop();
        }

        private static CropRegion DefaultCrop() =>
            new() { X = 0, Y = 0, Width = 1920, Height = 1080,
                    Score = 0.3, Zone = "center" };

        private static async Task<string> RunFFmpegGetOutput(
            string ffmpegPath, string args, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = ffmpegPath,
                    Arguments              = args,
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true
                };
                var sb = new StringBuilder();
                using var proc = Process.Start(psi);
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(ct);
                return sb.ToString();
            }
            catch { return ""; }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. MULTI-FORMAT EXPORT
    //    16:9 → 9:16 (Shorts/TikTok/Reels)
    //    Pametni crop + Blur Padding fallback
    // ═══════════════════════════════════════════════════════════════

    public static class MultiFormatExport
    {
        public enum VerticalMode
        {
            SmartCropCenter,   // Iseci centralni dio
            BlurPadding,       // Blur pozadina + original u sredini
            SmartCropFace,     // Pokušaj da nađe lice/subjekat (cropdetect)
        }

        /// <summary>
        /// Konvertuje 16:9 video u 9:16 za Shorts/TikTok/Reels.
        /// </summary>
        public static async Task<string> ExportVertical(
            string inputPath,
            string outputPath,
            string ffmpegPath,
            VerticalMode mode = VerticalMode.BlurPadding,
            int targetW = 1080,
            int targetH = 1920,
            CancellationToken ct = default)
        {
            if (!File.Exists(inputPath) || !File.Exists(ffmpegPath))
                return null;

            string filter = mode switch
            {
                VerticalMode.SmartCropCenter => BuildCenterCropFilter(targetW, targetH),
                VerticalMode.SmartCropFace   => BuildSmartCropFilter(targetW, targetH),
                _                            => BuildBlurPaddingFilter(targetW, targetH)
            };

            string args =
                $"-nostdin -i \"{inputPath}\" " +
                $"-vf \"{filter}\" " +
                $"-c:v libx264 -preset fast -crf 20 " +
                $"-c:a copy -pix_fmt yuv420p " +
                $"-y \"{outputPath}\"";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName              = ffmpegPath,
                    Arguments             = args,
                    CreateNoWindow        = true,
                    UseShellExecute       = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0 ? outputPath : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Generiše sve formate odjednom:
        /// - Original 16:9
        /// - Shorts 9:16 (BlurPadding)
        /// - Square 1:1 (za Instagram)
        /// </summary>
        public static async Task<ExportResults> ExportAllFormats(
            string inputPath,
            string outputDir,
            string ffmpegPath,
            string baseName,
            CancellationToken ct = default)
        {
            var results = new ExportResults();
            results.Original = inputPath;

            // 9:16 Shorts/TikTok sa blur padding
            string shortsPath = Path.Combine(outputDir, $"{baseName}_shorts_9x16.mp4");
            results.Shorts = await ExportVertical(
                inputPath, shortsPath, ffmpegPath,
                VerticalMode.BlurPadding, 1080, 1920, ct);

            // 1:1 Instagram
            if (!ct.IsCancellationRequested)
            {
                string squarePath = Path.Combine(outputDir, $"{baseName}_instagram_1x1.mp4");
                results.Square = await ExportSquare(inputPath, squarePath, ffmpegPath, ct);
            }

            return results;
        }

        // ── Filter graditelji ────────────────────────────────────────

        private static string BuildBlurPaddingFilter(int w, int h)
        {
            // Blur padding: original video se skalira na 9:16
            // blur kopija popunjava crne trake
            // Rezultat: nema crnih traka, punopravni 9:16 video
            return
                // Split na 2 streama
                $"split=2[bg][fg];" +
                // Pozadina: skalira na 9:16, bluruje jako
                $"[bg]scale={w}:{h}:force_original_aspect_ratio=increase," +
                $"crop={w}:{h}," +
                $"boxblur=luma_radius=40:luma_power=3[blurred];" +
                // Foreground: skalira da stane unutar 9:16 bez reza
                $"[fg]scale={w}:{h}:force_original_aspect_ratio=decrease[scaled];" +
                // Overlay: centered foreground na blurred background
                $"[blurred][scaled]overlay=" +
                $"x=(W-w)/2:" +
                $"y=(H-h)/2";
        }

        private static string BuildCenterCropFilter(int w, int h)
        {
            // Jednostavan center crop - uzme sredinu 16:9 videa
            // i uklapa u 9:16
            return $"crop=ih*{w}/{h}:ih:(iw-ih*{w}/{h})/2:0," +
                   $"scale={w}:{h}";
        }

        private static string BuildSmartCropFilter(int w, int h)
        {
            // Smart crop: cropdetect + scale
            // Bolje od čistog centra ali bez AI face detection
            return $"cropdetect=24:16:0," +
                   $"crop=ih*{w}/{h}:ih," +
                   $"scale={w}:{h}";
        }

        private static async Task<string> ExportSquare(
            string inputPath, string outputPath,
            string ffmpegPath, CancellationToken ct)
        {
            // 1:1 crop centra
            string filter = "crop=ih:ih:(iw-ih)/2:0,scale=1080:1080";
            string args =
                $"-nostdin -i \"{inputPath}\" " +
                $"-vf \"{filter}\" " +
                $"-c:v libx264 -preset fast -crf 20 " +
                $"-c:a copy -pix_fmt yuv420p " +
                $"-y \"{outputPath}\"";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath, Arguments = args,
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0 ? outputPath : null;
            }
            catch { return null; }
        }
    }

    public class ExportResults
    {
        public string Original { get; set; }
        public string Shorts   { get; set; }  // 9:16
        public string Square   { get; set; }  // 1:1
        public bool HasShorts  => !string.IsNullOrEmpty(Shorts)   && File.Exists(Shorts);
        public bool HasSquare  => !string.IsNullOrEmpty(Square)   && File.Exists(Square);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. VISION AI - LLaVA / Moondream opisi klipova
    //    Šalje sliku/frame Ollama vision modelu
    //    i vraća čitljivi opis za screen reader (JAWS)
    // ═══════════════════════════════════════════════════════════════

    public static class VisionAI
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        /// <summary>
        /// Generiše opis slike ili video klipa koristeći Ollama vision model.
        /// Podržava: LLaVA, Moondream, BakLLaVA, LLaVA-Phi3
        /// </summary>
        public static async Task<string> DescribeMedia(
            string mediaPath,
            string ffmpegPath,
            string ollamaModel = "moondream",  // moondream je najbrži
            CancellationToken ct = default)
        {
            if (!File.Exists(mediaPath)) return "Fajl ne postoji.";

            try
            {
                // Ako je video, izvuci frame na 25% trajanja
                string imagePath = mediaPath;
                bool   tempFrame = false;

                string ext = Path.GetExtension(mediaPath).ToLower();
                if (ext is ".mp4" or ".avi" or ".mov" or ".mkv")
                {
                    imagePath = await ExtractFrame(mediaPath, ffmpegPath, ct);
                    tempFrame = true;
                    if (string.IsNullOrEmpty(imagePath))
                        return "Nije moguce izvuci frame iz videa.";
                }

                // Konvertuj sliku u base64
                byte[] imgBytes = await File.ReadAllBytesAsync(imagePath, ct);
                string base64   = Convert.ToBase64String(imgBytes);

                if (tempFrame && File.Exists(imagePath))
                    File.Delete(imagePath);

                // Pošalji Ollama vision modelu
                string description = await QueryVisionModel(
                    base64, ollamaModel, ct);

                return string.IsNullOrEmpty(description)
                    ? "Opis nije dostupan."
                    : description;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return $"Greska pri opisu: {ex.Message}";
            }
        }

        /// <summary>
        /// Batch opisivanje - generiše opise za sve klipove na timeline-u.
        /// Poziva callback sa svakim opisom (za JAWS live region).
        /// </summary>
        public static async Task DescribeAllClips(
            System.Collections.Generic.List<TimelineItem> items,
            string ffmpegPath,
            string ollamaModel,
            Action<int, string> onProgress,  // (index, description)
            CancellationToken ct = default)
        {
            // Provjeri da li Ollama radi
            if (!await IsVisionModelAvailable(ollamaModel))
            {
                onProgress(-1, $"Ollama vision model '{ollamaModel}' nije dostupan. " +
                    $"Pokrenite: ollama pull {ollamaModel}");
                return;
            }

            int count = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                if (item.IsAudio) continue;
                if (!string.IsNullOrEmpty(item.AudioDescription) &&
                    item.AudioDescription.Length > 20)
                    continue; // Već ima opis

                string desc = await DescribeMedia(item.Path, ffmpegPath, ollamaModel, ct);

                // Skrati opis na razumnu dužinu za JAWS
                desc = TrimDescription(desc, item.Name);
                item.AudioDescription = desc;

                count++;
                onProgress(count, $"Klip {count}: {item.Name} — {desc}");

                // Mali delay između zahtjeva
                await Task.Delay(200, ct);
            }

            onProgress(-2, $"Opisivanje završeno: {count} klipova opisano.");
        }

        /// <summary>
        /// Provjeri da li je vision model dostupan u Ollami.
        /// </summary>
        public static async Task<bool> IsVisionModelAvailable(string model)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = await client.GetAsync("http://localhost:11434/api/tags");
                if (!resp.IsSuccessStatusCode) return false;
                string body = await resp.Content.ReadAsStringAsync();
                return body.Contains(model);
            }
            catch { return false; }
        }

        /// <summary>
        /// Lista preporučenih vision modela sa opisima.
        /// </summary>
        public static readonly (string model, string desc, string pullCmd)[] RecommendedModels =
        {
            ("moondream",    "Najbrži (1.7B), odličan za opise slika",   "ollama pull moondream"),
            ("llava",        "Balansiran (7B), detaljan opis scena",      "ollama pull llava"),
            ("llava-phi3",   "Precizan (4B), dobar za ljude i akciju",    "ollama pull llava-phi3"),
            ("bakllava-1",   "Specijalizovan za vizuelni sadržaj",        "ollama pull bakllava-1"),
            ("llava:13b",    "Najprecizniji, sporiji (13B)",              "ollama pull llava:13b"),
        };

        // ── Privatne metode ───────────────────────────────────────────

        private static async Task<string> ExtractFrame(
            string videoPath, string ffmpegPath, CancellationToken ct)
        {
            string outPath = Path.Combine(
                Path.GetTempPath(),
                $"frame_{Guid.NewGuid().ToString().Substring(0,8)}.jpg");

            // Uzmi frame na 25% trajanja videa
            string args =
                $"-nostdin -i \"{videoPath}\" " +
                $"-vf \"select='eq(n,1)',scale=512:288\" " +
                $"-vframes 1 -q:v 3 -y \"{outPath}\"";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName              = ffmpegPath,
                    Arguments             = args,
                    CreateNoWindow        = true,
                    UseShellExecute       = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                await proc.WaitForExitAsync(ct);
                return File.Exists(outPath) ? outPath : null;
            }
            catch { return null; }
        }

        private static async Task<string> QueryVisionModel(
            string base64Image,
            string model,
            CancellationToken ct)
        {
            // Ollama vision API - šalje base64 sliku
            var request = new
            {
                model  = model,
                prompt = "Describe this image in one short sentence, focusing on " +
                         "the main subject, action, and mood. Be specific. " +
                         "Reply in the same language as this instruction (English). " +
                         "Maximum 20 words.",
                images = new[] { base64Image },
                stream = false,
                options = new { temperature = 0.1, num_predict = 60 }
            };

            string json    = Newtonsoft.Json.JsonConvert.SerializeObject(request);
            var    content = new System.Net.Http.StringContent(
                json, Encoding.UTF8, "application/json");

            var resp = await _http.PostAsync(
                "http://localhost:11434/api/generate", content, ct);

            if (!resp.IsSuccessStatusCode) return null;

            string body   = await resp.Content.ReadAsStringAsync(ct);
            var    parsed = JObject.Parse(body);
            return parsed["response"]?.ToString()?.Trim();
        }

        private static string TrimDescription(string desc, string fallbackName)
        {
            if (string.IsNullOrEmpty(desc)) return fallbackName;

            // Ukloni nepotrebne fraze
            desc = desc.Replace("This image shows ", "")
                       .Replace("The image depicts ", "")
                       .Replace("In this image, ", "")
                       .Replace("I can see ", "")
                       .Trim();

            // Ograniči na 100 karaktera
            if (desc.Length > 100)
                desc = desc.Substring(0, 97) + "...";

            return desc;
        }
    }
}
