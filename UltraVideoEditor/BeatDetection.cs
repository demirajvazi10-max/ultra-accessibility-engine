using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // BEAT DETECTION ENGINE
    // ═══════════════════════════════════════════════════════════════

    public class BeatInfo
    {
        public double BPM { get; set; }
        public double BeatInterval { get; set; }
        public List<double> BeatTimes { get; set; }
        public List<double> DownBeats { get; set; }
        public string TimeSignature { get; set; }
        public double Confidence { get; set; }
        public bool IsValid => BPM > 30 && BPM < 300 && BeatTimes?.Count > 4;
    }

    public class BeatSyncPlan
    {
        public double ClipDuration { get; set; }
        public int BeatsPerClip { get; set; }
        public string SceneType { get; set; }
        public string Reason { get; set; }
    }

    public static class BeatDetection
    {
        public static async Task<BeatInfo> AnalyzeAudio(
            string audioPath,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(audioPath) || !File.Exists(ffmpegPath))
                return FallbackBeatInfo(120);

            try
            {
                string tempWav = Path.Combine(Path.GetTempPath(), $"beat_{Guid.NewGuid().ToString().Substring(0, 8)}.wav");
                bool extracted = await ExtractMonoAudio(audioPath, tempWav, ffmpegPath, ct);
                if (!extracted) return FallbackBeatInfo(120);

                var energyProfile = await GetEnergyProfile(tempWav, ffmpegPath, ct);
                var beatTimes = DetectBeatsFromEnergy(energyProfile);
                double bpm = CalculateBPM(beatTimes);
                var downBeats = GetDownBeats(beatTimes, bpm);

                try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }

                return new BeatInfo
                {
                    BPM = Math.Round(bpm, 1),
                    BeatInterval = bpm > 0 ? Math.Round(60.0 / bpm, 4) : 0.5,
                    BeatTimes = beatTimes,
                    DownBeats = downBeats,
                    TimeSignature = EstimateTimeSignature(bpm),
                    Confidence = CalculateConfidence(beatTimes, bpm)
                };
            }
            catch (OperationCanceledException) { throw; }
            catch { return FallbackBeatInfo(120); }
        }

        public static BeatSyncPlan GetSyncPlan(BeatInfo beats, string sceneType, int vibeScore)
        {
            if (!beats.IsValid)
            {
                double fallbackDur = sceneType switch
                {
                    "lullaby" => 6.0,
                    "chorus" => 2.0,
                    "intro" => 5.0,
                    "outro" => 6.0,
                    _ => 4.0
                };
                return new BeatSyncPlan
                {
                    ClipDuration = fallbackDur,
                    BeatsPerClip = 0,
                    SceneType = sceneType,
                    Reason = "Beat sync nije dostupan - koristim fiksno trajanje"
                };
            }

            double interval = beats.BeatInterval;
            int beatsPerClip = (sceneType, vibeScore) switch
            {
                ("lullaby", _) => 8,
                ("outro", _) => 8,
                ("intro", _) => 6,
                ("chorus", >= 8) => 2,
                ("chorus", >= 5) => 4,
                ("chorus", _) => 4,
                (_, >= 8) => 2,
                (_, >= 6) => 4,
                (_, >= 4) => 4,
                _ => 8
            };

            double raw = interval * beatsPerClip;
            while (raw < 0.8 && beatsPerClip < 16)
            {
                beatsPerClip *= 2;
                raw = interval * beatsPerClip;
            }
            while (raw > 8.0 && beatsPerClip > 1)
            {
                beatsPerClip = Math.Max(1, beatsPerClip / 2);
                raw = interval * beatsPerClip;
            }

            double clipDuration = Math.Round(interval * beatsPerClip, 3);
            string reason = $"{beats.BPM:F0} BPM, {beatsPerClip} beata po klipu = {clipDuration:F1}s po klipu ({sceneType}, vibe {vibeScore})";

            return new BeatSyncPlan
            {
                ClipDuration = clipDuration,
                BeatsPerClip = beatsPerClip,
                SceneType = sceneType,
                Reason = reason
            };
        }

        public static void ApplyBeatSync(List<LyricShot> shots, BeatInfo beats, double totalDuration)
        {
            if (!beats.IsValid || shots == null || shots.Count == 0) return;

            double cursor = 0;
            for (int i = 0; i < shots.Count; i++)
            {
                var shot = shots[i];
                string sceneType = shot.IsChorus ? "chorus" :
                    shot.Data.VibeScore <= 2 ? "lullaby" :
                    i == 0 ? "intro" :
                    i == shots.Count - 1 ? "outro" : "verse";

                var plan = GetSyncPlan(beats, sceneType, shot.Data.VibeScore);
                double duration = (i == shots.Count - 1) ? Math.Round(totalDuration - cursor, 3) : plan.ClipDuration;
                duration = Math.Max(0.5, duration);

                shot.StartSeconds = cursor;
                shot.EndSeconds = cursor + duration;
                shot.Timestamp = $"{FmtTs(cursor)} - {FmtTs(cursor + duration)}";

                if (shot.Data != null && plan.BeatsPerClip > 0)
                    shot.Data.MotionIntent = $"{shot.Data.MotionIntent} [beat sync: {plan.BeatsPerClip} beata]";

                cursor += duration;
            }
        }

        private static async Task<bool> ExtractMonoAudio(string input, string output, string ffmpegPath, CancellationToken ct)
        {
            string args = $"-nostdin -i \"{input}\" -ac 1 -ar 22050 -vn -y \"{output}\"";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0 && File.Exists(output);
            }
            catch { return false; }
        }

        private static async Task<List<(double time, double energy)>> GetEnergyProfile(string wavPath, string ffmpegPath, CancellationToken ct)
        {
            string tempLog = Path.Combine(Path.GetTempPath(), $"energy_{Guid.NewGuid().ToString().Substring(0, 8)}.txt");
            string args = $"-nostdin -i \"{wavPath}\" -af \"astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file={tempLog}\" -f null -";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync(ct);
            }
            catch { }

            var profile = new List<(double time, double energy)>();
            if (!File.Exists(tempLog)) return profile;

            string logContent = await File.ReadAllTextAsync(tempLog, ct);
            try { File.Delete(tempLog); } catch { }

            var lines = logContent.Split('\n');
            double currentTime = 0;
            foreach (var line in lines)
            {
                if (line.Contains("pts_time:"))
                {
                    var m = Regex.Match(line, @"pts_time:([\d.]+)");
                    if (m.Success)
                        double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out currentTime);
                }
                else if (line.Contains("RMS_level="))
                {
                    var m = Regex.Match(line, @"RMS_level=([-\d.]+|inf|-inf)");
                    if (m.Success)
                    {
                        string valStr = m.Groups[1].Value;
                        double rms = valStr == "-inf" || valStr == "inf" ? -100.0 : double.Parse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture);
                        double linear = rms > -100 ? Math.Pow(10, rms / 20.0) : 0;
                        profile.Add((currentTime, linear));
                    }
                }
            }
            return profile;
        }

        private static List<double> DetectBeatsFromEnergy(List<(double time, double energy)> profile)
        {
            if (profile.Count < 10) return new List<double>();

            int windowSize = Math.Max(10, profile.Count / 20);
            var beats = new List<double>();
            double minBeatGap = 0.2;
            double lastBeat = -1.0;

            for (int i = windowSize; i < profile.Count - windowSize; i++)
            {
                double localAvg = profile.Skip(i - windowSize).Take(windowSize * 2).Average(p => p.energy);
                double current = profile[i].energy;
                bool isLocalMax = current > profile[i - 1].energy && current > profile[i + 1].energy;
                bool aboveThreshold = current > localAvg * 1.3;

                if (isLocalMax && aboveThreshold)
                {
                    double t = profile[i].time;
                    if (t - lastBeat >= minBeatGap)
                    {
                        beats.Add(t);
                        lastBeat = t;
                    }
                }
            }
            return beats;
        }

        private static double CalculateBPM(List<double> beatTimes)
        {
            if (beatTimes.Count < 4) return 120;

            var intervals = new List<double>();
            for (int i = 1; i < beatTimes.Count; i++)
                intervals.Add(beatTimes[i] - beatTimes[i - 1]);

            intervals.Sort();
            double median = intervals[intervals.Count / 2];
            var filtered = intervals.Where(x => x > median * 0.5 && x < median * 2.0).ToList();

            if (filtered.Count == 0) return 120;

            double avgInterval = filtered.Average();
            double bpm = 60.0 / avgInterval;

            while (bpm < 60) bpm *= 2;
            while (bpm > 200) bpm /= 2;

            return bpm;
        }

        private static List<double> GetDownBeats(List<double> beats, double bpm)
        {
            if (beats.Count == 0) return new List<double>();
            int step = bpm < 80 ? 3 : 4;
            return beats.Where((_, idx) => idx % step == 0).ToList();
        }

        private static string EstimateTimeSignature(double bpm)
        {
            if (bpm >= 55 && bpm <= 85) return "3/4 (valcer)";
            return "4/4";
        }

        private static double CalculateConfidence(List<double> beats, double bpm)
        {
            if (beats.Count < 4) return 0;
            double interval = 60.0 / bpm;
            int consistent = 0;
            for (int i = 1; i < beats.Count; i++)
            {
                double diff = Math.Abs((beats[i] - beats[i - 1]) - interval);
                if (diff < interval * 0.15) consistent++;
            }
            return Math.Round((double)consistent / (beats.Count - 1), 2);
        }

        private static BeatInfo FallbackBeatInfo(double bpm)
        {
            double interval = 60.0 / bpm;
            return new BeatInfo
            {
                BPM = bpm,
                BeatInterval = interval,
                BeatTimes = new List<double>(),
                DownBeats = new List<double>(),
                TimeSignature = "4/4",
                Confidence = 0
            };
        }

        private static string FmtTs(double s) => TimeSpan.FromSeconds(s).ToString(@"m\:ss\.ff");
    }
}