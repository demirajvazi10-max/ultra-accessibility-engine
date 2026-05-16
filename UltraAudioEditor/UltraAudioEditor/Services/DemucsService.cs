using System.Diagnostics;
using System.IO;

namespace UltraAudioEditor.Services
{
    /// <summary>
    /// Vokal/instrumental razdvajanje pomoću Demucs (Meta AI).
    /// Demucs se poziva kao eksterni subprocess — identično kako Audacity i drugi editore rade.
    ///
    /// INSTALACIJA (jednom):
    ///   pip install demucs
    ///   ili: winget install Python.Python.3  pa  pip install demucs
    ///
    /// Modeli koje podržavamo:
    ///   htdemucs       — najbrži, 2 stema (vocals + no_vocals)
    ///   htdemucs_ft    — finiji, 4 stema (drums, bass, other, vocals)  ← default
    ///   mdx_extra      — visok kvalitet, sporiji
    /// </summary>
    public class DemucsService
    {
        public enum StemMode
        {
            TwoStems,   // vocals + no_vocals (instrumental)
            FourStems   // drums + bass + other + vocals
        }

        public bool IsAvailable => FindPython() != null;
        public string StatusMessage { get; private set; } = "";

        // ── Provjera da li je Demucs instaliran ───────────────────────────
        public async Task<bool> CheckAvailableAsync()
        {
            string? python = FindPython();
            if (python == null)
            {
                StatusMessage = "Python nije pronađen. Instalirajte Python 3.8+ sa python.org";
                return false;
            }
            try
            {
                var result = await RunCommandAsync(python, "-m demucs --help", "", null, CancellationToken.None);
                if (result.ExitCode != 0)
                {
                    StatusMessage = "Demucs nije instaliran. Pokrenite: pip install demucs";
                    return false;
                }
                StatusMessage = "Demucs je dostupan.";
                return true;
            }
            catch
            {
                StatusMessage = "Demucs nije instaliran. Pokrenite: pip install demucs";
                return false;
            }
        }

        // ── Glavna metoda za razdvajanje ──────────────────────────────────
        public async Task<DemucsResult> SeparateAsync(
            string inputFilePath,
            string outputDirectory,
            StemMode mode = StemMode.TwoStems,
            string model = "htdemucs",
            IProgress<(int Percent, string Status)>? progress = null,
            CancellationToken ct = default)
        {
            string? python = FindPython();
            if (python == null)
                throw new Exception("Python nije pronađen. Instalirajte Python 3.8+ sa python.org.");

            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException("Audio fajl nije pronađen.", inputFilePath);

            Directory.CreateDirectory(outputDirectory);

            // Demucs argumenti
            string stems = mode == StemMode.TwoStems ? "--two-stems vocals" : "";
            string args = $"-m demucs {stems} --name {model} --out \"{outputDirectory}\" \"{inputFilePath}\"";

            progress?.Report((5, "Pokretanje Demucs..."));

            var result = await RunCommandAsync(python, args, outputDirectory, progress, ct);

            if (result.ExitCode != 0)
                throw new Exception($"Demucs greška:\n{result.StdErr}");

            // Pronađi outpute — Demucs kreira: outputDir/model/track_name/*.wav
            string trackName = Path.GetFileNameWithoutExtension(inputFilePath);
            string stemDir   = Path.Combine(outputDirectory, model, trackName);

            progress?.Report((90, "Tražim izlazne fajlove..."));

            if (!Directory.Exists(stemDir))
            {
                // Neki Demucs build-ovi koriste drugačiji path
                var found = Directory.GetDirectories(outputDirectory, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(d => Directory.GetFiles(d, "*.wav").Length > 0);
                stemDir = found ?? outputDirectory;
            }

            var wavFiles = Directory.GetFiles(stemDir, "*.wav");
            progress?.Report((100, "Gotovo!"));

            return new DemucsResult
            {
                StemDirectory = stemDir,
                VocalsPath    = wavFiles.FirstOrDefault(f => f.Contains("vocals")),
                NoVocalsPath  = wavFiles.FirstOrDefault(f => f.Contains("no_vocals") || f.Contains("instrumental")),
                DrumsPath     = wavFiles.FirstOrDefault(f => f.Contains("drums")),
                BassPath      = wavFiles.FirstOrDefault(f => f.Contains("bass")),
                OtherPath     = wavFiles.FirstOrDefault(f => f.Contains("other")),
                AllStems      = wavFiles.ToList()
            };
        }

        // ── Async subprocess runner ───────────────────────────────────────
        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCommandAsync(
            string executable, string arguments, string workingDir,
            IProgress<(int, string)>? progress, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = executable,
                Arguments              = arguments,
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = new Process { StartInfo = psi };
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stdout.AppendLine(e.Data);
                // Demucs ispisuje progress kao: "Separating track ..."  ili  "100%"
                if (e.Data.Contains('%') && int.TryParse(
                    new string(e.Data.TakeWhile(c => char.IsDigit(c)).ToArray()), out int pct))
                    progress?.Report((Math.Clamp(5 + pct * 80 / 100, 5, 85), $"Demucs: {pct}%"));
                else if (e.Data.Length > 0)
                    progress?.Report((-1, e.Data.Trim()));
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await Task.Run(() => proc.WaitForExit(), ct);

            return (proc.ExitCode, stdout.ToString(), stderr.ToString());
        }

        // ── Pronalaženje Pythona ───────────────────────────────────────────
        private static string? FindPython()
        {
            string[] candidates = { "python", "python3", "py",
                @"C:\Python312\python.exe", @"C:\Python311\python.exe",
                @"C:\Python310\python.exe", @"C:\Python39\python.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Python\Python312\python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Python\Python311\python.exe"),
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate, Arguments = "--version",
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(2000);
                    if (p?.ExitCode == 0) return candidate;
                }
                catch { }
            }
            return null;
        }
    }

    public class DemucsResult
    {
        public string  StemDirectory { get; init; } = "";
        public string? VocalsPath    { get; init; }
        public string? NoVocalsPath  { get; init; }
        public string? DrumsPath     { get; init; }
        public string? BassPath      { get; init; }
        public string? OtherPath     { get; init; }
        public List<string> AllStems { get; init; } = new();
    }
}
