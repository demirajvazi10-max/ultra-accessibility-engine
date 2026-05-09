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

        public static async Task<VisionResult> AnalyzeClipAsync(
            string videoPath,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(videoPath))
                return MakeResult(5.0, "Fajl ne postoji");

            string tempFrame = Path.Combine(
                Path.GetTempPath(),
                "vision_" + Guid.NewGuid().ToString().Substring(0, 8) + ".png");

            try
            {
                bool extracted = await ExtractFrame(videoPath, tempFrame, ffmpegPath, ct);
                if (!extracted)
                    return await FfmpegAnalyze(videoPath, ffmpegPath, ct);

                if (_onnxAvailable && _onnxSession != null)
                {
                    var onnxResult = await OnnxAnalyzeFrame(tempFrame, ct);
                    if (onnxResult != null)
                    {
                        var ffResult = await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
                        onnxResult.Luminance = ffResult.Luminance;
                        onnxResult.Saturation = ffResult.Saturation;
                        onnxResult.Sharpness = ffResult.Sharpness;

                        double sharpScore = Math.Min(10.0, onnxResult.Sharpness / 10.0);
                        double lumScore = CalcLumScore(onnxResult.Luminance);
                        onnxResult.Score = Math.Round(
                            onnxResult.Score * 0.5 + sharpScore * 0.3 + lumScore * 0.2, 1);

                        return onnxResult;
                    }
                }

                return await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFrame)) File.Delete(tempFrame);
                }
                catch { }
            }
        }

        private static async Task<bool> ExtractFrame(
            string videoPath, string outputPath, string ffmpegPath, CancellationToken ct)
        {
            try
            {
                string args = "-nostdin -ss 1 -i \"" + videoPath +
                    "\" -vframes 1 -vf scale=224:224 -q:v 2 -y \"" + outputPath + "\"";
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
                if (proc == null) return false;
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0 && File.Exists(outputPath);
            }
            catch { return false; }
        }

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

        private static float[] LoadImageAsTensor(string imagePath)
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(imagePath);
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
