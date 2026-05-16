using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    public class VisionResult
    {
        public double Score { get; set; }
        public bool HasChildren { get; set; }
        public bool HasFaces { get; set; }
        public bool IsOutdoor { get; set; }
        public bool IsWarm { get; set; }
        public bool HasMotion { get; set; }
        public double Luminance { get; set; }
        public double Saturation { get; set; }
        public double Sharpness { get; set; }
        public string TopLabel { get; set; }
        public string[] Labels { get; set; }
        public bool OnnxUsed { get; set; }
        public string RejectReason { get; set; }

        /// <summary>
        /// Qwen je oznacio da scena ne odgovara kontekstu stiha —
        /// sistem treba da potrazi novi video za ovaj segment.
        /// </summary>
        public bool RetryNeeded { get; set; }
    }

    public static class VisionAnalyzer
    {
        private static readonly string _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraVideoEditor", "Models");

        private static readonly string _modelPath = Path.Combine(_modelDir, "mobilenetv2.onnx");
        private static readonly string _labelsPath = Path.Combine(_modelDir, "imagenet_labels.txt");

        private const string MODEL_URL =
            "https://github.com/onnx/models/raw/main/validated/vision/classification/mobilenet/model/mobilenetv2-12.onnx";
        private const string LABELS_URL =
            "https://raw.githubusercontent.com/anishathalye/imagenet-simple-labels/master/imagenet-simple-labels.json";

        private static object _onnxSession = null;
        private static string[] _labels = null;
        private static bool _onnxAvailable = false;
        private static bool _initDone = false;
        private static readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);

        // ── Qwen2-VL (Ollama) ─────────────────────────────────────────────────
        private static OllamaClient _ollamaClient;
        private static bool _qwenAvailable = false;
        private static readonly SemaphoreSlim _qwenCheckLock = new SemaphoreSlim(1, 1);

        private const string QWEN_MODEL      = "qwen2-vl";
        private const string QWEN_MODEL_ALT  = "qwen2.5-vl";
        private const string QWEN_MODEL_ALT2 = "qwen2.5vl"; // instaliran bez crtice

        // QWEN_PROMPT se više ne koristi direktno — koristiti BuildContextualQwenPrompt()
        // Ostaje kao fallback za pozive koji ne prosljeđuju kontekst
        private const string QWEN_PROMPT =
            "Analyze this image for a family music video. Respond ONLY in JSON:\n" +
            "{\"score\":7,\"outdoor\":true,\"children\":false,\"faces\":true,\"animals\":false," +
            "\"warm\":true,\"motion\":false,\"luminance\":0.6,\"saturation\":0.5,\"sharpness\":70," +
            "\"label\":\"park\",\"labels\":[\"park\",\"outdoor\",\"trees\",\"people\"]," +
            "\"retry\":false,\"reject\":null}\n" +
            "REJECT if: medical masks, hockey/boxing, violence, grainy/blurry, motion blur, watery compression, " +
            "dark/gloomy/sad, watermarks, text overlays, city/urban/traffic, people posing at camera.";

        // ── Dinamicki prompt koji uzima u obzir stih i godisnje doba ─────────
        public static string BuildContextualQwenPrompt(
            string lyricLine = null,
            string season = null,
            string mood = null,
            string context = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are a strict video quality reviewer for a family music video.");
            sb.AppendLine("Analyze this image and respond ONLY in this exact JSON format (no markdown, no extra text):");
            sb.AppendLine("{\"score\":7,\"outdoor\":true,\"children\":false,\"faces\":true,\"animals\":false,");
            sb.AppendLine("\"warm\":true,\"motion\":false,\"luminance\":0.6,\"saturation\":0.5,\"sharpness\":70,");
            sb.AppendLine("\"label\":\"park\",\"labels\":[\"park\",\"outdoor\",\"trees\",\"people\"],");
            sb.AppendLine("\"retry\":false,\"reject\":null}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(lyricLine))
                sb.AppendLine($"CURRENT LYRIC LINE: \"{lyricLine}\"");
            sb.AppendLine("\nSEASONAL & MOOD RULES:");
            string normSeason = (season ?? "").ToLower();
            if (normSeason == "spring" || normSeason == "summer")
            {
                sb.AppendLine("- SEASON: spring/summer — PREFER warm, sunny, bright outdoor scenes.");
                sb.AppendLine("- REJECT scenes that look cold, grey, wintry, or gloomy.");
            }
            else if (normSeason == "winter")
            {
                sb.AppendLine("- SEASON: winter — PREFER crisp bright white snow, clear sky, cheerful scenes.");
                sb.AppendLine("- REJECT grey overcast winter scenes that look sad or depressing.");
            }
            else if (normSeason == "autumn")
            {
                sb.AppendLine("- SEASON: autumn — PREFER warm orange/red/golden leaves, cozy outdoor scenes.");
                sb.AppendLine("- REJECT scenes that look cold, rainy, or melancholic.");
            }
            else
            {
                sb.AppendLine("- PREFER bright, colorful, joyful scenes with good lighting.");
            }
            sb.AppendLine("\nABSOLUTE REJECT RULES (set reject to the reason, not null):");
            sb.AppendLine("- scene looks sad, dark, gloomy, or depressing");
            sb.AppendLine("- medical masks, surgical masks, face masks");
            sb.AppendLine("- hockey, boxing, MMA, wrestling, or aggressive sport");
            sb.AppendLine("- violence, weapons, blood, hospital, cemetery");
            sb.AppendLine("- grainy, blurry, pixelated, or low quality image");
            sb.AppendLine("- excessive motion blur or watery/compression artifacts");
            sb.AppendLine("- poor contrast (washed out or crushed blacks)");
            sb.AppendLine("- surveillance/security camera look");
            sb.AppendLine("- text overlays, watermarks, or logos");
            sb.AppendLine("- people posing directly at camera with forced/dental-commercial smile (posed stock look)");
            sb.AppendLine("- corporate wellness, maternity lifestyle, or adult-only content (unless lyric demands it)");
            sb.AppendLine("- image is 'airy and empty' with no clear subject or focal point");
            // PATCH 10: Action Focus — kognitivno prilagođavanje za djecu do 6 god
            sb.AppendLine("- CLUTTERED SCENE REJECT: scene with 20+ people in background where main subject is unclear — score max 4, set reject='cluttered background too busy'");
            sb.AppendLine("- REJECT: visually noisy scenes with too many competing focal points (busy market, crowded festival, traffic intersection with many cars) — child loses attention");
            sb.AppendLine("- REJECT: scenes where the main subject (child/animal) is surrounded by distracting unrelated elements that occupy more than 60% of the frame");
            sb.AppendLine("- PREFER: clean backgrounds — single child on grass, child with one animal, child with one toy — simple, clear, focused");
            sb.AppendLine("- PREFER: bokeh/shallow depth of field where background is soft — subject pops out clearly for young viewers");
            sb.AppendLine("- pregnant belly, adult romance, or dating content");
            // PATCH 8: Kids Edition — Extended reject rules for 2-6 year old target audience
            sb.AppendLine("- gym equipment, weightlifting, bodybuilding, kettlebell, dumbbell — ANY fitness/gym scene");
            sb.AppendLine("- pregnant belly or maternity photo — reject immediately even if visually appealing");
            sb.AppendLine("- traffic jams, highway, crowded subway, metro, urban rush hour");
            sb.AppendLine("- hospital, clinic, medical equipment, doctors, nurses, syringes");
            sb.AppendLine("- corporate offices, business meetings, conference rooms");
            sb.AppendLine("- dark/desaturated/vintage filter — must look VIBRANT and CLEAN, not washed out");
            sb.AppendLine("- wide establishing shots where people appear smaller than 1/4 of frame height");
            sb.AppendLine("- rapid camera shake, handheld shaky footage, aggressive zoom or whip-pan");
            // PATCH 9: Anomalija-Fix "Vrtić Preciznost"
            sb.AppendLine("- ANOMALIJA 2 (REJECT): gramophone, turntable, vinyl record player, DJ mixer, mixing console, recording studio equipment, audio mixer, synthesizer, amplifier — child does NOT know what this is, reject immediately");
            sb.AppendLine("- ANOMALIJA 1 (HARD REJECT, NO EXCEPTIONS): ANY person holding or swinging a kettlebell, dumbbell, barbell, or any metal weight — INSTANT REJECT score=1. ANY gym machine visible in frame — INSTANT REJECT. This is the #1 most critical rule. One frame with a weight = reject the entire clip.");
            sb.AppendLine("- ANOMALIJA 3 (REJECT): urban cold shots — concrete skyline, busy street with cars, skyscrapers without greenery, cold/blue toned city shots");
            sb.AppendLine("- COMPOSITION RULE: if subject is a child, child must occupy at least 30% of frame area. If child is smaller than 30% — score max 4, set retry:true");
            sb.AppendLine("- WARM CONTINUITY: if previous clip was warm-toned, reject cold/blue-toned clips unless song is about snow or winter");
            sb.AppendLine("\nKIDS EDITION — PREFER (target: 2-6 year olds):");
            sb.AppendLine("- single clear subject in center of frame (child, animal, toy, flower)");
            sb.AppendLine("- vibrant saturated colors WITHOUT neon/glitch effects — think Cocomelon palette");
            sb.AppendLine("- animals: dogs, cats, birds, rabbits, ducks — NOT wild/aggressive animals");
            sb.AppendLine("- playground equipment, swings, slides, balloons, bubbles");
            sb.AppendLine("- warm family interactions: grandparent+child, parent+child, siblings playing");
            sb.AppendLine("- REPLACEMENT for gym scene: children jumping in sand, running after butterfly, playing hopscotch, dancing in circle");
            sb.AppendLine("- REPLACEMENT for gramophone: children clapping hands in rhythm, feet dancing on ground (close-up), children singing in circle");
            sb.AppendLine("- Set score 9-10 for: child+animal interaction, child+grandparent, child blowing bubbles or playing with balloons");
            sb.AppendLine("- Set score 9-10 for: child clearly occupying 30%+ of frame, warm sunlit colors, joyful expression");

            // PATCH 10 / CANDID ONLY — eliminacija "Stock Look"
            sb.AppendLine("\nCANDID ONLY RULE (strict — no exceptions):");
            sb.AppendLine("- REJECT IMMEDIATELY: any ADULT (18+) looking directly into the lens AND smiling — this is the classic stock photo look, score=1, reject='adult looking at camera stock'");
            sb.AppendLine("- REJECT: group of adults posing together looking at camera — score=1, reject='group pose stock'");
            sb.AppendLine("- EXCEPTION: children looking at camera while laughing/playing is ACCEPTABLE — children are naturally curious");
            sb.AppendLine("- PREFER: adults seen from behind, from the side, or interacting with children/each other");
            sb.AppendLine("- PREFER: candid moments — adult kneeling to child level, holding child's hand, watching child play");
            sb.AppendLine("- Set score 9-10 for: adult turned away from camera while child is visible and active in foreground");
            sb.AppendLine("- Set score 9-10 for: any interaction scene where no one is looking at the camera");
            sb.AppendLine("\nCANDID PREFERENCE (secondary):");
            sb.AppendLine("- PREFER: people interacting with each other, not looking at camera");
            sb.AppendLine("- PREFER: children at play, running, laughing naturally");
            sb.AppendLine("- Set score higher (8-10) for candid unposed moments");

            // Visual metaphor awareness
            sb.AppendLine("\nMETAPHOR AWARENESS:");
            sb.AppendLine("- If the scene could represent a feeling/emotion (joy, freedom, love), prefer it over literal objects");
            sb.AppendLine("- 'Heart full of happiness' → child in embrace, NOT anatomical heart image");
            sb.AppendLine("- Abstract or empty scenes score LOW even if technically sharp");

            // Color temperature field
            sb.AppendLine("\nAdd to JSON: \"warm\": true/false (is the color palette warm/golden or cool/blue?)");
            if (!string.IsNullOrWhiteSpace(lyricLine))
            {
                sb.AppendLine($"\nCONTEXTUAL LOGIC — lyric: \"{lyricLine}\"");
                sb.AppendLine("- If the scene does NOT match the emotion/action of this lyric, set \"retry\":true");
                sb.AppendLine("- Examples: lyric=walking/nature but scene=sport → retry:true");
                sb.AppendLine("- Examples: lyric=joyful but scene=sad/dark → retry:true");
            }
            sb.AppendLine("\nJSON: score 1-10, booleans, luminance/saturation/sharpness, label, labels[], retry bool, reject string or null.");
            return sb.ToString();
        }


        private static readonly HashSet<string> _childLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "baby", "toddler", "child", "girl", "boy", "infant", "kid",
            "playground", "swing", "slide", "sandbox"
        };

        private static readonly HashSet<string> _faceLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "face", "person", "people", "man", "woman", "girl", "boy", "portrait", "smile"
        };

        private static readonly HashSet<string> _outdoorLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "park", "garden", "field", "meadow", "forest", "beach", "mountain",
            "sky", "nature", "outdoor", "playground", "lawn", "flower", "tree",
            "grass", "lake", "fountain", "snow", "spring", "summer"
        };

        private static readonly HashSet<string> _badLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "coffee", "espresso", "cappuccino", "beer", "wine", "alcohol",
            "office", "cubicle", "desk", "laptop", "keyboard",
            "cigarette", "tobacco", "gun", "weapon", "knife",
            "frog", "toad", "reptile", "snake", "spider",
            "cemetery", "grave", "coffin", "skull", "casino", "nightclub"
        };

        public static async Task<bool> InitializeAsync(
            Action<string> log = null,
            CancellationToken ct = default)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (_initDone) return _onnxAvailable;

                Directory.CreateDirectory(_modelDir);

                if (!IsOnnxRuntimeAvailable())
                {
                    log?.Invoke("🧠 VisionAnalyzer: ONNX Runtime nije instaliran — FFmpeg mod aktivan");
                    _onnxAvailable = false;
                    _initDone = true;
                    return false;
                }

                if (!File.Exists(_modelPath))
                {
                    log?.Invoke("🧠 VisionAnalyzer: Preuzimam MobileNetV2 model (~14MB)...");
                    bool downloaded = await DownloadFileAsync(MODEL_URL, _modelPath, ct);
                    if (!downloaded)
                    {
                        log?.Invoke("⚠ VisionAnalyzer: Download nije uspio — FFmpeg mod aktivan");
                        _onnxAvailable = false;
                        _initDone = true;
                        return false;
                    }
                    log?.Invoke("✅ VisionAnalyzer: Model preuzet");
                }

                if (!File.Exists(_labelsPath))
                    await DownloadFileAsync(LABELS_URL, _labelsPath, ct);

                if (File.Exists(_labelsPath))
                {
                    string json = await File.ReadAllTextAsync(_labelsPath, ct);
                    var matches = Regex.Matches(json, "\"([^\"]+)\"");
                    _labels = matches.Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                }

                _onnxSession = CreateOnnxSession(_modelPath);
                _onnxAvailable = _onnxSession != null;

                if (_onnxAvailable)
                    log?.Invoke("✅ VisionAnalyzer: ONNX aktivan, " + (_labels?.Length ?? 0) + " labela");
                else
                    log?.Invoke(LanguageManager.GetText("va_onnx_error", (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr"));

                _initDone = true;
                return _onnxAvailable;
            }
            catch (Exception ex)
            {
                log?.Invoke(string.Format(LanguageManager.GetText("va_init_error", (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr"), ex.Message));
                _onnxAvailable = false;
                _initDone = true;
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Provjeri i inicijalizuj Qwen2-VL via Ollama (pozovi jednom pri startu).
        /// </summary>
        public static async Task<bool> InitializeQwenAsync(
            Action<string> log = null,
            CancellationToken ct = default)
        {
            await _qwenCheckLock.WaitAsync(ct);
            try
            {
                if (_qwenAvailable) return true;

                _ollamaClient = new OllamaClient();
                if (!await _ollamaClient.IsOllamaRunning())
                {
                    log?.Invoke("🧠 VisionAnalyzer: Ollama nije pokrenuta — Qwen2-VL nedostupan");
                    return false;
                }

                // Provjeri da li je model dostupan
                bool hasQwen = await _ollamaClient.IsModelAvailable(QWEN_MODEL)      ||
                               await _ollamaClient.IsModelAvailable(QWEN_MODEL_ALT)  ||
                               await _ollamaClient.IsModelAvailable(QWEN_MODEL_ALT2);

                if (hasQwen)
                {
                    _qwenAvailable = true;
                    log?.Invoke($"✅ VisionAnalyzer: Qwen2-VL aktivan — AI analiza slike omogućena");
                }
                else
                {
                    log?.Invoke($"⚠️ VisionAnalyzer: Qwen2-VL model nije instaliran.\n" +
                                $"   Instaliraj: ollama pull {QWEN_MODEL}\n" +
                                $"   Koristi se ONNX/FFmpeg fallback.");
                }

                return _qwenAvailable;
            }
            catch
            {
                _qwenAvailable = false;
                return false;
            }
            finally
            {
                _qwenCheckLock.Release();
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static async Task<VisionResult> AnalyzeClipAsync(
            string videoPath,
            string ffmpegPath,
            CancellationToken ct = default,
            string lyricLine = null,
            string season = null,
            string mood = null,
            string context = null)
        {
            if (!File.Exists(videoPath))
                return MakeResult(5.0, "Fajl ne postoji");

            string tempFrame = Path.Combine(
                Path.GetTempPath(),
                "vision_" + Guid.NewGuid().ToString().Substring(0, 8) + ".jpg");

            try
            {
                bool extracted = await ExtractFrame(videoPath, tempFrame, ffmpegPath, ct);
                if (!extracted)
                    return await FfmpegAnalyze(videoPath, ffmpegPath, ct);

                // ── Prioritet 1: Qwen2-VL via Ollama ────────────────────────
                if (_qwenAvailable && _ollamaClient != null)
                {
                    var qwenResult = await QwenAnalyzeFrame(tempFrame, ct, lyricLine, season, mood, context);
                    if (qwenResult != null)
                    {
                        // Dopunjavamo sa FFmpeg metrikama (luminance, saturation, sharpness)
                        var ffMetrics = await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
                        if (qwenResult.Luminance  <= 0) qwenResult.Luminance  = ffMetrics.Luminance;
                        if (qwenResult.Saturation <= 0) qwenResult.Saturation = ffMetrics.Saturation;
                        if (qwenResult.Sharpness  <= 0) qwenResult.Sharpness  = ffMetrics.Sharpness;
                        return qwenResult;
                    }
                }

                // ── Prioritet 2: ONNX MobileNetV2 ────────────────────────────
                if (_onnxAvailable && _onnxSession != null)
                {
                    var onnxResult = await OnnxAnalyzeFrame(tempFrame, ct);
                    if (onnxResult != null)
                    {
                        var ffResult = await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
                        onnxResult.Luminance  = ffResult.Luminance;
                        onnxResult.Saturation = ffResult.Saturation;
                        onnxResult.Sharpness  = ffResult.Sharpness;

                        double sharpScore = Math.Min(10.0, onnxResult.Sharpness / 10.0);
                        double lumScore   = CalcLumScore(onnxResult.Luminance);
                        onnxResult.Score  = Math.Round(
                            onnxResult.Score * 0.5 + sharpScore * 0.3 + lumScore * 0.2, 1);

                        return onnxResult;
                    }
                }

                // ── Prioritet 3: FFmpeg heuristika (fallback) ─────────────────
                return await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
            }
            finally
            {
                try { if (File.Exists(tempFrame)) File.Delete(tempFrame); } catch { }
            }
        }

        // ── Qwen2-VL analiza ──────────────────────────────────────────────────

        private static async Task<VisionResult> QwenAnalyzeFrame(
            string imagePath,
            CancellationToken ct,
            string lyricLine = null,
            string season = null,
            string mood = null,
            string context = null)
        {
            try
            {
                // Biramo model koji je dostupan
                string model = await _ollamaClient.IsModelAvailable(QWEN_MODEL)    ? QWEN_MODEL :
                               await _ollamaClient.IsModelAvailable(QWEN_MODEL_ALT) ? QWEN_MODEL_ALT :
                               QWEN_MODEL_ALT2;

                // Koristimo dinamicki prompt ako imamo kontekst, inace fallback na staticki
                string prompt = (lyricLine != null || season != null || mood != null)
                    ? BuildContextualQwenPrompt(lyricLine, season, mood, context)
                    : QWEN_PROMPT;

                var (response, error) = await _ollamaClient.VisionAsyncEx(imagePath, prompt, model, ct);
                if (!string.IsNullOrWhiteSpace(error))
                    System.Diagnostics.Debug.WriteLine($"QwenAnalyzeFrame error: {error}");
                if (string.IsNullOrWhiteSpace(response)) return null;

                return ParseQwenResponse(response);
            }
            catch
            {
                return null;
            }
        }

        private static VisionResult ParseQwenResponse(string json)
        {
            try
            {
                // Čistimo potencijalni markdown wrap
                json = Regex.Replace(json, @"```json?\s*|\s*```", "").Trim();

                // Jednostavan parser bez Newtonsoft ovisnosti na ovom nivou
                double  GetDouble(string key, double def = 0) {
                    var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*([\\d.]+)");
                    return m.Success && double.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;
                }
                bool GetBool(string key) {
                    var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*(true|false)");
                    return m.Success && m.Groups[1].Value == "true";
                }
                string GetString(string key) {
                    var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]+)\"");
                    return m.Success ? m.Groups[1].Value : null;
                }
                string[] GetArray(string key) {
                    var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*\\[([^\\]]+)\\]");
                    if (!m.Success) return Array.Empty<string>();
                    return Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"")
                                .Cast<Match>().Select(x => x.Groups[1].Value).ToArray();
                }

                double score     = GetDouble("score", 5.0);
                string rejectStr = GetString("reject");

                // Primijeni iste filteri kao ONNX/FFmpeg path
                string topLabel  = GetString("label") ?? "";
                string[] labels  = GetArray("labels");

                if (!string.IsNullOrEmpty(rejectStr))
                    return MakeResult(1.0, rejectStr);

                if (labels.Any(l => _badLabels.Contains(l)))
                    return MakeResult(1.0, string.Join(", ", labels.Where(l => _badLabels.Contains(l))));

                bool retryNeeded = GetBool("retry");

                return new VisionResult
                {
                    Score       = Math.Clamp(score, 1.0, 10.0),
                    IsOutdoor   = GetBool("outdoor"),
                    HasChildren = GetBool("children"),
                    HasFaces    = GetBool("faces"),
                    IsWarm      = GetBool("warm"),
                    HasMotion   = GetBool("motion"),
                    Luminance   = GetDouble("luminance", 0.5),
                    Saturation  = GetDouble("saturation", 0.5),
                    Sharpness   = GetDouble("sharpness", 50),
                    TopLabel    = topLabel,
                    Labels      = labels,
                    OnnxUsed    = false,  // Qwen2-VL, ne ONNX
                    RetryNeeded = retryNeeded
                };
            }
            catch
            {
                return null;
            }
        }

        private static async Task<bool> ExtractFrame(
            string videoPath, string outputPath, string ffmpegPath, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                    return false;

                if (File.Exists(outputPath)) File.Delete(outputPath);

                // -ss PRIJE -i = input seeking (brzo, bez dekodiranja cijelog videa)
                // PATCH 9: Robustno — probaj ss=1 prvi, pa fallback ss=0 ako fajl kratak
                string args = $"-nostdin -y -ss 1 -i \"{videoPath}\" -vframes 1 -vf scale=224:224 -q:v 2 \"{outputPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName               = ffmpegPath,
                    Arguments              = args,
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardError  = true,   // MORA se citati asinhrono — inace deadlock!
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;

                // KRITICNO: citamo stderr ASINHRONO prije WaitForExit
                // Bez ovoga FFmpeg blokira na punom stderr bufferu (~4KB)
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await proc.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                    return false;
                }

                bool valid = proc.ExitCode == 0
                          && File.Exists(outputPath)
                          && new FileInfo(outputPath).Length > 512;

                // PATCH 9: Fallback — ako ss=1 nije upio frejm (video kraći od 1s), pokušaj sa ss=0
                if (!valid && File.Exists(ffmpegPath))
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    string fallbackArgs = $"-nostdin -y -ss 0 -i \"{videoPath}\" -vframes 1 -vf scale=224:224 -q:v 2 \"{outputPath}\"";
                    var psi2 = new ProcessStartInfo
                    {
                        FileName = ffmpegPath, Arguments = fallbackArgs,
                        CreateNoWindow = true, UseShellExecute = false,
                        RedirectStandardError = true, RedirectStandardOutput = true
                    };
                    using var proc2 = Process.Start(psi2);
                    if (proc2 != null)
                    {
                        _ = proc2.StandardError.ReadToEndAsync();
                        _ = proc2.StandardOutput.ReadToEndAsync();
                        using var t2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        t2.CancelAfter(TimeSpan.FromSeconds(10));
                        try { await proc2.WaitForExitAsync(t2.Token); } catch { try { proc2.Kill(true); } catch { } }
                        valid = proc2.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 512;
                    }
                }
                return valid;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static async Task<VisionResult> OnnxAnalyzeFrame(string imagePath, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    float[] inputData = LoadImageAsTensor(imagePath);
                    if (inputData == null) return null;

                    string[] topLabels = RunOnnxInference(inputData);
                    if (topLabels == null || topLabels.Length == 0) return null;

                    var result = new VisionResult { OnnxUsed = true };
                    result.TopLabel = topLabels[0];
                    result.Labels = topLabels;

                    bool hasBad = topLabels.Any(l => _badLabels.Contains(l));
                    result.HasChildren = topLabels.Any(l => _childLabels.Contains(l));
                    result.HasFaces = topLabels.Any(l => _faceLabels.Contains(l));
                    result.IsOutdoor = topLabels.Any(l => _outdoorLabels.Contains(l));

                    if (hasBad)
                    {
                        result.Score = 1.0;
                        string badLabel = topLabels.FirstOrDefault(l => _badLabels.Contains(l)) ?? "";
                        result.RejectReason = "Neprikladan sadrzaj: " + badLabel;
                    }
                    else
                    {
                        double score = 5.0;
                        if (result.HasChildren) score += 2.5;
                        if (result.HasFaces) score += 1.5;
                        if (result.IsOutdoor) score += 1.0;
                        result.Score = Math.Min(10.0, score);
                    }

                    return result;
                }
                catch { return null; }
            }, ct);
        }

        private static async Task<VisionResult> FfmpegAnalyze(
            string videoPath, string ffmpegPath, CancellationToken ct)
        {
            string tempFrame = Path.Combine(Path.GetTempPath(),
                "vis_" + Guid.NewGuid().ToString().Substring(0, 8) + ".png");
            try
            {
                await ExtractFrame(videoPath, tempFrame, ffmpegPath, ct);
                return await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
            }
            finally
            {
                try { if (File.Exists(tempFrame)) File.Delete(tempFrame); } catch { }
            }
        }

        private static async Task<VisionResult> FfmpegFrameAnalyze(
            string framePath, string ffmpegPath, CancellationToken ct)
        {
            var result = new VisionResult { OnnxUsed = false };

            if (!File.Exists(framePath) || !File.Exists(ffmpegPath))
                return MakeResult(6.0, "FFmpeg nije dostupan");

            try
            {
                string args = "-nostdin -i \"" + framePath + "\" -vf \"signalstats,blurdetect\" -f null -";
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return MakeResult(6.0, "FFmpeg pokretanje neuspjesno");
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);

                double luminance = 128.0;
                var lumMatch = Regex.Match(stderr, @"YAVG:([\d.]+)");
                if (lumMatch.Success)
                {
                    double parsed;
                    if (double.TryParse(lumMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        luminance = parsed;
                    }
                }

                double saturation = 50.0;
                var satMatch = Regex.Match(stderr, @"SATAVG:([\d.]+)");
                if (satMatch.Success)
                {
                    double parsed;
                    if (double.TryParse(satMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        saturation = parsed;
                    }
                }

                double blurRatio = 0.5;
                var blurMatch = Regex.Match(stderr, @"blur_ratio:([\d.]+)");
                if (blurMatch.Success)
                {
                    double parsed;
                    if (double.TryParse(blurMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        blurRatio = parsed;
                    }
                }

                result.Luminance = luminance;
                result.Saturation = saturation;
                result.Sharpness = Math.Round((1.0 - blurRatio) * 100.0, 1);
                result.IsWarm = saturation > 30 && luminance > 60;
                result.IsOutdoor = luminance > 70;

                double lumScore = CalcLumScore(luminance);

                double satScore;
                if (saturation < 10) satScore = 2.0;
                else if (saturation < 30) satScore = 5.0;
                else if (saturation <= 120) satScore = 10.0;
                else satScore = 6.0;

                double sharpScore;
                if (result.Sharpness > 60) sharpScore = 10.0;
                else if (result.Sharpness > 30) sharpScore = 7.0;
                else if (result.Sharpness > 10) sharpScore = 4.0;
                else sharpScore = 1.0;

                result.Score = Math.Round(lumScore * 0.4 + satScore * 0.3 + sharpScore * 0.3, 1);

                if (result.Score < 4.0)
                {
                    result.RejectReason = "Nizak kvalitet: lum=" + luminance.ToString("F0") +
                        ", sat=" + saturation.ToString("F0") +
                        ", sharp=" + result.Sharpness.ToString("F0");
                }

                return result;
            }
            catch
            {
                return MakeResult(6.0, LanguageManager.GetText("va_analysis_error", (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr"));
            }
        }

        private static bool IsOnnxRuntimeAvailable()
        {
            try
            {
                var asm = System.Reflection.Assembly.Load("Microsoft.ML.OnnxRuntime");
                return asm != null;
            }
            catch { return false; }
        }

        private static object CreateOnnxSession(string modelPath)
        {
            try
            {
                var asm = System.Reflection.Assembly.Load("Microsoft.ML.OnnxRuntime");
                var sessionType = asm.GetType("Microsoft.ML.OnnxRuntime.InferenceSession");
                return Activator.CreateInstance(sessionType, modelPath);
            }
            catch { return null; }
        }

        private static string[] RunOnnxInference(float[] inputData)
        {
            if (_onnxSession == null || _labels == null) return null;
            try
            {
                var asm = System.Reflection.Assembly.Load("Microsoft.ML.OnnxRuntime");

                var tensorType = asm.GetType("Microsoft.ML.OnnxRuntime.Tensors.DenseTensor`1")
                    .MakeGenericType(typeof(float));
                var dims = new long[] { 1, 3, 224, 224 };
                var tensor = Activator.CreateInstance(tensorType, inputData, dims);

                var namedValueType = asm.GetType("Microsoft.ML.OnnxRuntime.NamedOnnxValue");
                var createMethod = namedValueType
                    .GetMethod("CreateFromTensor")
                    .MakeGenericMethod(typeof(float));
                var namedInput = createMethod.Invoke(null, new object[] { "input", tensor });

                var inputList = Array.CreateInstance(namedValueType, 1);
                inputList.SetValue(namedInput, 0);

                var runMethod = _onnxSession.GetType().GetMethod("Run",
                    new[] { typeof(System.Collections.Generic.IEnumerable<>)
                        .MakeGenericType(namedValueType) });
                var outputs = runMethod.Invoke(_onnxSession, new object[] { inputList });

                var enumerator = ((System.Collections.IEnumerable)outputs).GetEnumerator();
                if (!enumerator.MoveNext()) return null;
                var firstOutput = enumerator.Current;

                var valueProp = firstOutput.GetType().GetProperty("Value");
                if (valueProp == null) return null;
                var outputTensor = valueProp.GetValue(firstOutput);

                var toArray = outputTensor.GetType().GetMethod("ToArray");
                var scores = (float[])toArray.Invoke(outputTensor, null);

                var indexed = scores
                    .Select((s, i) => new { S = s, I = i })
                    .OrderByDescending(x => x.S)
                    .Take(5)
                    .Where(x => x.I < _labels.Length)
                    .Select(x => _labels[x.I])
                    .ToArray();

                return indexed;
            }
            catch { return null; }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static float[] LoadImageAsTensor(string imagePath)
        {
            try
            {
                using var bmp     = new System.Drawing.Bitmap(imagePath);
                using var resized = new System.Drawing.Bitmap(bmp, 224, 224);

                float[] tensor = new float[3 * 224 * 224];
                float[] mean = { 0.485f, 0.456f, 0.406f };
                float[] std = { 0.229f, 0.224f, 0.225f };

                for (int y = 0; y < 224; y++)
                {
                    for (int x = 0; x < 224; x++)
                    {
                        var px = resized.GetPixel(x, y);
                        tensor[0 * 224 * 224 + y * 224 + x] = (px.R / 255f - mean[0]) / std[0];
                        tensor[1 * 224 * 224 + y * 224 + x] = (px.G / 255f - mean[1]) / std[1];
                        tensor[2 * 224 * 224 + y * 224 + x] = (px.B / 255f - mean[2]) / std[2];
                    }
                }
                return tensor;
            }
            catch { return null; }
        }

        private static double CalcLumScore(double lum)
        {
            if (lum < 30) return 1.0;
            if (lum < 60) return 4.0;
            if (lum < 80) return 6.5;
            if (lum <= 180) return 10.0;
            if (lum <= 210) return 7.0;
            return 3.0;
        }

        private static VisionResult MakeResult(double score, string reason)
        {
            return new VisionResult { Score = score, RejectReason = reason };
        }

        private static async Task<bool> DownloadFileAsync(
            string url, string destPath, CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var stream = await client.GetStreamAsync(url, ct);
                using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(file, ct);
                return true;
            }
            catch { return false; }
        }
    }
}
