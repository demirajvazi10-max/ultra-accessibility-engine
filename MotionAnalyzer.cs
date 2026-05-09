using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    public enum MotionDirection
    {
        Static, Left, Right, Up, Down, TowardCamera, AwayCamera, Mixed, Unknown
    }

    public class MotionResult
    {
        public MotionDirection Direction { get; set; }
        public MotionDirection EndDirection { get; set; }
        public double Magnitude { get; set; }
        public bool IsStatic { get { return Direction == MotionDirection.Static; } }
        public bool HasStrongMotion { get { return Magnitude > 30; } }

        public static bool IsCompatible(MotionResult prev, MotionResult next)
        {
            if (prev == null || next == null) return true;
            if (prev.IsStatic || next.IsStatic) return true;
            if (prev.Direction == MotionDirection.Unknown || next.Direction == MotionDirection.Unknown) return true;
            if (prev.Direction == MotionDirection.Mixed || next.Direction == MotionDirection.Mixed) return true;
            if (prev.EndDirection == next.Direction) return true;

            if (prev.EndDirection == MotionDirection.Right && next.Direction == MotionDirection.Right) return true;
            if (prev.EndDirection == MotionDirection.Left && next.Direction == MotionDirection.Left) return true;
            if (prev.EndDirection == MotionDirection.Up && (next.Direction == MotionDirection.Up || next.Direction == MotionDirection.TowardCamera)) return true;
            if (prev.EndDirection == MotionDirection.Down && (next.Direction == MotionDirection.Down || next.Direction == MotionDirection.AwayCamera)) return true;
            if (prev.EndDirection == MotionDirection.TowardCamera && (next.Direction == MotionDirection.TowardCamera || next.Direction == MotionDirection.Up)) return true;
            if (prev.EndDirection == MotionDirection.AwayCamera && (next.Direction == MotionDirection.AwayCamera || next.Direction == MotionDirection.Down)) return true;

            return false;
        }
    }

    public static class MotionAnalyzer
    {
        private static readonly Dictionary<string, MotionResult> _cache =
            new Dictionary<string, MotionResult>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        public static async Task<MotionResult> AnalyzeAsync(
            string videoPath,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(videoPath) || !File.Exists(ffmpegPath))
                return MakeUnknown();

            await _cacheLock.WaitAsync(ct);
            bool found = _cache.TryGetValue(videoPath, out MotionResult cached);
            _cacheLock.Release();
            if (found) return cached;

            var result = await DoAnalyze(videoPath, ffmpegPath, ct, 0, 3.0);

            await _cacheLock.WaitAsync(ct);
            _cache[videoPath] = result;
            _cacheLock.Release();

            return result;
        }

        public static async Task<MotionResult> AnalyzeEndAsync(
            string videoPath,
            string ffmpegPath,
            double clipDuration,
            double analyzeLastSeconds = 2.0,
            CancellationToken ct = default)
        {
            if (!File.Exists(videoPath) || !File.Exists(ffmpegPath))
                return MakeUnknown();

            double startAt = Math.Max(0, clipDuration - analyzeLastSeconds);
            return await DoAnalyze(videoPath, ffmpegPath, ct, startAt, analyzeLastSeconds);
        }

        public static async Task<List<string>> FilterCompatibleAsync(
            List<string> candidatePaths,
            MotionResult previousClipMotion,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (previousClipMotion == null || previousClipMotion.IsStatic ||
                candidatePaths == null || candidatePaths.Count == 0)
                return candidatePaths;

            var compatible = new List<string>();

            foreach (var path in candidatePaths)
            {
                var motion = await AnalyzeAsync(path, ffmpegPath, ct);
                if (MotionResult.IsCompatible(previousClipMotion, motion))
                    compatible.Add(path);
            }

            return compatible.Count > 0 ? compatible : candidatePaths;
        }

        public static void ClearCache()
        {
            _cache.Clear();
        }

        private static async Task<MotionResult> DoAnalyze(
            string videoPath, string ffmpegPath, CancellationToken ct,
            double startAt, double duration)
        {
            try
            {
                string seekPart = startAt > 0.5
                    ? "-ss " + startAt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " "
                    : "";
                string durStr = duration.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

                string args = "-nostdin " + seekPart + "-i \"" + videoPath + "\" " +
                    "-t " + durStr + " " +
                    "-vf \"mestimate=method=ds:search_param=7,metadata=print:key=lavfi.motion.estimation_avg\" " +
                    "-f null -an -";

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
                if (proc == null) return MakeUnknown();

                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);

                return ParseMotionOutput(stderr);
            }
            catch
            {
                return await FallbackMotionDetect(videoPath, ffmpegPath, ct, startAt);
            }
        }

        private static MotionResult ParseMotionOutput(string ffmpegOutput)
        {
            var xValues = new List<double>();
            var yValues = new List<double>();

            // Pattern: lavfi.motion.estimation_avg=X,Y
            string pattern = @"lavfi\.motion\.estimation_avg=([-0-9.]+),([-0-9.]+)";
            var matches = Regex.Matches(ffmpegOutput, pattern);

            foreach (Match m in matches)
            {
                double xVal, yVal;
                if (double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out xVal))
                {
                    xValues.Add(xVal);
                }
                if (double.TryParse(m.Groups[2].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out yVal))
                {
                    yValues.Add(yVal);
                }
            }

            if (xValues.Count < 3) return MakeUnknown();

            double avgX = xValues.Average();
            double avgY = yValues.Average();
            double magnitude = Math.Sqrt(avgX * avgX + avgY * avgY);
            double normalizedMag = Math.Min(100, magnitude * 5);

            if (normalizedMag < 5)
                return new MotionResult { Direction = MotionDirection.Static, EndDirection = MotionDirection.Static, Magnitude = normalizedMag };

            double threshold = 2.0;
            bool strongX = Math.Abs(avgX) > threshold;
            bool strongY = Math.Abs(avgY) > threshold;

            MotionDirection dir;
            if (strongX && strongY)
            {
                if (Math.Abs(avgX) > Math.Abs(avgY) * 1.5)
                    dir = avgX > 0 ? MotionDirection.Right : MotionDirection.Left;
                else if (Math.Abs(avgY) > Math.Abs(avgX) * 1.5)
                    dir = avgY > 0 ? MotionDirection.Down : MotionDirection.Up;
                else
                    dir = MotionDirection.Mixed;
            }
            else if (strongX)
                dir = avgX > 0 ? MotionDirection.Right : MotionDirection.Left;
            else if (strongY)
                dir = avgY > 0 ? MotionDirection.Down : MotionDirection.Up;
            else
                dir = MotionDirection.Static;

            return new MotionResult
            {
                Direction = dir,
                EndDirection = dir,
                Magnitude = Math.Round(normalizedMag, 1)
            };
        }

        private static async Task<MotionResult> FallbackMotionDetect(
            string videoPath, string ffmpegPath, CancellationToken ct, double startAt)
        {
            string tempA = Path.Combine(Path.GetTempPath(),
                "motA_" + Guid.NewGuid().ToString().Substring(0, 6) + ".png");
            string tempB = Path.Combine(Path.GetTempPath(),
                "motB_" + Guid.NewGuid().ToString().Substring(0, 6) + ".png");

            try
            {
                string seekA = startAt > 0.5
                    ? "-ss " + startAt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " "
                    : "";
                double timeB = startAt + 0.5;
                string timeBStr = timeB.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

                await RunFfmpegAsync(ffmpegPath,
                    "-nostdin " + seekA + "-i \"" + videoPath + "\" -vframes 1 -vf scale=128:72 -q:v 5 -y \"" + tempA + "\"", ct);

                await RunFfmpegAsync(ffmpegPath,
                    "-nostdin -ss " + timeBStr + " -i \"" + videoPath + "\" -vframes 1 -vf scale=128:72 -q:v 5 -y \"" + tempB + "\"", ct);

                if (!File.Exists(tempA) || !File.Exists(tempB))
                    return MakeUnknown();

                return await EstimateFromFrames(tempA, tempB, ffmpegPath, ct);
            }
            catch
            {
                return MakeUnknown();
            }
            finally
            {
                try { if (File.Exists(tempA)) File.Delete(tempA); } catch { }
                try { if (File.Exists(tempB)) File.Delete(tempB); } catch { }
            }
        }

        private static async Task<MotionResult> EstimateFromFrames(
            string frameA, string frameB, string ffmpegPath, CancellationToken ct)
        {
            try
            {
                string args = "-nostdin -i \"" + frameA + "\" -i \"" + frameB + "\" " +
                    "-lavfi \"[0:v][1:v]phase_correlation\" -f null -";

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
                if (proc == null) return MakeUnknown();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);

                // Pattern: x:NUMBER y:NUMBER
                string pattern = @"x:([-0-9.]+)\s+y:([-0-9.]+)";
                var m = Regex.Match(stderr, pattern);
                if (m.Success)
                {
                    double dx, dy;
                    bool okX = double.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out dx);
                    bool okY = double.TryParse(m.Groups[2].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out dy);

                    if (okX && okY)
                    {
                        double mag = Math.Sqrt(dx * dx + dy * dy);
                        double normalizedMag = Math.Min(100, mag * 10);

                        if (normalizedMag < 5)
                            return new MotionResult { Direction = MotionDirection.Static, EndDirection = MotionDirection.Static, Magnitude = normalizedMag };

                        MotionDirection dir;
                        if (Math.Abs(dx) > Math.Abs(dy))
                            dir = dx > 0 ? MotionDirection.Right : MotionDirection.Left;
                        else
                            dir = dy > 0 ? MotionDirection.Down : MotionDirection.Up;

                        return new MotionResult { Direction = dir, EndDirection = dir, Magnitude = normalizedMag };
                    }
                }
            }
            catch { }

            return MakeUnknown();
        }

        private static async Task RunFfmpegAsync(string ffmpegPath, string args, CancellationToken ct)
        {
            try
            {
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
                if (proc != null) await proc.WaitForExitAsync(ct);
            }
            catch { }
        }

        private static MotionResult MakeUnknown()
        {
            return new MotionResult { Direction = MotionDirection.Unknown, EndDirection = MotionDirection.Unknown, Magnitude = 0 };
        }
    }
}
