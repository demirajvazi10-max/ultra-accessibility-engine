using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfMessageBox  = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using WpfButton      = System.Windows.Controls.Button;
using Brush          = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color          = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace UltraVideoEditor
{
    public partial class VideoReviewDialog : Window
    {
        // ── Language helper ───────────────────────────────────────────────────
        private string _lang => (WpfApplication.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _lang);

        // ── State ─────────────────────────────────────────────────────────────
        private CancellationTokenSource _cts;
        private OllamaClient _ollama;
        private string _videoPath;
        private readonly string _ffmpegPath;
        private readonly string _feedbackFile;

        // ETA tracking
        private DateTime  _analysisStartTime;
        private int       _totalFrames;
        private int       _processedFrames;

        private ObservableCollection<FindingItem> _findings = new();

        // ── Konstruktor ───────────────────────────────────────────────────────
        public VideoReviewDialog()
        {
            InitializeComponent();

            _ffmpegPath  = FindFfmpegPath();
            _feedbackFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UltraVideoEditor", "review_feedback.txt");

            lstFindings.ItemsSource = _findings;

            chkTeachMode.Checked   += (s, e) => borderTeach.Visibility = Visibility.Visible;
            chkTeachMode.Unchecked += (s, e) => borderTeach.Visibility = Visibility.Collapsed;

            // Fire-and-forget — ne čekamo, ali se izvodi čim dispatcher krene
            Dispatcher.BeginInvoke(new Action(async () => await InitOllamaAsync()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ── Pronalaženje ffmpeg.exe ───────────────────────────────────────────
        private static string FindFfmpegPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. Uz exe — podfolder Ffmpeg (originalna lokacija)
            string[] localCandidates =
            {
                Path.Combine(baseDir, "Ffmpeg", "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(baseDir, "tools", "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg.exe"),
            };
            foreach (string c in localCandidates)
                if (File.Exists(c)) return c;

            // 2. System PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                try
                {
                    string c = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(c)) return c;
                }
                catch { }
            }

            // 3. Ceste instalacije
            string[] wellKnown =
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", "ffmpeg.exe"),
            };
            foreach (string c in wellKnown)
                if (File.Exists(c)) return c;

            // Nije pronaden — vrati originalnu putanju kao placeholder za jasnu gresku
            return Path.Combine(baseDir, "Ffmpeg", "ffmpeg.exe");
        }

        // ── Inicijalizacija Ollama ────────────────────────────────────────────
        private async Task InitOllamaAsync()
        {
            // Prikazi ffmpeg status odmah na startu
            bool ffmpegOk = File.Exists(_ffmpegPath);
            AddFinding(FindingItem.Info("—", ffmpegOk
                ? $"✅ FFmpeg: {_ffmpegPath}"
                : $"❌ FFmpeg nije pronaden! Ocekivana lokacija: {_ffmpegPath}"));

            AddFinding(FindingItem.Info("—", "🔄 Provjera Ollama..."));
            SetStatus("Provjera Ollama...", "#FF9800");

            _ollama = new OllamaClient();

            bool running = false;
            try { running = await _ollama.IsOllamaRunning(); }
            catch (Exception ex)
            {
                AddFinding(FindingItem.Info("—", $"❌ Ollama greška: {ex.Message}"));
            }

            if (!running)
            {
                AddFinding(FindingItem.Info("—", "❌ Ollama nije pokrenuta — pokreni Ollama desktop app"));
                SetStatus("⚠️ Ollama nije pokrenuta — analiza neće raditi bez nje.", "#FF5722");
                // NE blokiramo dugme — korisnik može pokušati svejedno
                return;
            }

            AddFinding(FindingItem.Info("—", "✅ Ollama radi"));

            string detectedModel = null;
            foreach (string name in new[] { "qwen2.5vl", "qwen2-vl", "qwen2.5-vl", "qwen2.5vl:latest", "qwen2-vl:latest" })
            {
                try
                {
                    bool found = await _ollama.IsModelAvailable(name);
                    AddFinding(FindingItem.Info("—", $"   Model '{name}': {(found ? "✅ pronađen" : "— nije")}"));
                    if (found && detectedModel == null)
                        detectedModel = name;
                }
                catch (Exception ex)
                {
                    AddFinding(FindingItem.Info("—", $"   Model '{name}': greška — {ex.Message}"));
                }
            }

            if (detectedModel != null)
            {
                AddFinding(FindingItem.Info("—", $"🤖 Koristim model: {detectedModel}"));
                SetStatus($"✅ {detectedModel} aktivan. Izaberi video i pritisni ANALIZIRAJ.", "#00E676");
            }
            else
            {
                AddFinding(FindingItem.Info("—", "⚠️ Qwen model nije pronađen. Provjeri: ollama list"));
                SetStatus("⚠️ Qwen nije potvrđen — pokušaj svejedno.", "#FF9800");
            }
        }

        // ── UI eventi ─────────────────────────────────────────────────────────
        private void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Izaberi video za analizu",
                Filter = "Video fajlovi|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.webm|Svi fajlovi|*.*"
            };
            if (dlg.ShowDialog() == true)
                LoadVideo(dlg.FileName);
        }

        private void btnFromTimeline_Click(object sender, RoutedEventArgs e)
        {
            // Pokušaj naći posljednji eksportovani video iz MainWindow
            var main = WpfApplication.Current.MainWindow as MainWindow;
            if (main == null) return;

            string lastExport = main.timelineItems
                .Where(t => t.IsVideo && File.Exists(t.Path))
                .OrderByDescending(t => File.GetLastWriteTime(t.Path))
                .Select(t => t.Path)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(lastExport))
            {
                LoadVideo(lastExport);
            }
            else
            {
                SetStatus("Nema video fajlova na timeline-u. Koristi 'Otvori' za ručni odabir.", "#FF9800");
            }
        }

        private void LoadVideo(string path)
        {
            _videoPath       = path;
            txtVideoPath.Text = path;
            _findings.Clear();
            badgeProblems.Visibility = Visibility.Collapsed;
            badgeScore.Visibility    = Visibility.Collapsed;
            btnExport.IsEnabled      = false;
            SetStatus($"Video učitan: {Path.GetFileName(path)}", "#00E676");
        }

        private async void btnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            // DEBUG — provjera da li click uopšte stiže
            AddFinding(FindingItem.Info("—", $"▶ Klik primljen. Video: '{_videoPath}'"));
            AddFinding(FindingItem.Info("—", $"   FFmpeg: {(_ffmpegPath != null && File.Exists(_ffmpegPath) ? "✅ " + _ffmpegPath : "❌ nije nađen: " + _ffmpegPath)}"));

            if (string.IsNullOrEmpty(_videoPath) || !File.Exists(_videoPath))
            {
                SetStatus("Izaberi video fajl prije analize.", "#FF9800");
                AddFinding(FindingItem.Info("—", "❌ Video fajl ne postoji ili nije izabran."));
                return;
            }
            if (!File.Exists(_ffmpegPath))
            {
                SetStatus("FFmpeg nije pronađen. Provjeri Ffmpeg/ffmpeg.exe.", "#FF5722");
                AddFinding(FindingItem.Info("—", $"❌ FFmpeg nije pronađen na: {_ffmpegPath}"));
                return;
            }


            _cts       = new CancellationTokenSource();
            btnAnalyze.IsEnabled = false;
            btnStop.IsEnabled    = true;
            btnExport.IsEnabled  = false;
            _findings.Clear();
            badgeProblems.Visibility = Visibility.Collapsed;
            badgeScore.Visibility    = Visibility.Collapsed;
            _analysisStartTime = DateTime.Now;
            _processedFrames   = 0;
            _totalFrames       = 0;
            txtFrameCounter.Text = "";
            txtETA.Text          = "";
            txtPct.Text          = "";

            // Pročitaj sve UI vrijednosti PRIJE await-a — ne mogu se čitati iz async konteksta
            var opts = new ReviewOptions
            {
                CheckCuts    = chkCuts.IsChecked    == true,
                CheckQuality = chkQuality.IsChecked == true,
                CheckLogic   = chkLogic.IsChecked   == true,
                CheckRepeat  = chkRepeat.IsChecked  == true,
                CheckFreeze  = chkFreeze.IsChecked  == true,
                IntervalSec  = int.Parse(((cmbInterval.SelectedItem as ComboBoxItem)?.Tag as string) ?? "2")
            };

            try
            {
                await RunAnalysisAsync(_cts.Token, opts);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Analiza prekinuta.", "#FF9800");
                AddFinding(FindingItem.Info("—", "Analiza prekinuta od strane korisnika."));
            }
            catch (Exception ex)
            {
                SetStatus($"Greška: {ex.Message}", "#FF5722");
            }
            finally
            {

                btnAnalyze.IsEnabled = true;
                btnStop.IsEnabled    = false;
                btnExport.IsEnabled  = _findings.Count > 0;
            }
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void btnGoToTimestamp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is WpfButton btn && btn.Tag is double seconds)
            {
                var main = WpfApplication.Current.MainWindow as MainWindow;
                main?.SeekToPosition(seconds);
            }
        }

        private void btnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = "Sačuvaj izvještaj",
                Filter     = "Tekstualni fajl|*.txt",
                FileName   = $"VideoReview_{Path.GetFileNameWithoutExtension(_videoPath)}_{DateTime.Now:yyyyMMdd_HHmm}.txt"
            };
            if (dlg.ShowDialog() != true) return;

            var sb = new StringBuilder();
            sb.AppendLine($"AI PREGLED VIDEA — {Path.GetFileName(_videoPath)}");
            sb.AppendLine($"Datum: {DateTime.Now:dd.MM.yyyy HH:mm}");
            sb.AppendLine(new string('─', 60));
            sb.AppendLine();
            foreach (var f in _findings)
            {
                sb.AppendLine($"[{f.Timestamp}] {f.Icon} {f.Title}");
                if (!string.IsNullOrEmpty(f.Description))
                    sb.AppendLine($"    {f.Description}");
                sb.AppendLine();
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            SetStatus($"Izvještaj sačuvan: {dlg.FileName}", "#00E676");
        }

        private void btnSaveFeedback_Click(object sender, RoutedEventArgs e)
        {
            string feedback = txtTeachFeedback.Text.Trim();
            if (string.IsNullOrEmpty(feedback)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(_feedbackFile)!);
            File.AppendAllText(_feedbackFile,
                $"[{DateTime.Now:dd.MM.yyyy HH:mm}] {Path.GetFileName(_videoPath ?? "")}\n{feedback}\n\n",
                Encoding.UTF8);

            SetStatus("💾 Komentar sačuvan za trening.", "#FFD54F");
            txtTeachFeedback.Text = "";
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close();
            // Ugasi Dispatcher ovog STA threada da thread ne ostane živ
            Dispatcher.InvokeShutdown();
        }

        // ── Glavna analiza ────────────────────────────────────────────────────
        private async Task RunAnalysisAsync(CancellationToken ct, ReviewOptions opts)
        {
            AddFinding(FindingItem.Info("—", "🔄 RunAnalysisAsync — počinjem..."));

            AddFinding(FindingItem.Info("—", $"   Čitam trajanje: {_videoPath}"));
            double duration = await GetVideoDurationAsync(_videoPath, ct);
            AddFinding(FindingItem.Info("—", $"   Trajanje: {duration:F1}s"));

            if (duration <= 0)
            {
                SetStatus("Nije moguće pročitati trajanje videa.", "#FF5722");
                AddFinding(FindingItem.Info("—", "❌ Trajanje je 0 — ffprobe/ffmpeg nije uspio pročitati video."));
                return;
            }

            _totalFrames = (int)(duration / opts.IntervalSec);

            SetStatus($"Analiziram {Path.GetFileName(_videoPath)} ({duration:F0}s) — {_totalFrames} frejmova...", "#FF9800");
            AddFinding(FindingItem.Info("00:00", $"Video: {Path.GetFileName(_videoPath)} | Trajanje: {FormatTime(duration)} | Interval: {opts.IntervalSec}s"));

            AddFinding(FindingItem.Info("—", "   Tražim Qwen model..."));
            string model = await DetectQwenModelAsync();
            AddFinding(FindingItem.Info("—", $"   Model: {(string.IsNullOrEmpty(model) ? "❌ nije pronađen" : "✅ " + model)}"));

            if (string.IsNullOrEmpty(model))
            {
                SetStatus("Qwen2-VL nije dostupan.", "#FF5722");
                return;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), $"UVE_Review_{Guid.NewGuid():N}");
            AddFinding(FindingItem.Info("—", $"   TempDir: {tempDir}"));
            Directory.CreateDirectory(tempDir);

            // Warm-up ping — Qwen model se ucitava u VRAM pri prvom pozivu.
            // Saljemo prazan tekstualni zahtjev da model bude spreman prije petlje.
            AddFinding(FindingItem.Info("—", "   Zagrijavam Qwen model (prvi poziv ucitava u VRAM)..."));
            try
            {
                using var warmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                warmCts.CancelAfter(TimeSpan.FromMinutes(5)); // model load moze trajati dugo
                await _ollama.VisionAsync(null, "hi", model, warmCts.Token); // null path -> brz fail, ali model se ucita
            }
            catch { }
            AddFinding(FindingItem.Info("—", "   TempDir kreiran. Krećem petlju..."));

            // Struktura za cuvanje rezultata svakog frejma
            var frameResults = new List<(double ts, string desc, string raw, ReviewFrame parsed)>();

            try
            {
                string prevDesc  = null;
                int scoredFrames  = 0;
                double totalScore = 0;

                for (int i = 0; i <= _totalFrames; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    double timestamp = i * opts.IntervalSec;
                    if (timestamp > duration) break;

                    _processedFrames = i;
                    int pct = (int)((double)i / Math.Max(_totalFrames, 1) * 100);
                    UpdateProgress(pct, timestamp, duration);

                    string framePath = Path.Combine(tempDir, $"frame_{i:D4}.jpg");
                    bool extracted = await ExtractFrameAtAsync(_videoPath, timestamp, framePath, ct);
                    if (!extracted) continue;

                    long frameKb = new System.IO.FileInfo(framePath).Length / 1024;
                    // Crni kadar (< 2KB) — preskacemo tiho, ne zagadjujemo log
                    if (frameKb < 2)
                    {
                        try { File.Delete(framePath); } catch { }
                        continue;
                    }

                    // Qwen analiza
                    SetStatus($"Analiziram kadar {FormatTime(timestamp)} ({i + 1}/{_totalFrames})...", "#FF9800");
                    string prompt = BuildReviewPrompt(opts.CheckCuts, opts.CheckQuality, opts.CheckLogic,
                                                      opts.CheckRepeat, opts.CheckFreeze, prevDesc);
                    string raw = null;
                    using var qwenTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    qwenTimeout.CancelAfter(TimeSpan.FromSeconds(180));
                    try
                    {
                        var (qwenResponse, qwenError) = await _ollama.VisionAsyncEx(framePath, prompt, model, qwenTimeout.Token);
                        raw = qwenResponse;
                        if (!string.IsNullOrEmpty(qwenError) && qwenError != "Korisnik otkazao analizu")
                            AddFinding(FindingItem.Info("—", $"   ⚠ Kadar {FormatTime(timestamp)}: {qwenError}"));
                    }
                    catch (OperationCanceledException)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    try { File.Delete(framePath); } catch { }
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var parsed = ParseReviewResponse(raw, timestamp);
                    if (parsed != null)
                    {
                        totalScore += parsed.ScoreValue;
                        scoredFrames++;
                        prevDesc = parsed.SceneDescription;
                        frameResults.Add((timestamp, parsed.SceneDescription, raw, parsed));
                    }
                }

                // ── Grupisi frameove u scene i prikazi ih ────────────────────
                SetProgress(90, "Grupisem scene...");
                var sceneGroups = GroupIntoScenes(frameResults);

                int problemCount = 0;
                foreach (var group in sceneGroups)
                {
                    // Formati timestamp opseg: "00:04 – 00:12"
                    string tsRange = group.Count == 1
                        ? FormatTime(group[0].ts)
                        : $"{FormatTime(group[0].ts)} – {FormatTime(group[^1].ts)}";

                    // Opis scene — uzmi najduzi / najkonkretniji
                    string sceneDesc = group
                        .Where(f => !string.IsNullOrEmpty(f.desc))
                        .OrderByDescending(f => f.desc?.Length ?? 0)
                        .FirstOrDefault().desc ?? "—";

                    // Problemi iz svih frameova u grupi — deduplikacija po tipu
                    var allProblems = group
                        .Where(f => f.parsed?.HasProblems == true)
                        .SelectMany(f => f.parsed.Problems)
                        .GroupBy(p => p.type)
                        .Select(g => g.OrderByDescending(p => p.severity == "high" ? 2 : p.severity == "medium" ? 1 : 0).First())
                        .ToList();

                    // Prikaz grupe — uvek pokazujemo sta se vidi, cak i ako nema problema
                    double avgGroupScore = group
                        .Where(f => f.parsed != null)
                        .Average(f => f.parsed.ScoreValue);
                    string scoreIcon = avgGroupScore >= 8 ? "🟢" : avgGroupScore >= 6 ? "🟡" : "🔴";

                    int frameCount = group.Count;
                    string frameLabel = frameCount == 1 ? "1 kadar" : $"{frameCount} kadra";

                    if (allProblems.Count == 0)
                    {
                        // Scena OK — kratki info zapis
                        AddFinding(FindingItem.Info(tsRange,
                            $"{scoreIcon} {sceneDesc} ({frameLabel}, ocena {avgGroupScore:F0}/10)"));
                    }
                    else
                    {
                        // Scena ima probleme — detaljni prikaz
                        problemCount++;
                        string icon = allProblems[0].type switch
                        {
                            "cut"         => "✂️",
                            "quality"     => "🔍",
                            "logic"       => "🤔",
                            "repeat"      => "🔁",
                            "freeze"      => "🧊",
                            "composition" => "📐",
                            "color"       => "🎨",
                            _             => "⚠️"
                        };
                        string title = allProblems[0].type switch
                        {
                            "cut"         => "Rez / seckanje",
                            "quality"     => "Kvalitet kadra",
                            "logic"       => "Nelogican prelaz",
                            "repeat"      => "Ponavljanje",
                            "freeze"      => "Zamrznuti kadar",
                            "composition" => "Losa kompozicija",
                            "color"       => "Nedosledne boje",
                            _             => "Problem"
                        };

                        string problemDesc = string.Join("\n",
                            allProblems.Select(p =>
                            {
                                string sev = p.severity == "high" ? "🔴 Ozbiljno" :
                                             p.severity == "medium" ? "🟡 Uocljivo" : "🟢 Sitnica";
                                return $"{sev}: {p.desc}";
                            }));

                        string severity = allProblems.Any(p => p.severity == "high") ? "high" :
                                          allProblems.Any(p => p.severity == "medium") ? "medium" : "low";

                        AddFinding(new FindingItem
                        {
                            Timestamp      = tsRange,
                            TimestampAcc   = $"Kadrovi {tsRange}",
                            TimeSeconds    = group[0].ts,
                            Icon           = icon,
                            Title          = $"{title} — {sceneDesc} ({frameLabel})",
                            Description    = problemDesc,
                            Severity       = severity,
                            CanNavigate    = Visibility.Visible,
                            GoToAcc        = $"Idi na {FormatTime(group[0].ts)}",
                            BackgroundColor = new SolidColorBrush(severity == "high"
                                ? Color.FromArgb(80, 183, 28, 28)
                                : severity == "medium"
                                ? Color.FromArgb(80, 230, 81, 0)
                                : Color.FromArgb(60, 33, 33, 33))
                        });
                    }

                    // Freeze provera za grupu
                    if (opts.CheckFreeze && group.Count >= 2)
                    {
                        bool frozen = await CheckFreezeAsync(_videoPath, group[^1].ts, opts.IntervalSec, ct);
                        if (frozen)
                        {
                            problemCount++;
                            AddFinding(FindingItem.Problem(tsRange, group[0].ts,
                                "🧊 Zamrznuti kadar",
                                $"Kadrovi {tsRange} izgledaju identicno — moguca greska zamrznutog kadra."));
                        }
                    }
                }

                // Ponavljanja
                var forRepeat = frameResults.Select(f => (f.ts, f.desc, f.raw)).ToList();
                if (opts.CheckRepeat)
                    DetectRepetitions(forRepeat, ref problemCount);

                // Zakljucak
                SetProgress(100, "Analiza zavrsena.");
                double avgScore = scoredFrames > 0 ? totalScore / scoredFrames : 0;
                string grade    = avgScore >= 8 ? "Odlican" : avgScore >= 6 ? "Dobar" : avgScore >= 4 ? "Osrednji" : "Los";
                string gradeIcon = avgScore >= 8 ? "🏆" : avgScore >= 6 ? "👍" : avgScore >= 4 ? "⚠️" : "❌";

                AddFinding(FindingItem.Summary(FormatTime(duration),
                    $"{gradeIcon} Analiza zavrsena — {problemCount} problema u {sceneGroups.Count} scene\n" +
                    $"   Prosecna ocena: {avgScore:F1}/10 ({grade}) | Analizirano: {scoredFrames}/{_totalFrames} kadrova"));

                // Ekstrakcija teksta iz videa putem Qwen OCR
                await TryExtractLyricsWithQwen(_videoPath, model, duration, ct);

                txtProblemCount.Text       = $"{problemCount} problema";
                badgeProblems.Visibility   = problemCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                txtScoreLabel.Text         = $"Ocjena: {avgScore:F1}/10";
                badgeScore.Visibility      = Visibility.Visible;
                badgeScore.Background      = new SolidColorBrush(
                    avgScore >= 7 ? Color.FromRgb(27, 94, 32) :
                    avgScore >= 5 ? Color.FromRgb(230, 81, 0) :
                                    Color.FromRgb(183, 28, 28));

                SetStatus(
                    $"Gotovo — {problemCount} problema | Ocjena: {avgScore:F1}/10 ({grade})",
                    avgScore >= 7 ? "#00E676" : avgScore >= 5 ? "#FF9800" : "#FF5722");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ── Prompt za Qwen2-VL ────────────────────────────────────────────────
        private static string BuildReviewPrompt(
            bool checkCuts, bool checkQuality, bool checkLogic,
            bool checkRepeat, bool checkFreeze, string prevSceneDesc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Ti si iskusni video montažer. Analiziraš jedan kadar iz videa.");
            sb.AppendLine("Odgovori ISKLJUČIVO u ovom JSON formatu (bez markdowna, bez ikakvog teksta izvan JSON-a):\n");
            sb.AppendLine("{");
            sb.AppendLine("  \"score\": 7,");
            sb.AppendLine("  \"scene\": \"Dijete trči po sunčanom parku\",");
            sb.AppendLine("  \"problems\": [");
            sb.AppendLine("    {\"type\": \"quality\", \"severity\": \"high\", \"description\": \"Kadar je pretaman, lica se jedva razlikuju od pozadine\"},");
            sb.AppendLine("    {\"type\": \"cut\", \"severity\": \"medium\", \"description\": \"Nagla promjena scene bez vizuelne logike\"}");
            sb.AppendLine("  ]");
            sb.AppendLine("}\n");
            sb.AppendLine("OBAVEZNO za polje \"scene\":");
            sb.AppendLine("- Opiši KO ili ŠTA se vidi u kadru (npr. 'Dijete sjedi na klupi u parku')");
            sb.AppendLine("- Navedi osvjetljenje i ugođaj (npr. 'sunčano', 'tamno', 'mutno')");
            sb.AppendLine("- Ako je kadar prazan/crn/tehnički, napiši to jasno (npr. 'Crni kadar — intro')");
            sb.AppendLine("- Piši na bosanskom/srpskom jeziku, jasno i konkretno, 5-10 riječi");
            sb.AppendLine("\nProblemi koje trebaš provjeriti:");
            if (checkCuts)    sb.AppendLine("- cut: nagla ili nelogična promjena kadra, jump cut, nepovezan pokret");
            if (checkQuality) sb.AppendLine("- quality: zamagljenost, preeksponiranost, premrak, loš fokus, kompresijski artefakti");
            if (checkLogic)   sb.AppendLine("- logic: scena nema smisla u kontekstu (npr. unutra pa vani bez prijelaza)");
            if (checkRepeat)  sb.AppendLine("- repeat: vizuelno identično ili skoro identično prethodnoj sceni");
            if (checkFreeze)  sb.AppendLine("- freeze: statična slika previše dugo — izgleda kao greška zamrznutog kadra");
            sb.AppendLine("- composition: loše kadriranje, lice/tijelo odsječeno, nagnut horizont");
            sb.AppendLine("- color: nedosljedna korekcija boja u odnosu na okolne kadrove");
            sb.AppendLine("\nZa \"description\" svakog problema: piši na bosanskom/srpskom, konkretno objasni ŠTA je problem u ovom kadru.");
            sb.AppendLine("Primjeri dobrog opisa:");
            sb.AppendLine("  ✅ 'Dijete je odsječeno s desne strane kadra, glava nije vidljiva'");
            sb.AppendLine("  ✅ 'Scena je premračna, lice subjekta jedva vidljivo'");
            sb.AppendLine("  ✅ 'Identičan kadar kao 4 sekunde ranije — ponavljanje'");
            sb.AppendLine("  ❌ 'Frame is too dark' (engleski nije dozvoljen)");
            sb.AppendLine("  ❌ 'Poor quality' (previše generično)");

            if (!string.IsNullOrEmpty(prevSceneDesc))
                sb.AppendLine($"\nPrethodni kadar bio je: \"{prevSceneDesc}\". Označi logičke greške ako ovaj kadar nije konzistentan.");

            sb.AppendLine("\nAko nema problema, vrati prazan niz: \"problems\": []");
            sb.AppendLine("Težina problema: low (sitnica) / medium (uočljivo) / high (ozbiljan problem)");
            sb.AppendLine("score: 1-10 (ukupni kvalitet kadra za music video)");

            return sb.ToString();
        }

        // ── Parsiranje odgovora ───────────────────────────────────────────────
        private static ReviewFrame ParseReviewResponse(string json, double timestamp)
        {
            try
            {
                json = System.Text.RegularExpressions.Regex.Replace(json, @"```json?\s*|\s*```", "").Trim();

                double score = 5;
                var sm = System.Text.RegularExpressions.Regex.Match(json, @"""score""\s*:\s*([\d.]+)");
                if (sm.Success) double.TryParse(sm.Groups[1].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out score);

                string scene = "";
                var scm = System.Text.RegularExpressions.Regex.Match(json, @"""scene""\s*:\s*""([^""]+)""");
                if (scm.Success) scene = scm.Groups[1].Value;

                // Izvuci sve probleme iz array-a
                var problems = new List<(string type, string severity, string desc)>();
                var probBlock = System.Text.RegularExpressions.Regex.Match(
                    json, @"""problems""\s*:\s*\[(.*?)\]",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                if (probBlock.Success && probBlock.Groups[1].Value.Trim().Length > 2)
                {
                    var typeMatches = System.Text.RegularExpressions.Regex.Matches(
                        probBlock.Groups[1].Value, @"""type""\s*:\s*""([^""]+)""");
                    var sevMatches  = System.Text.RegularExpressions.Regex.Matches(
                        probBlock.Groups[1].Value, @"""severity""\s*:\s*""([^""]+)""");
                    var descMatches = System.Text.RegularExpressions.Regex.Matches(
                        probBlock.Groups[1].Value, @"""description""\s*:\s*""([^""]+)""");

                    int n = Math.Min(typeMatches.Count, Math.Min(sevMatches.Count, descMatches.Count));
                    for (int i = 0; i < n; i++)
                        problems.Add((typeMatches[i].Groups[1].Value,
                                      sevMatches[i].Groups[1].Value,
                                      descMatches[i].Groups[1].Value));
                }

                return new ReviewFrame
                {
                    Timestamp        = timestamp,
                    ScoreValue       = score,
                    SceneDescription = scene,
                    Problems         = problems
                };
            }
            catch { return null; }
        }

        // ── Freeze detekcija (pixel diff dva uzastopna frejma) ────────────────
        private async Task<bool> CheckFreezeAsync(
            string videoPath, double timestamp, int interval, CancellationToken ct)
        {
            if (timestamp < interval) return false;
            string f1 = Path.Combine(Path.GetTempPath(), $"freeze_a_{Guid.NewGuid():N8}.png");
            string f2 = Path.Combine(Path.GetTempPath(), $"freeze_b_{Guid.NewGuid():N8}.png");
            try
            {
                bool ok1 = await ExtractFrameAtAsync(videoPath, timestamp - interval, f1, ct);
                bool ok2 = await ExtractFrameAtAsync(videoPath, timestamp, f2, ct);
                if (!ok1 || !ok2) return false;

                long sz1 = new FileInfo(f1).Length;
                long sz2 = new FileInfo(f2).Length;
                // Heuristika: ako su PNG fajlovi identičnih veličina ± 2%, vjerovatno isti frejm
                double diff = Math.Abs(sz1 - sz2) / (double)Math.Max(sz1, 1);
                return diff < 0.02;
            }
            catch { return false; }
            finally
            {
                try { File.Delete(f1); } catch { }
                try { File.Delete(f2); } catch { }
            }
        }

        // ── Detekcija ponavljanja scena ───────────────────────────────────────
        // Ekstrakcija teksta iz videa putem Qwen2-VL
        // Qwen cita tekst direktno iz frameova — ne treba spoljni SRT fajl
        private async Task TryExtractLyricsWithQwen(string videoPath, string model, double duration, CancellationToken ct)
        {
            AddFinding(FindingItem.Info("—", "📝 Ekstraktujem tekst iz videa (Qwen cita stihove iz frameova)..."));

            string tempDir2 = Path.Combine(Path.GetTempPath(), $"UVE_Lyrics_{Guid.NewGuid():N8}");
            Directory.CreateDirectory(tempDir2);

            try
            {
                // Uzimamo frame na svakih 5 sekundi — dovoljno za tekst koji stoji par sekundi
                double interval = 5.0;
                int total = (int)(duration / interval);
                var detectedLines = new List<string>();
                string lastText = "";

                for (int i = 0; i <= total; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    double ts = i * interval;
                    if (ts > duration) break;

                    string framePath = Path.Combine(tempDir2, $"lyric_{i:D4}.jpg");
                    bool ok = await ExtractFrameAtAsync(videoPath, ts, framePath, ct);
                    if (!ok || !File.Exists(framePath) || new FileInfo(framePath).Length < 1000) continue;

                    string prompt =
                        "Look at this video frame. Is there any text visible (lyrics, subtitles, captions)? " +
                        "If yes, respond with ONLY the text you see, exactly as written, nothing else. " +
                        "If no text is visible, respond with exactly: NONE";

                    string raw = null;
                    try
                    {
                        using var lyrCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        lyrCts.CancelAfter(TimeSpan.FromSeconds(60));
                        (raw, _) = await _ollama.VisionAsyncEx(framePath, prompt, model, lyrCts.Token);
                    }
                    catch { }

                    try { File.Delete(framePath); } catch { }

                    if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "NONE") continue;

                    string line = raw.Trim();
                    // Deduplikacija — ne dodaj isti tekst dva puta za redom
                    if (line != lastText && line.Length > 2)
                    {
                        detectedLines.Add($"[{FormatTime(ts)}] {line}");
                        lastText = line;
                    }
                }

                if (detectedLines.Count == 0)
                {
                    AddFinding(FindingItem.Info("—",
                        "ℹ Qwen nije pronasao tekst u kadru. Video mozda nema titlove/stihove na slici."));
                    return;
                }

                AddFinding(FindingItem.Info("—", $"📝 Pronadjen tekst u {detectedLines.Count} kadra:"));
                foreach (string line in detectedLines)
                    AddFinding(FindingItem.Info("—", $"   {line}"));
            }
            finally
            {
                try { Directory.Delete(tempDir2, true); } catch { }
            }
        }

        // Grupisanje uzastopnih frameova koji vizuelno cine istu scenu
        private static List<List<(double ts, string desc, string raw, ReviewFrame parsed)>> GroupIntoScenes(
            List<(double ts, string desc, string raw, ReviewFrame parsed)> frames)
        {
            var groups = new List<List<(double ts, string desc, string raw, ReviewFrame parsed)>>();
            if (frames.Count == 0) return groups;

            var current = new List<(double ts, string desc, string raw, ReviewFrame parsed)> { frames[0] };

            for (int i = 1; i < frames.Count; i++)
            {
                string prevDesc = frames[i - 1].desc ?? "";
                string currDesc = frames[i].desc ?? "";

                // Scena je "ista" ako su opisi vizuelno slicni (jaccard slicnost > 0.45)
                // ili ako je razmak manji od 4 sekunde i nema problema u prethodnom
                double sim = SceneSimilarity(prevDesc, currDesc);
                double gap = frames[i].ts - frames[i - 1].ts;
                bool sameScene = sim > 0.45 || (gap <= 4.0 && sim > 0.25);

                // Ako je novi kadar crni/prazan ili tehnicki, pripoji prethodnoj grupi
                bool isEmpty = string.IsNullOrWhiteSpace(currDesc) ||
                               currDesc.ToLower().Contains("crni kadar") ||
                               currDesc.ToLower().Contains("black frame") ||
                               currDesc.ToLower().Contains("no visible");
                if (isEmpty) sameScene = true;

                // Grupa ne sme biti veca od 6 kadra (max ~12 sekundi)
                if (sameScene && current.Count < 6)
                    current.Add(frames[i]);
                else
                {
                    groups.Add(current);
                    current = new List<(double ts, string desc, string raw, ReviewFrame parsed)> { frames[i] };
                }
            }
            groups.Add(current);
            return groups;
        }

        private void DetectRepetitions(
            List<(double ts, string desc, string raw)> frames, ref int problemCount)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                for (int j = i + 2; j < frames.Count; j++) // preskačemo uzastopne
                {
                    if (string.IsNullOrEmpty(frames[i].desc) || string.IsNullOrEmpty(frames[j].desc)) continue;

                    double sim = SceneSimilarity(frames[i].desc, frames[j].desc);
                    if (sim > 0.75)
                    {
                        double gap = frames[j].ts - frames[i].ts;
                        if (gap > 5) // ignoriši bliske frejmove iste scene
                        {
                            problemCount++;
                            AddFinding(FindingItem.Problem(
                                FormatTime(frames[j].ts), frames[j].ts,
                                "🔁 Ponavljanje scene",
                                $"Vizualno slično kadru na {FormatTime(frames[i].ts)} — isti tip scene se ponavlja."));
                        }
                    }
                }
            }
        }

        private static double SceneSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            var wa = a.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var wb = b.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            int common = wa.Intersect(wb).Count();
            return (double)common / Math.Max(Math.Max(wa.Count, wb.Count), 1);
        }

        // ── FFmpeg helpers ────────────────────────────────────────────────────
        private async Task<bool> ExtractFrameAtAsync(
            string videoPath, double timestamp, string outputPath, CancellationToken ct)
        {
            try
            {
                // Brisi stari output ako postoji
                if (File.Exists(outputPath)) File.Delete(outputPath);

                // -ss PRIJE -i = input seeking (brzo, bez dekodiranja cijelog videa)
                // Za timestamp=0 koristimo 0 direktno — novije verzije FFmpeg-a to podrzavaju
                string tsStr = timestamp.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

                string args = $"-nostdin -y -ss {tsStr} -i \"{videoPath}\" " +
                              $"-vframes 1 -vf scale=640:360 -q:v 2 \"{outputPath}\"";

                var psi = new ProcessStartInfo(_ffmpegPath, args)
                {
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardError  = true,   // MORA se citati asinhrono — inace deadlock
                    RedirectStandardOutput = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    AddFinding(FindingItem.Info("—", "   ❌ Process.Start vratio null — ffmpeg nije pokrenut"));
                    return false;
                }

                // KRITIČNO: citamo stderr ASINHRONO prije WaitForExit
                // Bez ovoga, ako FFmpeg ispise vise od ~4KB na stderr, buffer se blokira
                // i WaitForExit nikad ne vraca — beskonacni deadlock!
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();

                // Timeout 15s
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested(); // propagiraj samo ako je korisnik otkazao
                    AddFinding(FindingItem.Info("—", $"   ⏱ FFmpeg timeout (15s) na {tsStr}s"));
                    return false;
                }

                string stderr = await stderrTask;

                // Log stderr samo ako ima gresaka (FFmpeg uvijek nesto pise na stderr — normalno)
                if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                {
                    // Uzmi zadnju liniju — tamo je obicno kljucna greska
                    string lastLine = stderr.Trim().Split('\n').LastOrDefault(l => l.Contains("Error") || l.Contains("Invalid") || l.Contains("error")) ?? "";
                    if (!string.IsNullOrEmpty(lastLine))
                        AddFinding(FindingItem.Info("—", $"   FFmpeg stderr: {lastLine.Trim()}"));
                }

                bool valid = proc.ExitCode == 0
                             && File.Exists(outputPath)
                             && new FileInfo(outputPath).Length > 512; // JPG < 512B = prazan/korumpiran

                if (!valid && proc.ExitCode == 0 && File.Exists(outputPath))
                    AddFinding(FindingItem.Info("—", $"   ⚠ Fajl postoji ali je premali ({new FileInfo(outputPath).Length}B) — ignorisem"));

                return valid;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddFinding(FindingItem.Info("—", $"   ❌ ExtractFrame greska: {ex.Message}"));
                return false;
            }
        }

        private async Task<double> GetVideoDurationAsync(string videoPath, CancellationToken ct)
        {
            try
            {
                string ffprobeDir = Path.GetDirectoryName(_ffmpegPath)!;
                string ffprobe    = Path.Combine(ffprobeDir, "ffprobe.exe");
                if (!File.Exists(ffprobe))
                    ffprobe = Path.Combine(ffprobeDir, "ffprobe");  // Linux fallback

                if (!File.Exists(ffprobe))
                    return await GetDurationViaffmpegAsync(videoPath, ct);

                string args = $"-v error -show_entries format=duration " +
                              $"-of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";

                var psi = new ProcessStartInfo(ffprobe, args)
                {
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                using var proc  = Process.Start(psi);
                // ASINHRONO citanje — sprjecava deadlock
                var stdoutTask  = proc!.StandardOutput.ReadToEndAsync();
                var stderrTask  = proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);
                string output   = await stdoutTask;

                if (double.TryParse(output.Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
                    return d;
            }
            catch { }
            return await GetDurationViaffmpegAsync(videoPath, ct);
        }

        private async Task<double> GetDurationViaffmpegAsync(string videoPath, CancellationToken ct)
        {
            try
            {
                // -i bez outputa vraca gresku ali Duration je u stderr
                var psi = new ProcessStartInfo(_ffmpegPath, $"-i \"{videoPath}\"")
                {
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true
                };
                using var proc   = Process.Start(psi);
                // Citaj stderr ASINHRONO — sprjecava deadlock
                var stderrTask   = proc!.StandardError.ReadToEndAsync();
                var stdoutTask   = proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);
                string stderr    = await stderrTask;
                var m = System.Text.RegularExpressions.Regex.Match(
                    stderr, @"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)");
                if (m.Success)
                    return int.Parse(m.Groups[1].Value) * 3600
                         + int.Parse(m.Groups[2].Value) * 60
                         + int.Parse(m.Groups[3].Value)
                         + int.Parse(m.Groups[4].Value) / 100.0;
            }
            catch { }
            return 0;
        }

        private async Task<string> DetectQwenModelAsync()
        {
            foreach (string name in new[] { "qwen2.5vl", "qwen2-vl", "qwen2.5-vl" })
                if (await _ollama.IsModelAvailable(name)) return name;
            return null;
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private void SetStatus(string msg, string hex = "#FF9800")
        {
            txtStatus.Text       = msg;
            txtStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        private void UpdateProgress(int pct, double currentTs, double totalDuration)
        {
            pbProgress.Value = pct;
            txtPct.Text      = $"{pct}%";

            txtFrameCounter.Text = $"Frejm {_processedFrames} / {_totalFrames}  |  " +
                                   $"{FormatTime(currentTs)} / {FormatTime(totalDuration)}";

            if (_processedFrames >= 2)
            {
                double elapsed  = (DateTime.Now - _analysisStartTime).TotalSeconds;
                double perFrame = elapsed / _processedFrames;
                int remaining   = _totalFrames - _processedFrames;
                double etaSec   = perFrame * remaining;

                txtETA.Text = etaSec > 60
                    ? $"Preostalo: ~{(int)(etaSec / 60)}m {(int)(etaSec % 60)}s"
                    : $"Preostalo: ~{(int)etaSec}s";
            }
            else
            {
                txtETA.Text = "Računam trajanje...";
            }

            txtStatus.Text = $"🔍 Analiziram kadar na {FormatTime(currentTs)}...";
        }

        private void SetProgress(int pct, string statusMsg = null)
        {
            pbProgress.Value = pct;
            txtPct.Text      = $"{pct}%";
            if (statusMsg != null) SetStatus(statusMsg);
        }

        private void AddFinding(FindingItem item)
        {
            _findings.Add(item);
        }

        private static string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.Hours > 0
                ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    // ── Data modeli ───────────────────────────────────────────────────────────

    internal class ReviewFrame
    {
        public double   Timestamp        { get; set; }
        public double   ScoreValue       { get; set; }
        public string   SceneDescription { get; set; }
        public List<(string type, string severity, string desc)> Problems { get; set; } = new();
        public bool     HasProblems      => Problems?.Count > 0;

        public FindingItem ToFinding()
        {
            if (!HasProblems) return null;
            var p    = Problems[0]; // prvi problem kao glavni
            string icon = p.type switch
            {
                "cut"         => "✂️",
                "quality"     => "🔍",
                "logic"       => "🤔",
                "repeat"      => "🔁",
                "freeze"      => "🧊",
                "composition" => "📐",
                "color"       => "🎨",
                _             => "⚠️"
            };
            string title = p.type switch
            {
                "cut"         => "Rez / seckanje",
                "quality"     => "Kvalitet kadra",
                "logic"       => "Nelogičan prelaz",
                "repeat"      => "Ponavljanje",
                "freeze"      => "Zamrznuti kadar",
                "composition" => "Loša kompozicija",
                "color"       => "Nedosljedno bojenje",
                _             => "Problem"
            };

            // Naslov: tip problema + opis scene u zagradi
            string sceneCtx = !string.IsNullOrWhiteSpace(SceneDescription)
                ? $" — {SceneDescription}"
                : "";

            // Svaki problem na posebnoj liniji, s težinom
            string allDesc = string.Join("\n",
                Problems.Select(x =>
                {
                    string sev = x.severity switch
                    {
                        "high"   => "🔴 Ozbiljno",
                        "medium" => "🟡 Uočljivo",
                        "low"    => "🟢 Sitnica",
                        _        => "⚪"
                    };
                    return $"{sev}: {x.desc}";
                }));

            return new FindingItem
            {
                Timestamp      = FormatTime(Timestamp),
                TimestampAcc   = $"Timestamp {FormatTime(Timestamp)}",
                TimeSeconds    = Timestamp,
                Icon           = icon,
                Title          = title + sceneCtx,
                Description    = allDesc,
                Severity       = p.severity,
                CanNavigate    = Visibility.Visible,
                GoToAcc        = $"Idi na {FormatTime(Timestamp)}",
                BackgroundColor = new SolidColorBrush(p.severity == "high"
                    ? Color.FromArgb(80, 183, 28, 28)
                    : p.severity == "medium"
                    ? Color.FromArgb(80, 230, 81, 0)
                    : Color.FromArgb(60, 33, 33, 33))
            };
        }

        private static string FormatTime(double s)
        {
            var ts = TimeSpan.FromSeconds(s);
            return ts.Hours > 0
                ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    internal class ReviewOptions
    {
        public bool CheckCuts    { get; set; }
        public bool CheckQuality { get; set; }
        public bool CheckLogic   { get; set; }
        public bool CheckRepeat  { get; set; }
        public bool CheckFreeze  { get; set; }
        public int  IntervalSec  { get; set; } = 2;
    }

    public class FindingItem
    {
        public string  Timestamp       { get; set; }
        public string  TimestampAcc    { get; set; }
        public double  TimeSeconds     { get; set; }
        public string  Icon            { get; set; }
        public string  Title           { get; set; }
        public string  Description     { get; set; }
        public string  Severity        { get; set; }
        public Visibility CanNavigate  { get; set; } = Visibility.Collapsed;
        public string  GoToAcc         { get; set; }
        public Brush   BackgroundColor { get; set; }

        public static FindingItem Problem(string ts, double seconds, string title, string desc) => new()
        {
            Timestamp       = ts,
            TimestampAcc    = $"Problem na {ts}",
            TimeSeconds     = seconds,
            Icon            = "⚠️",
            Title           = title,
            Description     = desc,
            Severity        = "medium",
            CanNavigate     = Visibility.Visible,
            GoToAcc         = $"Idi na {ts}",
            BackgroundColor = new SolidColorBrush(Color.FromArgb(80, 183, 28, 28))
        };

        public static FindingItem Info(string ts, string msg) => new()
        {
            Timestamp       = ts,
            TimestampAcc    = $"Info {ts}",
            TimeSeconds     = 0,
            Icon            = "ℹ️",
            Title           = msg,
            Description     = "",
            CanNavigate     = Visibility.Collapsed,
            BackgroundColor = new SolidColorBrush(Color.FromArgb(60, 20, 20, 40))
        };

        public static FindingItem Summary(string ts, string msg) => new()
        {
            Timestamp       = ts,
            TimestampAcc    = $"Zaključak {ts}",
            TimeSeconds     = 0,
            Icon            = "📋",
            Title           = msg,
            Description     = "",
            CanNavigate     = Visibility.Collapsed,
            BackgroundColor = new SolidColorBrush(Color.FromArgb(80, 0, 80, 0))
        };
    }
}
