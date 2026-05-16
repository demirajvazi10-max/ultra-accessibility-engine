using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// Lokalna Whisper AI transkripcija — bez interneta, bez API kljuca.
    /// Koristi whisper.exe (Python whisper CLI) ili faster-whisper-xxl.exe.
    /// Vraca stihove sa timestamp-ovima za sinhronizaciju kadrova.
    /// </summary>
    public static class AITranscription
    {
        // ── Rezultat transkripcije ────────────────────────────────────────────
        public class TranscriptionResult
        {
            public string FullText { get; set; } = "";
            public List<TimedLine> Lines { get; set; } = new();
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = "";
        }

        public class TimedLine
        {
            public double StartSeconds { get; set; }
            public double EndSeconds   { get; set; }
            public string Text         { get; set; } = "";
        }

        // ── Pretraga Whisper izvrsne datoteke ─────────────────────────────────
        private static string FindWhisperExecutable()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(appDir, "whisper.exe"),
                Path.Combine(appDir, "whisper-cli.exe"),
                Path.Combine(appDir, "faster-whisper-xxl.exe"),
                Path.Combine(appDir, "Whisper", "whisper.exe"),
                Path.Combine(appDir, "Whisper", "faster-whisper-xxl.exe"),
                Path.Combine(appDir, "Tools", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "Local", "Programs", "Python", "Python311", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "Local", "Programs", "Python", "Python312", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "Local", "Programs", "Python", "Python310", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "miniconda3", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "anaconda3", "Scripts", "whisper.exe"),
            };

            foreach (var path in candidates)
                if (File.Exists(path)) return path;

            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo("where", "whisper")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (!string.IsNullOrEmpty(output))
                {
                    string first = output.Split('\n')[0].Trim();
                    if (File.Exists(first)) return first;
                }
            }
            catch { }

            return null;
        }

        
        // Language helper
        private static string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public static bool IsWhisperAvailable() => FindWhisperExecutable() != null;

        private static async Task<string> ExtractAudioAsync(string mediaPath, string tempDir, string ffmpegPath)
        {
            string outPath = Path.Combine(tempDir, $"whisper_audio_{Guid.NewGuid():N}.wav");
            string args = $"-nostdin -i \"{mediaPath}\" -ar 16000 -ac 1 -c:a pcm_s16le -y \"{outPath}\"";

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpegPath, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            await Task.Run(() => proc.WaitForExit());
            return File.Exists(outPath) ? outPath : null;
        }

        private static List<TimedLine> ParseWhisperSrt(string srtPath)
        {
            var lines = new List<TimedLine>();
            if (!File.Exists(srtPath)) return lines;

            string content = File.ReadAllText(srtPath, Encoding.UTF8);
            var blocks = Regex.Split(content.Trim(), @"\r?\n\r?\n");

            foreach (var block in blocks)
            {
                var blockLines = block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (blockLines.Length < 3) continue;

                var timeMatch = Regex.Match(blockLines[1],
                    @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");
                if (!timeMatch.Success) continue;

                double start = int.Parse(timeMatch.Groups[1].Value) * 3600
                             + int.Parse(timeMatch.Groups[2].Value) * 60
                             + int.Parse(timeMatch.Groups[3].Value)
                             + int.Parse(timeMatch.Groups[4].Value.Trim()) / 1000.0;

                double end = int.Parse(timeMatch.Groups[5].Value) * 3600
                           + int.Parse(timeMatch.Groups[6].Value) * 60
                           + int.Parse(timeMatch.Groups[7].Value)
                           + int.Parse(timeMatch.Groups[8].Value) / 1000.0;

                string text = string.Join(" ", blockLines.Skip(2)).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(new TimedLine { StartSeconds = start, EndSeconds = end, Text = text });
            }

            return lines;
        }

        public static async Task<TranscriptionResult> TranscribeAsync(
            string mediaPath,
            string language = "sr",
            string ffmpegPath = null,
            string modelSize = "large-v3",
            IProgress<string> progress = null,
            CancellationToken ct = default)
        {
            var result = new TranscriptionResult();

            string whisperExe = FindWhisperExecutable();
            if (whisperExe == null)
            {
                result.ErrorMessage =
                    "Whisper not found on this computer.\n\n" +
                    "Install it in one of these ways:\n\n" +
                    "OPTION A — Python (recommended):\n" +
                    "  pip install openai-whisper\n\n" +
                    "OPTION B — Standalone (no Python):\n" +
                    "  Download faster-whisper-xxl.exe from GitHub\n" +
                    "  and place it next to UltraVideoEditor.exe\n\n" +
                    "After installation, restart the application.";
                return result;
            }

            if (string.IsNullOrEmpty(ffmpegPath))
                ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
            {
                result.ErrorMessage = L("re_ffmpeg_missing");
                return result;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), $"UVE_Whisper_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                progress?.Report("Ekstrahujem audio...");
                string audioPath = mediaPath;

                string ext = Path.GetExtension(mediaPath).ToLower();
                if (ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm")
                {
                    audioPath = await ExtractAudioAsync(mediaPath, tempDir, ffmpegPath);
                    if (audioPath == null)
                    {
                        result.ErrorMessage = L("at_extract_error");
                        return result;
                    }
                }

                progress?.Report($"Whisper analizira audio (model: {modelSize})...");

                bool isFasterWhisper = whisperExe.ToLower().Contains("faster-whisper");
                string whisperArgs = isFasterWhisper
                    ? $"\"{audioPath}\" --model {modelSize} --language {language} " +
                      $"--output_format srt --output_dir \"{tempDir}\" " +
                      $"--compute_type float16 " +   // GPU preciznost — tačnije od int8/int8_float16
                      $"--beam_size 5 " +            // 5 kandidata po koraku (default, ali eksplicitno)
                      $"--best_of 5 " +              // uzima najbolji od 5 — poboljšava preciznost
                      $"--temperature 0"             // bez random sampling — deterministično, manje grešaka
                    : $"\"{audioPath}\" --model {modelSize} --language {language} --output_format srt --output_dir \"{tempDir}\" --verbose False";

                var whisperProc = new Process
                {
                    StartInfo = new ProcessStartInfo(whisperExe, whisperArgs)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                var stdErr = new StringBuilder();
                whisperProc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        stdErr.AppendLine(e.Data);
                        if (e.Data.Contains("%") || e.Data.Contains("Detecting"))
                            progress?.Report($"Whisper: {e.Data.Trim()}");
                    }
                };

                whisperProc.Start();
                whisperProc.BeginErrorReadLine();

                // WaitForExitAsync(ct) direktno reaguje na otkazivanje — nema polling latency
                try
                {
                    await whisperProc.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    try { whisperProc.Kill(); } catch { }
                    throw;
                }

                ct.ThrowIfCancellationRequested();

                progress?.Report("Parsiranje rezultata...");

                string audioName = Path.GetFileNameWithoutExtension(audioPath);
                string srtPath = Path.Combine(tempDir, audioName + ".srt");

                if (!File.Exists(srtPath))
                {
                    var srtFiles = Directory.GetFiles(tempDir, "*.srt");
                    srtPath = srtFiles.FirstOrDefault();
                }

                if (srtPath == null || !File.Exists(srtPath))
                {
                    result.ErrorMessage = LF("at_no_srt", stdErr);
                    return result;
                }

                var timedLines = ParseWhisperSrt(srtPath);

                if (timedLines.Count == 0)
                {
                    result.ErrorMessage = "Whisper nije prepoznao nikakav tekst u audio fajlu.";
                    return result;
                }

                result.Lines = timedLines;
                result.FullText = string.Join("\n", timedLines.Select(l => l.Text));
                result.Success = true;
                progress?.Report($"Gotovo — {timedLines.Count} linija prepoznato.");
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Transkripcija otkazana.";
            }
            catch (Exception ex)
            {
                result.ErrorMessage = LF("generic_error", ex.Message);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            return result;
        }

        public static string FormatLyricsForTextBox(List<TimedLine> lines)
            => string.Join("\n", lines.Select(l => l.Text));

        public static Dictionary<string, double> BuildTimestampMap(List<TimedLine> lines)
        {
            var map = new Dictionary<string, double>();
            foreach (var line in lines)
                if (!map.ContainsKey(line.Text))
                    map[line.Text] = line.StartSeconds;
            return map;
        }
    }
}
