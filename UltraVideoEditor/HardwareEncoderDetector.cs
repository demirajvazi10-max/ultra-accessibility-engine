using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    public class HardwareEncoderInfo
    {
        public string Name { get; set; }          // npr. "h264_nvenc"
        public string DisplayName { get; set; }   // npr. "NVIDIA NVENC"
        public int Priority { get; set; }         // 1 = najbolji
        public bool IsAvailable { get; set; }
        public string TestError { get; set; }
    }

    public static class HardwareEncoderDetector
    {
        private static string ffmpegPath;
        private static List<HardwareEncoderInfo> _cachedEncoders;
        // SemaphoreSlim sprečava race condition ako se GetAvailableEncodersAsync pozove paralelno
        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        static HardwareEncoderDetector()
        {
            ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
        }

        /// <summary>
        /// Vraća listu svih dostupnih hardverskih enkodera za H.264
        /// </summary>
        public static async Task<List<HardwareEncoderInfo>> GetAvailableEncodersAsync(string codec = "h264")
        {
            // Brza provjera bez locka
            if (_cachedEncoders != null)
                return _cachedEncoders;

            // Lock sprečava duplo testiranje enkodera ako se pozove paralelno
            await _cacheLock.WaitAsync();
            try
            {
                // Ponovna provjera unutar locka (double-checked locking)
                if (_cachedEncoders != null)
                    return _cachedEncoders;

                var possibleEncoders = new List<HardwareEncoderInfo>
                {
                    new HardwareEncoderInfo { Name = $"{codec}_nvenc", DisplayName = "NVIDIA NVENC", Priority = 1 },
                    new HardwareEncoderInfo { Name = $"{codec}_qsv", DisplayName = "Intel QuickSync", Priority = 2 },
                    new HardwareEncoderInfo { Name = $"{codec}_amf", DisplayName = "AMD AMF", Priority = 3 },
                    new HardwareEncoderInfo { Name = $"{codec}_vaapi", DisplayName = "VAAPI (Linux/Intel/AMD)", Priority = 4 },
                    new HardwareEncoderInfo { Name = $"{codec}_videotoolbox", DisplayName = "Apple VideoToolbox", Priority = 5 },
                    new HardwareEncoderInfo { Name = $"{codec}_v4l2m2m", DisplayName = "V4L2 M2M (Linux/ARM)", Priority = 6 },
                };

                var tempDir = Path.GetTempPath();
                var testInputFile = Path.Combine(tempDir, "hw_test_input.mp4");

                if (!File.Exists(testInputFile))
                    await CreateTestVideoFile(testInputFile);

                foreach (var encoder in possibleEncoders)
                    encoder.IsAvailable = await TestEncoderAsync(encoder.Name, testInputFile);

                _cachedEncoders = possibleEncoders.Where(e => e.IsAvailable).OrderBy(e => e.Priority).ToList();

                try { File.Delete(testInputFile); } catch { }

                return _cachedEncoders;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Vraća najbolji dostupni hardverski enkoder (ili null ako nijedan ne radi)
        /// </summary>
        public static async Task<string> GetBestEncoderAsync(string codec = "h264")
        {
            var available = await GetAvailableEncodersAsync(codec);
            return available.FirstOrDefault()?.Name;
        }

        /// <summary>
        /// Vraća odgovarajuće parametre za FFmpeg na osnovu odabranog enkodera
        /// </summary>
        public static string GetEncoderParams(string encoderName, string quality = "high")
        {
            if (string.IsNullOrEmpty(encoderName))
                return "-c:v libx264 -crf 23";

            if (encoderName.Contains("nvenc"))
            {
                return quality == "high"
                    ? $"-c:v {encoderName} -preset p7 -tune hq -rc vbr -cq 23 -b:v 0"
                    : $"-c:v {encoderName} -preset p1 -rc cbr -b:v 5M";
            }
            else if (encoderName.Contains("qsv"))
            {
                return quality == "high"
                    ? $"-c:v {encoderName} -preset slow -global_quality 23"
                    : $"-c:v {encoderName} -preset veryfast -global_quality 35";
            }
            else if (encoderName.Contains("amf"))
            {
                return quality == "high"
                    ? $"-c:v {encoderName} -quality quality -rc cbr -b:v 8M"
                    : $"-c:v {encoderName} -quality speed -rc cbr -b:v 4M";
            }
            else if (encoderName.Contains("vaapi"))
            {
                return $"-c:v {encoderName} -global_quality 23";
            }
            else if (encoderName.Contains("videotoolbox"))
            {
                return $"-c:v {encoderName} -q:v 65";
            }

            return "-c:v libx264 -crf 23";
        }

        /// <summary>
        /// Testira da li određeni enkoder radi
        /// </summary>
        private static async Task<bool> TestEncoderAsync(string encoderName, string testInputFile)
        {
            string outputFile = Path.GetTempFileName() + ".mp4";

            var args = $"-y -hide_banner -loglevel error -t 1 -i \"{testInputFile}\" -c:v {encoderName} -f mp4 \"{outputFile}\"";

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                bool success = process.ExitCode == 0 && File.Exists(outputFile);

                try { File.Delete(outputFile); } catch { }

                return success;
            }
            catch
            {
                try { File.Delete(outputFile); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Kreira test video fajl za proveru enkodera
        /// </summary>
        private static async Task CreateTestVideoFile(string outputPath)
        {
            // Generiše 1 sekundu test videa (crveni ekran)
            var args = $"-y -hide_banner -loglevel error -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -c:v libx264 -t 1 \"{outputPath}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };

            process.Start();
            await process.WaitForExitAsync();
        }

        /// <summary>
        /// Resetuje keš (pozovite ako se hardver promeni)
        /// </summary>
        public static void ResetCache()
        {
            _cachedEncoders = null;
        }
    }
}