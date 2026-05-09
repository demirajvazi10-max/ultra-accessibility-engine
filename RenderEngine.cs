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

        private static string _LangCode => (WpfApp.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

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
            LogToMainWindow(L("re_starting"));

            bool nvencAvailable = false;
            if (useGPU)
            {
                try
                {
                    string testArgs = $"-f lavfi -i color=c=black:s=64x64:d=0.1 -c:v h264_nvenc -f null -";
                    string testOut = await RunFFmpegGetOutputAsync(testArgs, CancellationToken.None);
                    nvencAvailable = !testOut.Contains("No NVENC capable devices") &&
                                      !testOut.Contains("Cannot load") &&
                                      !testOut.Contains("Unknown encoder");
                }
                catch { nvencAvailable = false; }
            }

            // DODATO ZA WINDOWS MEDIA PLAYER: -pix_fmt yuv420p -profile:v high -level 4.1
            string vEncArgs = nvencAvailable
                ? "-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0 -profile:v high -level 4.1"
                : "-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1";
            string pixFmt = "-pix_fmt yuv420p";

            const string TARGET_FPS = "30";
            const string VSYNC_CFR = "-vsync cfr";
            string fpsSuffix = $",fps={TARGET_FPS}";
            LogToMainWindow($"RenderEngine: Enkoder: {(nvencAvailable ? "h264_nvenc (GPU)" : "libx264 (CPU)")} | FastRender={fastRender} | FPS={TARGET_FPS} CFR");
            vEncArgs_cached = vEncArgs;

            if (!File.Exists(_ffmpegPath))
            {
                LogToMainWindow(L("re_ffmpeg_missing"));
                throw new FileNotFoundException(LF("re_ffmpeg_path", _ffmpegPath));
            }

            var sortedItems = items.OrderBy(i => i.Start).ToList();

            LogToMainWindow($"RenderEngine: Ukupno klipova prije filtriranja: {sortedItems.Count}");

            foreach (var item in sortedItems)
            {
                LogToMainWindow($"  Klip: Type={item.Type}, Name={item.Name}, Path={(string.IsNullOrEmpty(item.Path) ? "EMPTY" : item.Path)}");
            }

            var allImageItems = sortedItems.Where(i => i.Type == "Image").ToList();
            LogToMainWindow($"RenderEngine: Ukupno Image klipova: {allImageItems.Count}");

            var images = allImageItems.Where(i =>
                !string.IsNullOrEmpty(i.Path) &&
                File.Exists(i.Path) &&
                !i.Name.Contains("Najavni") &&
                !i.Name.Contains("Odjavni")).ToList();

            var textImages = allImageItems.Where(i =>
                string.IsNullOrEmpty(i.Path) ||
                i.Name.Contains("Najavni") ||
                i.Name.Contains("Odjavni")).ToList();

            var audio = sortedItems
                .Where(i => (i.Type == "Audio" || i.IsAudio) &&
                            !i.Name.StartsWith("🔊"))
                .OrderByDescending(i => i.Duration)
                .FirstOrDefault()
                ?? sortedItems.FirstOrDefault(i => i.Type == "Audio" || i.IsAudio);
            var videos = sortedItems.Where(i => i.Type == "Video" || i.IsVideo).ToList();

            LogToMainWindow(LF("re_found_media", images.Count, textImages.Count, videos.Count));
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
                var videoFiles = new List<string>();
                var itemToFile = new Dictionary<TimelineItem, string>();

                int total = sortedItems.Count(i => i.Type == "Image" || i.Type == "Video" || i.IsVideo || i.IsAudio == false);
                int current = 0;
                int fileIdx = 0;

                foreach (var item in sortedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool isVideo = item.Type == "Video" || item.IsVideo;
                    bool isImage = item.Type == "Image";
                    bool isAudio = item.Type == "Audio" || item.IsAudio;
                    bool isTextItem = isImage && (string.IsNullOrEmpty(item.Path) ||
                                                  item.Name.Contains("Najavni") ||
                                                  item.Name.Contains("Odjavni") ||
                                                  (item.Path != null && !File.Exists(item.Path)));

                    if (isAudio) continue;

                    string tempVideo = Path.Combine(tempDir, $"clip_{fileIdx++:D4}.mp4");
                    string durationStr = item.Duration.ToString(CultureInfo.InvariantCulture);
                    bool success = false;

                    if (isTextItem)
                    {
                        string displayText = !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path)
                            ? ExtractTextFromName(item.Name)
                            : item.Name;
                        LogToMainWindow($"RenderEngine: Tekstualni sloj '{item.Name}' (Start={item.Start:F1}s, trajanje: {item.Duration:F2}s)");

                        string escapedText = EscapeText(displayText);
                        int fontSize = item.Name.Contains("Najavni") ? 60 : 34;

                        if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                        {
                            string preparedImage = await PrepareImageWithMagick(item.Path, tempDir);
                            if (preparedImage != null)
                            {
                                string scaleF = $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=1,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2{fpsSuffix}";
                                string argsImg = $"-nostdin -loop 1 -r {TARGET_FPS} -i \"{preparedImage}\" {vEncArgs} -t {durationStr} {VSYNC_CFR} {pixFmt} -y \"{tempVideo}\"";
                                success = await RunFFmpegAsync(argsImg, cancellationToken);
                            }
                        }

                        if (!success)
                        {
                            string argsText = $"-nostdin -f lavfi -i color=c=black:s={targetWidth}x{targetHeight}:r={TARGET_FPS}:d={durationStr} " +
                                              $"-vf \"drawtext=text='{escapedText}':fontcolor=white:fontsize={fontSize}:x=(w-text_w)/2:y=(h-text_h)/2{fpsSuffix}\" " +
                                              $"{vEncArgs} {VSYNC_CFR} {pixFmt} -y \"{tempVideo}\"";
                            success = await RunFFmpegAsync(argsText, cancellationToken);
                        }
                    }
                    else if (isImage)
                    {
                        if (!File.Exists(item.Path)) continue;

                        string preparedImage = await PrepareImageWithMagick(item.Path, tempDir);
                        if (preparedImage == null) continue;

                        string scaleF = $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=1,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2";

                        string fadeFilter = "";
                        bool isNaslov = item.Name.StartsWith("Naslov:");
                        bool isOdjava = item.Name == "Odjavni tekst";
                        double dur = item.Duration;

                        if (isNaslov)
                        {
                            fadeFilter = $",fade=t=in:st=0:d=0.8," +
                                         $"fade=t=out:st={Math.Max(0, dur - 0.5):F2}:d=0.5";
                        }
                        else if (isOdjava)
                        {
                            fadeFilter = $",fade=t=in:st=0:d=0.5," +
                                         $"fade=t=out:st={Math.Max(0, dur - 1.5):F2}:d=1.5";
                        }

                        string imgVf = scaleF + fadeFilter + fpsSuffix;
                        string argsImg = $"-nostdin -loop 1 -r {TARGET_FPS} -i \"{preparedImage}\" {vEncArgs} -t {durationStr} {VSYNC_CFR} {pixFmt} -vf \"{imgVf}\" -y \"{tempVideo}\"";
                        success = await RunFFmpegAsync(argsImg, cancellationToken);
                    }
                    else if (isVideo)
                    {
                        if (!File.Exists(item.Path)) continue;

                        string scaleFilter = $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=1,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2";
                        string baseNormalize = "eq=brightness=0.04:saturation=1.1:contrast=1.02,format=yuv420p";

                        string moodTag = item.AudioDescription ?? "";
                        string moodFilter = ExtractTag(moodTag, "mood") != "" ? "eq=saturation=1.10:brightness=0.02:contrast=1.03,curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'" : "";

                        string videoVf = string.IsNullOrEmpty(moodFilter)
                            ? $"{scaleFilter},{baseNormalize}"
                            : $"{scaleFilter},{baseNormalize},{moodFilter}";

                        int sceneEnergy = 3;
                        string audioDesc2 = item.AudioDescription ?? "";
                        var energyMatch = System.Text.RegularExpressions.Regex.Match(audioDesc2, @"energy=(\d+)");
                        if (energyMatch.Success)
                        {
                            int parsed;
                            if (int.TryParse(energyMatch.Groups[1].Value, out parsed))
                                sceneEnergy = parsed;
                        }

                        bool isStaticClip = audioDesc2.Contains("static=1");
                        double anchorBrightness = -1;
                        var anchorMatch = System.Text.RegularExpressions.Regex.Match(audioDesc2, @"anchor_brightness=([\d.]+)");
                        if (anchorMatch.Success && double.TryParse(anchorMatch.Groups[1].Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double abv))
                            anchorBrightness = abv;

                        double overscanFactor = sceneEnergy >= 4 ? 1.20 : sceneEnergy <= 2 ? 1.10 : 1.15;

                        string warmthBoost;
                        if (sceneEnergy >= 4)
                            warmthBoost = "curves=r='0/0 0.5/0.56 1/1':g='0/0 0.5/0.54 1/1'";
                        else if (sceneEnergy <= 2)
                            warmthBoost = "curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'";
                        else
                            warmthBoost = "curves=r='0/0 0.5/0.54 1/1':g='0/0 0.5/0.525 1/1'";

                        bool needsSlowMoFix = audioDesc2.Contains("slowmo=1");
                        string fpsNormalize = needsSlowMoFix
                            ? ",minterpolate=fps=30:mi_mode=mci:mc_mode=aobmc:me=hexbs:vsbmc=1"
                            : "";

                        string colorMatchFilter = "";
                        if (anchorBrightness > 0)
                        {
                            double currentBrightness = 0.5;
                            double delta = Math.Max(-0.12, Math.Min(0.12, anchorBrightness - currentBrightness));
                            if (Math.Abs(delta) > 0.03)
                            {
                                double satAdj = delta > 0 ? 1.05 : 0.97;
                                colorMatchFilter = $",eq=brightness={delta.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:saturation={satAdj.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}";
                            }
                        }

                        baseNormalize = $"{baseNormalize},{warmthBoost}";
                        videoVf = string.IsNullOrEmpty(moodFilter)
                            ? $"{scaleFilter},{baseNormalize}{colorMatchFilter}{fpsNormalize}"
                            : $"{scaleFilter},{baseNormalize},{moodFilter}{colorMatchFilter}{fpsNormalize}";

                        if (!fastRender)
                        {
                            try
                            {
                                if (isStaticClip && item.Duration >= 2.0)
                                {
                                    int staticFrames = Math.Max(1, (int)(item.Duration * 25.0));
                                    int overWs = (int)(targetWidth * 1.12);
                                    int overHs = (int)(targetHeight * 1.12);
                                    if (overWs % 2 != 0) overWs++;
                                    if (overHs % 2 != 0) overHs++;
                                    int midXs = (overWs - targetWidth) / 2;
                                    int midYs = (overHs - targetHeight) / 2;
                                    string staticKB = $"scale={overWs}:{overHs}:flags=lanczos," +
                                                      $"crop={targetWidth}:{targetHeight}:" +
                                                      $"{midXs}*n/{staticFrames}:{midYs}*n/{staticFrames}";
                                    videoVf = string.IsNullOrEmpty(moodFilter)
                                        ? $"{staticKB},{baseNormalize}{colorMatchFilter}"
                                        : $"{staticKB},{baseNormalize},{moodFilter}{colorMatchFilter}";
                                }

                                if (item.Duration >= 3.0 && item.Duration <= 12.0 && !isStaticClip)
                                {
                                    int overW = (int)(targetWidth * overscanFactor);
                                    int overH = (int)(targetHeight * overscanFactor);
                                    if (overW % 2 != 0) overW++;
                                    if (overH % 2 != 0) overH++;

                                    int frames = Math.Max(1, (int)(item.Duration * 25.0));
                                    int maxX = overW - targetWidth;
                                    int maxY = overH - targetHeight;
                                    int midX = maxX / 2;
                                    int midY = maxY / 2;

                                    string zone = "center";
                                    string xExpr = $"{midX}";
                                    string yExpr = $"{midY}";

                                    string kenBurnsGpu =
                                        $"scale={overW}:{overH}:flags=lanczos," +
                                        $"crop={targetWidth}:{targetHeight}:{xExpr}:{yExpr}";

                                    videoVf = kenBurnsGpu;
                                }
                            }
                            catch { }
                        }

                        bool isZoompan = videoVf.Contains("zoompan");
                        string argsVid;

                        if (isZoompan)
                        {
                            argsVid = $"-nostdin -t {durationStr} -i \"{item.Path}\" -vf \"{videoVf}{fpsSuffix}\" {vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{tempVideo}\"";
                        }
                        else
                        {
                            argsVid = $"-nostdin -stream_loop -1 -t {durationStr} -i \"{item.Path}\" -vf \"{videoVf}{fpsSuffix}\" {vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{tempVideo}\"";
                        }

                        success = await RunFFmpegAsync(argsVid, cancellationToken);
                    }
                    else
                    {
                        continue;
                    }

                    if (success)
                    {
                        videoFiles.Add(tempVideo);
                        itemToFile[item] = tempVideo;
                    }

                    current++;
                    progress?.Report(Math.Min(85, current * 85 / Math.Max(1, total)));
                }

                if (videoFiles.Count == 0)
                    throw new Exception(L("re_no_media"));

                string concatFile = Path.Combine(tempDir, "concat.txt");
                using (var sw = new StreamWriter(concatFile, false, new UTF8Encoding(false)))
                {
                    foreach (var vf in videoFiles)
                        await sw.WriteLineAsync($"file '{vf.Replace("\\", "/")}'");
                }

                string crossfadedVideo = null;
                if (videoFiles.Count > 1)
                {
                    try
                    {
                        crossfadedVideo = Path.Combine(tempDir, "crossfaded.mp4");
                        crossfadedVideo = await ApplyCrossfade(videoFiles, crossfadedVideo, 0.3, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        LogToMainWindow(LF("re_crossfade_fail", ex.Message));
                        crossfadedVideo = null;
                    }
                }

                string finalOutput = outputPath;
                string argsFinal;

                if (audio != null && File.Exists(audio.Path))
                {
                    string tempAudioPath = Path.Combine(tempDir, "audio" + Path.GetExtension(audio.Path));
                    File.Copy(audio.Path, tempAudioPath, true);

                    var ambientItem = items.FirstOrDefault(i =>
                        !string.IsNullOrEmpty(i.AmbientSoundPath) &&
                        File.Exists(i.AmbientSoundPath));

                    if (ambientItem != null)
                    {
                        string mixedAudio = Path.Combine(tempDir, "mixed_audio.aac");
                        string mixedPath = tempAudioPath;
                        tempAudioPath = mixedPath;
                    }

                    var secondaryAudioClips = sortedItems
                        .Where(i => i.Type == "Audio" &&
                                    i != audio &&
                                    !string.IsNullOrEmpty(i.Path) &&
                                    File.Exists(i.Path))
                        .OrderBy(i => i.Start)
                        .GroupBy(i => Math.Round(i.Start, 1))
                        .Select(g => g.First())
                        .Take(50)
                        .ToList();

                    if (secondaryAudioClips.Count > 0)
                    {
                        string mixedWithTransitions = Path.Combine(tempDir, "audio_with_transitions.aac");
                        string mixResult = await MixSecondaryAudioClips(
                            tempAudioPath,
                            secondaryAudioClips,
                            mixedWithTransitions,
                            cancellationToken);
                        if (mixResult != null)
                        {
                            tempAudioPath = mixResult;
                        }
                    }

                    string subtitleFile = await CreateSubtitlesFile(subtitles, tempDir);
                    if (!string.IsNullOrEmpty(subtitleFile) && File.Exists(subtitleFile))
                    {
                        string escapedSub = subtitleFile.Replace("\\", "/").Replace(":", "\\:");
                        // DODATO ZA WINDOWS MEDIA PLAYER: -pix_fmt yuv420p -profile:v high -level 4.1
                        argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" -i \"{tempAudioPath}\" " +
                                    $"-vf \"subtitles='{escapedSub}':force_style='FontSize=22,PrimaryColour=&H00FFFF00,OutlineColour=&H00000000,Outline=2,Shadow=1,Alignment=2'\" " +
                                    $"-c:v libx264 -preset veryfast -crf 20 -profile:v high -level 4.1 -pix_fmt yuv420p -c:a aac -map 0:v -map 1:a -shortest -y \"{finalOutput}\"";
                    }
                    else
                    {
                        if (crossfadedVideo != null && File.Exists(crossfadedVideo))
                            argsFinal = $"-nostdin -i \"{crossfadedVideo}\" -i \"{tempAudioPath}\" -c:v copy -c:a aac -map 0:v -map 1:a -shortest -y \"{finalOutput}\"";
                        else
                            argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" -i \"{tempAudioPath}\" -c:v copy -c:a aac -map 0:v -map 1:a -shortest -y \"{finalOutput}\"";
                    }
                }
                else
                {
                    if (crossfadedVideo != null && File.Exists(crossfadedVideo))
                        argsFinal = $"-nostdin -i \"{crossfadedVideo}\" -c:v copy -y \"{finalOutput}\"";
                    else
                        argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" -c:v copy -y \"{finalOutput}\"";
                }

                await RunFFmpegAsync(argsFinal, cancellationToken);

                if (File.Exists(finalOutput))
                {
                    string postOutput = finalOutput.Replace(".mp4", "_post.mp4");
                    try
                    {
                        double finalDur = await GetVideoDuration(finalOutput, cancellationToken);
                        if (finalDur > 4.0)
                        {
                            double fadeStart = Math.Max(0, finalDur - 2.0);
                            string fadeStartStr = fadeStart.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

                            // DODATO ZA WINDOWS MEDIA PLAYER
                            string encArgsPost = vEncArgs_cached ?? "-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1";
                            string postArgs = "-nostdin -i \"" + finalOutput + "\" " +
                                "-vf \"fade=t=out:st=" + fadeStartStr + ":d=2," +
                                "colorchannelmixer=rr=1:rb=-0.02:gr=0:gg=1:gb=-0.02:br=-0.03:bg=-0.03:bb=1.06\" " +
                                "-af \"afade=t=out:st=" + fadeStartStr + ":d=2\" " +
                                $"{encArgsPost} -pix_fmt yuv420p -c:a aac -y \"{postOutput}\"";

                            bool postOk = await RunFFmpegAsync(postArgs, cancellationToken);
                            if (postOk && File.Exists(postOutput) && new FileInfo(postOutput).Length > 100_000)
                            {
                                File.Delete(finalOutput);
                                File.Move(postOutput, finalOutput);
                            }
                        }
                    }
                    catch { }
                }

                progress?.Report(100);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        private async Task<double> GetVideoDuration(string videoPath, CancellationToken ct)
        {
            try
            {
                string probePath = _ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
                if (File.Exists(probePath))
                {
                    string dArgs = "-v error -select_streams v:0 -show_entries stream=duration -of default=noprint_wrappers=1:nokey=1 \"" + videoPath + "\"";
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = probePath,
                        Arguments = dArgs,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    var probeSo = proc.StandardOutput.ReadToEndAsync();
                    var probeSe = proc.StandardError.ReadToEndAsync();
                    await Task.WhenAll(probeSo, probeSe);
                    await proc.WaitForExitAsync(ct);
                    string output = probeSo.Result;
                    if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d))
                        return d;
                }

                var psi2 = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-nostdin -i \"" + videoPath + "\" -f null -",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc2 = System.Diagnostics.Process.Start(psi2);
                var _re1so = proc2.StandardOutput.ReadToEndAsync();
                var _re1se = proc2.StandardError.ReadToEndAsync();
                await Task.WhenAll(_re1so, _re1se);
                await proc2.WaitForExitAsync(ct);
                string stderr = _re1se.Result;
                var m = System.Text.RegularExpressions.Regex.Match(stderr, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                if (m.Success)
                    return int.Parse(m.Groups[1].Value) * 3600
                         + int.Parse(m.Groups[2].Value) * 60
                         + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return 0.0;
        }

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

                    bool ok = await RunFFmpegAsync(args, ct);

                    if (!ok || !File.Exists(batchOutput) || new FileInfo(batchOutput).Length < 1000)
                    {
                        continue;
                    }

                    if (currentAudio != mainAudioPath && File.Exists(currentAudio))
                        try { File.Delete(currentAudio); } catch { }

                    currentAudio = batchOutput;
                }

                if (currentAudio != outputPath && File.Exists(currentAudio))
                    File.Copy(currentAudio, outputPath, true);

                return File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000
                    ? outputPath : null;
            }
            catch (Exception ex)
            {
                LogToMainWindow(LF("re_mix_error", ex.Message));
                return null;
            }
        }

        private string ExtractTextFromName(string name)
        {
            if (name.Contains(":"))
            {
                return name.Substring(name.IndexOf(':') + 1).Trim();
            }
            if (name.StartsWith("Najavni tekst:"))
                return name.Substring("Najavni tekst:".Length).Trim();
            if (name.StartsWith("Odjavni tekst:"))
                return name.Substring("Odjavni tekst:".Length).Trim();
            return name;
        }

        private string EscapeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

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
                return srtFile;
            }
            catch (Exception ex)
            {
                LogToMainWindow(LF("re_subtitle_error", ex.Message));
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
                LogToMainWindow(LF("re_magick_error", imagePath, ex.Message));
                return null;
            }
        }

        private async Task<bool> RunFFmpegAsync(string arguments, CancellationToken ct)
        {
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

                if (process.ExitCode != 0)
                {
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        private async Task<string> RunFFmpegGetOutputAsync(string arguments, CancellationToken ct)
        {
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

            var _re3so = process.StandardOutput.ReadToEndAsync();
            var _re3se = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(_re3so, _re3se);
            await process.WaitForExitAsync(ct);
            string stderr = _re3se.Result;
            string stdout = _re3so.Result;

            return stderr + stdout;
        }

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

        private async Task<string> ApplyCrossfade(
            List<string> videoFiles,
            string outputPath,
            double fadeDuration,
            CancellationToken ct)
        {
            if (videoFiles.Count < 2) return null;

            var durations = new List<double>();
            foreach (var vf in videoFiles)
            {
                double dur = 5.0;
                try
                {
                    string probePath = _ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
                    bool useProbe = File.Exists(probePath);

                    if (useProbe)
                    {
                        string dArgs = $"-v error -select_streams v:0 " +
                                       $"-show_entries stream=duration " +
                                       $"-of default=noprint_wrappers=1:nokey=1 " +
                                       $"\"{vf}\"";
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = probePath,
                            Arguments = dArgs,
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        var probeSo2 = proc.StandardOutput.ReadToEndAsync();
                        var probeSe2 = proc.StandardError.ReadToEndAsync();
                        await Task.WhenAll(probeSo2, probeSe2);
                        await proc.WaitForExitAsync(ct);
                        string output = probeSo2.Result;
                        if (double.TryParse(output.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d))
                            dur = d;
                    }
                    else
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = _ffmpegPath,
                            Arguments = $"-nostdin -i \"{vf}\" -f null -",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        var _re4so = proc.StandardOutput.ReadToEndAsync();
                        var _re4se = proc.StandardError.ReadToEndAsync();
                        await Task.WhenAll(_re4so, _re4se);
                        await proc.WaitForExitAsync(ct);
                        string stderr = _re4se.Result;
                        var m = System.Text.RegularExpressions.Regex.Match(
                            stderr, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                        if (m.Success)
                        {
                            dur = int.Parse(m.Groups[1].Value) * 3600
                                + int.Parse(m.Groups[2].Value) * 60
                                + double.Parse(m.Groups[3].Value,
                                    System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                }
                catch { }
                durations.Add(dur);
            }

            var inputs = new System.Text.StringBuilder();
            var filterParts = new System.Text.StringBuilder();
            double runningOffset = 0;
            string lastLabel = "0:v";

            for (int i = 0; i < videoFiles.Count; i++)
                inputs.Append($"-i \"{videoFiles[i]}\" ");

            for (int i = 1; i < videoFiles.Count; i++)
            {
                runningOffset += durations[i - 1] - fadeDuration;
                string outLabel = (i == videoFiles.Count - 1) ? "vout" : $"v{i}";
                string offsetStr = runningOffset.ToString("F3",
                    System.Globalization.CultureInfo.InvariantCulture);
                string fadeStr = fadeDuration.ToString("F3",
                    System.Globalization.CultureInfo.InvariantCulture);

                filterParts.Append(
                    $"[{lastLabel}][{i}:v]xfade=transition=fade:" +
                    $"duration={fadeStr}:offset={offsetStr}[{outLabel}];");
                lastLabel = outLabel;
            }

            string filterComplex = filterParts.ToString().TrimEnd(';');

            // DODATO ZA WINDOWS MEDIA PLAYER
            string encArgs = vEncArgs_cached ?? "-c:v libx264 -preset veryfast -crf 20 -profile:v high -level 4.1";

            string args = $"-nostdin {inputs}" +
                          $"-filter_complex \"{filterComplex}\" " +
                          $"-map \"[vout]\" " +
                          $"{encArgs} " +
                          $"-pix_fmt yuv420p -an -y \"{outputPath}\"";

            bool ok = await RunFFmpegAsync(args, ct);
            return ok && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000
                ? outputPath : null;
        }

        private string vEncArgs_cached = null;

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