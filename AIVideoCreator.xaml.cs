using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using Microsoft.Win32;
using SkiaSharp;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfMessageBox = System.Windows.MessageBox;
using WpfApp = System.Windows.Application;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace UltraVideoEditor
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class AIVideoCreator : Window
    {
        private OllamaClient _ollama;
        private bool _ollamaRunning = false;
        private List<string> _lyricLines;
        private Dictionary<int, double> _lyricTimestamps = new Dictionary<int, double>();
        private List<TimelineSegment> _segments;
        private string _pixabayApiKey;
        private bool _showLyrics = false;

        private double _timelineCursorOffset = 0;

        public static bool FastRenderMode = true;
        private bool _enableTransitionSounds = true;
        private bool _enableAmbientSounds = true;
        private bool _enableVoiceNarration = false;
        private string _selectedTheme = "fun";
        private string _tempVideoFolder;
        private string _ambientAudioPath;
        private string _audioPath;
        private double _totalDuration;
        private BeatInfo _beatInfo;
        private MotionResult _lastClipMotion;
        private double _lastDownloadedVisionScore = 6.0;
        private bool _lastDownloadedIsStatic = false;
        private string _detectedMood = "neutral";
        private string _detectedContext = "";

        private string _lang => (WpfApp.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _lang);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _lang), args);

        private readonly Dictionary<string, string> _transitionSoundCache = new Dictionary<string, string>();

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly HttpClient _dlHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private CancellationTokenSource _cts;
        private bool _isRunning = false;
        private readonly SemaphoreSlim _apiRateLimiter = new SemaphoreSlim(1, 1);

        private readonly HashSet<string> _usedMediaUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _queryUseCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private List<string> _universalKeywords = new List<string>
        {
            "children playing in park soft light warm colors",
            "kids running and laughing happy atmosphere",
            "children on swings warm colors",
            "happy kids playing outdoors soft light",
            "children having fun warm colors",
            "kids playing together happy atmosphere"
        };

        private static readonly Dictionary<string, List<string>> _contextKeywords = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["lullaby"] = new List<string> { "baby sleeping peaceful soft light", "toddler sleeping cozy bedroom night", "mother rocking baby to sleep warm light" },
            ["party"] = new List<string> { "children birthday party colorful balloons", "kids celebrating happy confetti", "birthday cake candles children excited" },
            ["love"] = new List<string> { "couple holding hands sunset romantic", "romantic walk park golden hour", "love heart flowers warm colors" },
            ["sad"] = new List<string> { "child alone window rain melancholy", "person looking out rainy window", "melancholy empty bench autumn" },
            ["adventure"] = new List<string> { "child exploring forest adventure", "kids hiking mountains adventure", "children running through field adventure" },
            ["nature"] = new List<string> { "beautiful nature flowers soft light", "meadow flowers butterflies sunlight", "spring flowers garden warm light" },
            ["dance"] = new List<string> { "children dancing joyful colorful", "kids dance class happy movement", "children spinning dancing colorful clothes" },
            ["school"] = new List<string> { "children learning classroom bright", "kids school backpack happy", "children reading books learning" },
            ["animal"] = new List<string> { "cute animals children playing warm", "child playing with dog happy", "kids petting animals farm" },
            ["christmas"] = new List<string> { "christmas tree lights cozy warm", "children christmas gifts excited", "winter holiday family warm lights" },
            ["music"] = new List<string> {
                "children singing together joyful music",
                "child playing piano keys happy",
                "kids dancing music colorful fun",
                "children with instruments school music class",
                "child headphones listening music happy bedroom"
            },
            ["fun"] = new List<string> { "children playing in park soft light warm colors", "kids running and laughing happy atmosphere", "children on swings warm colors" },
            ["outdoor"] = new List<string> {
                "children running park playground sunny",
                "kids playing park green grass happy",
                "child walking park family sunny day",
                "children playground swings slide happy",
                "kids outdoor park nature sunny playing"
            },
            ["health"] = new List<string> {
                "children running active outdoor park",
                "kids healthy active outdoor sunny",
                "child jumping playing park happy healthy",
                "children outdoor exercise active fun",
                "kids sports active outdoor healthy sunny"
            }
        };

        public AIVideoCreator()
        {
            InitializeComponent();
            Loaded += AIVideoCreator_Loaded;
            _pixabayApiKey = GetPixabayApiKey();
        }

        private async void AIVideoCreator_Loaded(object sender, RoutedEventArgs e)
        {
            AutomationProperties.SetName(this, "AI Video Creator dijalog");
            txtOllamaStatus.Text = L("ol_checking");
            txtOllamaStatus.Foreground = System.Windows.Media.Brushes.Orange;

            _ollama = new OllamaClient();
            _ollamaRunning = await _ollama.IsOllamaRunning();

            if (_ollamaRunning)
            {
                txtOllamaStatus.Text = L("ollama_running");
                txtOllamaStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                btnGenerate.IsEnabled = true;
            }
            else
            {
                txtOllamaStatus.Text = L("ollama_not_running");
                txtOllamaStatus.Foreground = System.Windows.Media.Brushes.Red;
                btnGenerate.IsEnabled = false;
            }

            InitFreesound();
            AutoPopulateSongInfo();
            txtLyrics.Focus();
        }

        private void AutoPopulateSongInfo()
        {
            var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            var audioItem = mainWindow.timelineItems.FirstOrDefault(i => i.Type == "Audio");
            if (audioItem == null || string.IsNullOrEmpty(audioItem.Path)) return;

            string fileName = Path.GetFileNameWithoutExtension(audioItem.Path);
            if (string.IsNullOrWhiteSpace(fileName)) return;

            if (txtIntroText != null)
                txtIntroText.Text = fileName;

            if (txtOutroText != null)
            {
                if (fileName.Contains(" - "))
                {
                    var parts = fileName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    string izvodjac = parts[0].Trim();
                    string naziv = parts[1].Trim();
                    txtOutroText.Text =
                        naziv + " | Autor: " + izvodjac +
                        L("outro_subscribe");
                }
                else
                {
                    txtOutroText.Text = fileName;
                }
            }

            LogToMainWindow($"🎵 Auto-popunjeno iz audio fajla: '{fileName}'");
        }

        private void InitFreesound()
        {
            string soundsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds");
            if (Directory.Exists(soundsDir) && Directory.GetFiles(soundsDir, "*.*", SearchOption.AllDirectories).Length > 0)
            {
                LogToMainWindow("🔊 Lokalna zvučna biblioteka pronađena — ambijentalni zvukovi su aktivni.");
            }
            else
            {
                LogToMainWindow("⚠️ Assets/Sounds/ prazan ili ne postoji — ambijentalni zvukovi neće biti dostupni.");
                _enableAmbientSounds = false;
                if (chkAmbientSounds != null) chkAmbientSounds.IsChecked = false;
                if (chkAmbientSounds != null) chkAmbientSounds.IsEnabled = false;
            }
        }

        private void btnBrowseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WpfOpenFileDialog { Filter = L("filter_images") };
            if (dialog.ShowDialog() == true)
                txtLogoPath.Text = dialog.FileName;
        }

        private void txtLyrics_TextChanged(object sender, TextChangedEventArgs e)
        {
            _lyricLines = txtLyrics.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            lstKeywords.Items.Clear();
            for (int i = 0; i < _lyricLines.Count; i++)
            {
                var stackPanel = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(5) };
                stackPanel.Children.Add(new TextBlock { Text = $"{i + 1}. ", Width = 40, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                stackPanel.Children.Add(new TextBlock { Text = _lyricLines[i], Width = 350, Foreground = System.Windows.Media.Brushes.LightGray, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                stackPanel.Children.Add(new TextBlock { Text = L("ai_keywords_label"), Width = 300, Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
                lstKeywords.Items.Add(stackPanel);
            }
            btnGenerate.IsEnabled = _lyricLines.Count > 0;
        }

        private async void btnAutoTranscribe_Click(object sender, RoutedEventArgs e)
        {
            string audioPath = null;

            var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                var audioItem = mainWindow.timelineItems
                    .FirstOrDefault(t => t.IsAudio && File.Exists(t.Path));
                if (audioItem != null)
                    audioPath = audioItem.Path;
            }

            if (string.IsNullOrEmpty(audioPath))
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Izaberi audio fajl za transkripciju",
                    Filter = L("filter_audio_video")
                };
                if (dlg.ShowDialog() != true) return;
                audioPath = dlg.FileName;
            }

            if (!AITranscription.IsWhisperAvailable())
            {
                var msg = L("whisper_not_found_msg") + Environment.NewLine + Environment.NewLine +
                          "Instaliraj ga na jedan od ova dva nacina:" + Environment.NewLine + Environment.NewLine +
                          "OPCIJA A - Python (preporuceno):" + Environment.NewLine +
                          "  pip install openai-whisper" + Environment.NewLine + Environment.NewLine +
                          "OPCIJA B - Standalone (bez Python-a):" + Environment.NewLine +
                          "  Preuzmi faster-whisper-xxl.exe" + Environment.NewLine +
                          "  i stavi ga pored UltraVideoEditor.exe" + Environment.NewLine + Environment.NewLine +
                          "Nakon instalacije, ponovo pokreni aplikaciju.";
                WpfMessageBox.Show(msg, L("whisper_not_found_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnAutoTranscribe.IsEnabled = false;
            btnAutoTranscribe.Content = "⏳ Transkribujem...";
            if (txtTranscribeStatus != null)
                txtTranscribeStatus.Text = "Pokrecem Whisper...";

            var progress = new Progress<string>(msg =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (txtTranscribeStatus != null)
                        txtTranscribeStatus.Text = msg;
                    AnnounceToUser(msg, 0);
                });
            });

            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            try
            {
                var result = await AITranscription.TranscribeAsync(
                    audioPath,
                    language: "sr",
                    ffmpegPath: ffmpegPath,
                    modelSize: "large-v3",
                    progress: progress,
                    ct: _cts?.Token ?? CancellationToken.None);

                if (!result.Success)
                {
                    WpfMessageBox.Show(result.ErrorMessage, L("transcription_error_title"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                txtLyrics.Text = AITranscription.FormatLyricsForTextBox(result.Lines);

                _lyricTimestamps.Clear();
                for (int i = 0; i < result.Lines.Count; i++)
                    _lyricTimestamps[i] = result.Lines[i].StartSeconds;

                if (txtTranscribeStatus != null)
                    txtTranscribeStatus.Text = $"✅ {result.Lines.Count} linija prepoznato";

                AnnounceToUser(LF("transcription_done", result.Lines.Count), 0);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(LF("generic_error", ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnAutoTranscribe.IsEnabled = true;
                btnAutoTranscribe.Content = "🎤 Transkribuj iz audio";
            }
        }

        private async void btnGenerate_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = WpfApp.Current.MainWindow as MainWindow;
            if (mainWindow == null) { WpfMessageBox.Show(L("mainwindow_not_available"), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error); return; }

            _enableTransitionSounds = chkTransitionSounds.IsChecked == true;
            _enableAmbientSounds = chkAmbientSounds.IsChecked == true;
            FastRenderMode = chkFastRender.IsChecked == true;
            _enableVoiceNarration = chkVoiceNarration.IsChecked == true;
            _selectedTheme = (cmbVideoTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fun";

            LogToMainWindow($"🎬 Postavke: TransitionSounds={_enableTransitionSounds}, AmbientSounds={_enableAmbientSounds}, VoiceNarration={_enableVoiceNarration}, Theme={_selectedTheme}");

            var audioItem = mainWindow.timelineItems.FirstOrDefault(i => i.Type == "Audio");

            double totalDuration;
            bool hasAudio = audioItem != null && audioItem.Duration > 0;

            if (!hasAudio)
            {
                var result = WpfMessageBox.Show(L("no_audio_on_timeline"),
                    "🎵 AI Muzika", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    WpfMessageBox.Show(L("add_audio_manually"),
                        L("warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AnnounceToUser(L("ai_analyzing_song"), 0);
                var tempStoryBoard = await GenerateStoryBoard(_lyricLines, _cts?.Token ?? CancellationToken.None);

                totalDuration = 180;

                AnnounceToUser(L("ai_searching_music"), 5);
                string mood = _detectedMood ?? tempStoryBoard?.OverallTheme ?? "happy";
                string aiMusicPath = await DownloadBackgroundMusic(mood, totalDuration, _tempVideoFolder, _cts.Token);

                if (!string.IsNullOrEmpty(aiMusicPath))
                {
                    var newAudioItem = new TimelineItem
                    {
                        Path = aiMusicPath,
                        Duration = 0,
                        Start = 0,
                        End = 0,
                        Name = "🎵 AI generisana muzika",
                        Type = "Audio",
                        Volume = 100,
                        TrackIndex = 1
                    };
                    mainWindow.timelineItems.Add(newAudioItem);

                    totalDuration = await GetAudioDuration(aiMusicPath);
                    newAudioItem.Duration = totalDuration;
                    newAudioItem.End = totalDuration;

                    LogToMainWindow($"🎵 AI generisana muzika dodata, trajanje: {FormatTime(totalDuration)}");
                    audioItem = newAudioItem;
                    hasAudio = true;
                }
                else
                {
                    WpfMessageBox.Show(L("ai_music_download_failed"),
                        L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                totalDuration = audioItem.Duration;
                if (totalDuration <= 0)
                {
                    WpfMessageBox.Show(L("audio_unknown_duration"), L("warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _lyricLines = txtLyrics.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (_lyricLines.Count == 0)
            {
                int autoSceneCount = Math.Max(4, (int)(totalDuration / 7.0));
                _lyricLines = GenerateInstrumentalSceneDescriptions(autoSceneCount, totalDuration, _detectedContext, _detectedMood);
                AnnounceToUser(LF("no_lyrics_generating_auto", autoSceneCount), 0);
            }

            _showLyrics = chkShowLyrics.IsChecked == true;
            _cts = new CancellationTokenSource();
            _isRunning = true;
            btnGenerate.IsEnabled = false;
            btnCancel.Content = L("stop_button");
            btnGenerate.Content = L("ai_creating_story");
            prgProgress.Visibility = Visibility.Visible;
            AnnounceToUser(LF("audio_duration_analyzing", FormatTime(totalDuration), totalDuration, _lyricLines.Count), 0);

            try
            {
                await ProcessVideoCreation(audioItem.Path, totalDuration);
            }
            catch (OperationCanceledException) { AnnounceToUser(L("operation_cancelled")); }
            catch (Exception ex) { WpfMessageBox.Show(LF("generic_error", ex.Message), L("generic_error_title"), MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { _isRunning = false; _cts?.Dispose(); _cts = null; btnGenerate.IsEnabled = true; btnGenerate.Content = L("create_video_button"); btnCancel.Content = L("cancel_button"); btnCancel.IsEnabled = true; prgProgress.Visibility = Visibility.Collapsed; txtProgress.Text = ""; }
        }

        #region AI Pipeline - 3 Step Architecture

        private async Task<SongAnalysis> AnalyseSongWithAI(List<string> lyrics, CancellationToken ct)
        {
            string lyricsText = string.Join("\n", lyrics);

            string prompt = $@"Analiziraj ovu pjesmu i odgovori ISKLJUČIVO u JSON formatu.

TEKST PJESME:
{lyricsText}

Odgovori u ovom JSON formatu:
{{
  ""context"": ""[fun/lullaby/party/love/sad/adventure/nature/dance/school/animal/christmas/outdoor/seasons/health]"",
  ""mood"": ""[happy/calm/melancholy/romantic/excited/energetic/upbeat/curious/playful/joyful/peaceful]"",
  ""theme"": ""[jedna rečenica na srpskom: o čemu je pjesma]"",
  ""visual_style"": ""[5-7 engleskih riječi za vizualni stil]"",
  ""main_subject"": ""[dijete/beba/porodica/priroda/životinja]"",
  ""season"": ""[spring/summer/autumn/winter/all/none]"",
  ""setting"": ""[park/bedroom/school/forest/beach/city/home/outdoors/mixed]""
}}";

            try
            {
                LogToMainWindow(L("ai_song_analyzing"));
                string response = await _ollama.GenerateAsync(prompt, ct: ct);
                string jsonStr = ExtractJson(response);
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    var analysis = Newtonsoft.Json.JsonConvert.DeserializeObject<SongAnalysis>(jsonStr);
                    bool validAnalysis = analysis != null
                        && !string.IsNullOrWhiteSpace(analysis.Context)
                        && !string.IsNullOrWhiteSpace(analysis.Mood);
                    if (validAnalysis)
                    {
                        LogToMainWindow($"🎭 AI analiza: kontekst='{analysis.Context}', mood='{analysis.Mood}', tema='{analysis.Theme}'");
                        return analysis;
                    }
                    LogToMainWindow(L("ai_empty_values"));
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow(LF("ai_analysis_failed", ex.Message));
            }

            return FallbackSongAnalysis(lyrics);
        }

        private SongAnalysis FallbackSongAnalysis(List<string> lyrics)
        {
            string allText = string.Join(" ", lyrics).ToLower();
            var scores = new Dictionary<string, int> { ["music"] = 0, ["lullaby"] = 0, ["party"] = 0, ["love"] = 0, ["sad"] = 0, ["adventure"] = 0, ["nature"] = 0, ["dance"] = 0, ["school"] = 0, ["animal"] = 0, ["christmas"] = 0, ["outdoor"] = 0, ["seasons"] = 0, ["health"] = 0, ["fun"] = 1 };

            string[] musicW = { "muzik", "melodij", "pesm", "pjesm", "svira", "instrument", "nota", "ritam", "zvuk", "gitara", "klavir", "bubanj", "violina", "flauta", "pevaj", "pjevaj", "muzičar", "koncert", "slušaj muzik", "blago muzik" };
            string[] lullabyW = { "spavaj", "usni", "sni", "laku noć", "uspavanka", "sleep", "lullaby", "goodnight", "moonlight" };
            string[] partyW = { "sretan", "srećan", "rođendan", "baloni", "torta", "birthday", "party", "balloon", "cake", "celebrate" };
            string[] loveW = { "volim", "ljubav", "srce", "draga", "dragi", "love", "heart", "kiss", "hug", "romance" };
            string[] sadW = { "plačem", "suze", "tužan", "tuga", "cry", "tears", "sad", "alone", "lonely", "goodbye" };
            string[] adventW = { "istraži", "avantura", "planina", "šuma", "adventure", "explore", "mountain", "forest", "discover" };
            string[] natureW = { "cvijet", "proljeće", "jesen", "zima", "ljeto", "priroda", "flower", "spring", "autumn", "winter", "summer", "nature" };
            string[] danceW = { "pleši", "igraj", "ples", "ritam", "dance", "dancing", "rhythm", "music", "sing" };
            string[] schoolW = { "škola", "učenje", "knjiga", "učitelj", "school", "learn", "book", "teacher", "class" };
            string[] animalW = { "pas", "maca", "konj", "zec", "ptica", "dog", "cat", "horse", "bunny", "animal" };
            string[] xmasW = { "božić", "nova godina", "snijeg", "jelka", "christmas", "santa", "snow", "holiday", "gift" };
            string[] outdoorW = { "šetaj", "šetnja", "trči", "park", "outdoor", "walk", "run", "playground", "park", "outside" };
            string[] seasonsW = { "proleće", "proljeće", "jesen", "zima", "leto", "ljeto", "spring", "autumn", "fall", "winter", "summer", "seasons" };
            string[] healthW = { "zdravlje", "zdravo", "zdravi", "kretanje", "health", "healthy", "exercise", "active", "fit", "movement" };

            foreach (var w in musicW) if (allText.Contains(w)) scores["music"] += 4;
            foreach (var w in lullabyW) if (allText.Contains(w)) scores["lullaby"] += 3;
            foreach (var w in partyW) if (allText.Contains(w)) scores["party"] += 3;
            foreach (var w in loveW) if (allText.Contains(w)) scores["love"] += 3;
            foreach (var w in sadW) if (allText.Contains(w)) scores["sad"] += 3;
            foreach (var w in adventW) if (allText.Contains(w)) scores["adventure"] += 2;
            foreach (var w in natureW) if (allText.Contains(w)) scores["nature"] += 1;
            foreach (var w in danceW) if (allText.Contains(w)) scores["dance"] += 2;
            foreach (var w in schoolW) if (allText.Contains(w)) scores["school"] += 2;
            foreach (var w in animalW) if (allText.Contains(w)) scores["animal"] += 2;
            foreach (var w in xmasW) if (allText.Contains(w)) scores["christmas"] += 4;
            foreach (var w in outdoorW) if (allText.Contains(w)) scores["outdoor"] += 2;
            foreach (var w in seasonsW) if (allText.Contains(w)) scores["seasons"] += 2;
            foreach (var w in healthW) if (allText.Contains(w)) scores["health"] += 2;

            string ctx = scores.OrderByDescending(kv => kv.Value).First().Key;
            string mood = ctx switch { "music" => "upbeat", "lullaby" => "calm", "sad" => "melancholy", "love" => "romantic", "party" => "excited", "adventure" => "energetic", "dance" => "upbeat", "christmas" => "joyful", "animal" => "playful", "school" => "curious", "nature" => "peaceful", "outdoor" => "happy", _ => "happy" };

            string season = "none";
            if (allText.Contains("proleć") || allText.Contains("spring")) season = "spring";
            else if (allText.Contains("jesen") || allText.Contains("autumn")) season = "autumn";
            else if (allText.Contains("zima") || allText.Contains("winter")) season = "winter";
            else if (allText.Contains("leto") || allText.Contains("summer")) season = "summer";
            else if (scores["seasons"] >= 2) season = "all";

            string themeByCtx = ctx switch
            {
                "music" => "Dječija pjesma o muzici",
                "lullaby" => "Uspavanka",
                "party" => "Vesela proslava",
                "love" => "Pjesma o ljubavi",
                "sad" => "Tužna pjesma",
                "adventure" => "Avantura",
                "nature" => "Priroda i životinje",
                "dance" => "Ples i ritam",
                "school" => "Školska pjesma",
                "animal" => "Životinje",
                "christmas" => "Božićna pjesma",
                "outdoor" => "Dječija pjesma o šetnji i igri",
                "health" => "Zdravlje i aktivnost djece",
                _ => "Dječija pjesma"
            };

            return new SongAnalysis
            {
                Context = ctx,
                Mood = mood,
                Theme = themeByCtx,
                VisualStyle = ctx switch
                {
                    "music" => "colorful bright children joyful",
                    "outdoor" => "children park playground sunny green happy running",
                    "health" => "children active outdoor park sunny healthy",
                    _ => "warm colors soft light natural outdoor"
                },
                MainSubject = ctx switch
                {
                    "music" => "children singing playing music",
                    "outdoor" => "children running playing park playground",
                    "health" => "children active healthy outdoor",
                    _ => "children"
                },
                Season = season,
                Setting = ctx == "outdoor" || ctx == "seasons" || ctx == "health" ? "outdoors" : "mixed"
            };
        }

        private async Task<List<SceneKeywords>> GenerateKeywordsPerLyric(List<string> lyrics, SongAnalysis analysis, CancellationToken ct)
        {
            var results = new List<SceneKeywords>();
            int total = lyrics.Count;

            LogToMainWindow($"🎬 Generiram vizualne keywords za {total} stihova (LITERAL ACTION SYNC)...");

            int batchSize = 4;
            for (int i = 0; i < total; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = lyrics.Skip(i).Take(batchSize).ToList();
                int batchStart = i;

                string batchText = string.Join("\n", batch.Select((l, idx) => $"{batchStart + idx + 1}. {l}"));

                string prompt = $@"VIDEO REŽIJA – LITERALNA ILUSTRACIJA – NEMA PEJZAŽA BEZ AKCIJE

Pravilo: ŠTA STIH KAŽE, TO SE VIDEO VIDI. NEMA METAFORA. NEMA GENERIČKIH PRIZORA.

Stihovi pjesme (tema: {analysis.Theme}, stil: {analysis.VisualStyle}):

{batchText}

Za SVAKI stih, navedi tačno ono što se bukvalno događa u tekstu:

Ako stih kaže:
- ""trči"" → video MORA biti: children running
- ""skače"" → video MORA biti: child jumping
- ""smeje se"" → video MORA biti: children laughing
- ""šeće se"" → video MORA biti: child walking
- ""igra loptu"" → video MORA biti: children playing ball
- ""drži se za ruke"" → video MORA biti: holding hands walking
- ""jede sladoled"" → video MORA biti: child eating ice cream
- ""vozi bicikl"" → video MORA biti: child riding bicycle

ZABRANJENO:
- generički ""happy child in park"" bez jasne akcije
- pejzaži bez djece (šume, polja, cvijeće) – OSLIM ako tekst to ne nalaže
- apstraktne scene

DOZVOLJENO:
- SPECIFIČNA radnja koja se pominje u stihu
- LICA koja pokazuju emociju navedenu u stihu
- SEZONSKE elemente SAMO ako tekst pominje godišnje doba

Odgovori ISKLJUČIVO JSON:
[
  {{""line"": {batchStart + 1}, ""keywords"": ""konkretna akcija + subjekt + detalj"", ""ambient"": ""zvuk koji odgovara sceni""}},
  {{""line"": {batchStart + 2}, ""keywords"": ""..."", ""ambient"": ""...""}}
]";

                try
                {
                    AnnounceToUser(LF("ai_analyzing_lyrics", i + 1, Math.Min(i + batchSize, total)), 5 + (i * 20 / total));
                    string response = await _ollama.GenerateAsync(prompt, ct: ct);
                    string jsonStr = ExtractJson(response, isArray: true);

                    if (!string.IsNullOrEmpty(jsonStr))
                    {
                        var batchResults = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SceneKeywords>>(jsonStr);
                        if (batchResults != null && batchResults.Count > 0)
                        {
                            foreach (var r in batchResults)
                            {
                                bool isTemplate = string.IsNullOrEmpty(r.Keywords) ||
                                    r.Keywords.Contains("konkretna akcija") ||
                                    r.Keywords.Contains("subjekt + detalj") ||
                                    r.Keywords == "..." ||
                                    r.Keywords.Length < 5;

                                bool hasCyrillic = r.Keywords?.Any(c => c > 0x400 && c < 0x500) ?? false;
                                bool hasLatin = r.Keywords?.Any(c => "šđčćžŠĐČĆŽ".Contains(c)) ?? false;

                                var genericPhrases = new[] { "happy child", "child playing", "children playing", "warm colors", "soft light" };
                                bool isTooGeneric = genericPhrases.Any(p => r.Keywords?.ToLower() == p);

                                var adultKeywords = new[] { "coffee", "beer", "wine", "whiskey", "alcohol", "office",
                                    "business", "suit", "tie", "laptop", "computer", "meeting", "corporate",
                                    "cigarette", "smoking", "bar ", "cocktail", "nightclub", "adult",
                                    "toad", "frog", "frog closeup", "reptile", "insect closeup",
                                    "rush hour", "busy street", "commute", "crowd city",
                                    "stock market", "finance", "real estate",
                                    "marble floor", "corridor", "hallway", "atrium", "lobby",
                                    "indoor walk", "shopping mall", "airport terminal",
                                    "black and white", "monochrome", "silhouette", "animation",
                                    "cartoon", "sketch", "drawing", "illustrated",
                                    "dark alley", "abandoned", "horror", "scary",
                                    "cemetery", "graveyard", "funeral" };
                                bool hasAdultContent = adultKeywords.Any(w => r.Keywords?.ToLower().Contains(w) ?? false);

                                if (isTemplate || hasCyrillic || hasLatin || isTooGeneric || hasAdultContent)
                                {
                                    if (hasAdultContent)
                                        LogToMainWindow(LF("ai_bad_keywords", r.Line, r.Keywords));
                                    int lyricIdx = r.Line - batchStart - 1;
                                    if (lyricIdx >= 0 && lyricIdx < batch.Count)
                                    {
                                        r.Keywords = GenerateKeywordsFromLyric(batch[lyricIdx], analysis);
                                        r.Ambient = InferAmbientFromLyric(batch[lyricIdx], analysis.Context);
                                        LogToMainWindow(LF("ai_unusable_keywords", r.Line));
                                    }
                                }
                            }
                            results.AddRange(batchResults);
                            foreach (var r in batchResults)
                                LogToMainWindow($"   Stih {r.Line}: akcija='{r.Keywords}', zvuk='{r.Ambient}'");
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogToMainWindow(LF("ai_keywords_error", ex.Message));
                }

                foreach (var (lyric, idx) in batch.Select((l, idx) => (l, idx)))
                {
                    results.Add(new SceneKeywords
                    {
                        Line = batchStart + idx + 1,
                        Keywords = GenerateKeywordsFromLyric(lyric, analysis),
                        Ambient = InferAmbientFromLyric(lyric, analysis.Context)
                    });
                }
            }

            while (results.Count < total)
            {
                int missing = results.Count;
                results.Add(new SceneKeywords
                {
                    Line = missing + 1,
                    Keywords = GenerateKeywordsFromLyric(lyrics[missing], analysis),
                    Ambient = InferAmbientFromLyric(lyrics[missing], analysis.Context)
                });
            }

            return results;
        }

        private List<string> GenerateInstrumentalSceneDescriptions(int count, double totalDuration, string context = "fun", string mood = "happy", string visualStyle = "")
        {
            string[] templates;

            switch (context)
            {
                case "music":
                    templates = new[]
                    {
                        "children singing together choir joyful colorful",
                        "child playing piano happy lesson bright",
                        "kids clapping hands singing music fun",
                        "children dancing music classroom colorful",
                        "girl boy playing guitar together happy",
                        "child headphones listening music happy bedroom",
                        "children instruments school band performance",
                        "kids music class teacher colorful fun",
                        "child microphone singing stage joyful",
                        "children choir singing colorful concert",
                        "boy girl dancing music living room fun",
                        "children music notes colorful animation singing",
                    };
                    break;

                case "lullaby":
                    templates = new[]
                    {
                        "moonlight soft glow peaceful night",
                        "baby sleeping peaceful soft light",
                        "stars twinkling night sky gentle",
                        "clouds floating slow soft colors",
                        "mother rocking baby gentle warm",
                        "soft toys bedroom cozy night light",
                        "candle flickering soft warm glow",
                        "teddy bear child sleeping peaceful",
                    };
                    break;

                case "party":
                    templates = new[]
                    {
                        "confetti colorful falling celebration",
                        "children dancing party colorful clothes",
                        "balloons colorful floating celebration",
                        "birthday cake candles children cheering",
                        "children jumping happy excited party",
                        "colorful lights bokeh celebration",
                        "kids laughing party fun together",
                        "streamers colorful festive celebration",
                    };
                    break;

                case "love":
                    templates = new[]
                    {
                        "heart shapes colorful romantic warm",
                        "family hugging together warm golden light",
                        "children holding hands friendship",
                        "flowers blooming colorful garden",
                        "family together park sunset warm golden light",
                        "mother child hugging tender moment",
                        "golden hour sunlight warm glow",
                    };
                    break;

                case "nature":
                    templates = new[]
                    {
                        "forest sunlight through trees peaceful",
                        "butterfly flower garden colorful",
                        "river flowing peaceful nature sounds",
                        "birds flying sky freedom",
                        "flowers blooming spring garden",
                        "children exploring nature curious",
                        "rainbow after rain colorful sky",
                        "meadow green sunny peaceful",
                    };
                    break;

                case "animal":
                    templates = new[]
                    {
                        "cute animals farm happy sunny",
                        "dog playing child happy outdoor",
                        "cat curious playful sunny window",
                        "birds colorful singing branch",
                        "rabbit hopping green meadow",
                        "children petting animals farm",
                        "ducks pond water splashing",
                        "horse running field beautiful",
                    };
                    break;

                case "dance":
                    templates = new[]
                    {
                        "children dancing joyful colorful",
                        "feet dancing floor rhythmic",
                        "colorful dance performance stage",
                        "kids moving dancing happy energetic",
                        "dance studio children learning",
                        "spinning twirling colorful dress",
                        "group dance children smiling",
                    };
                    break;

                case "adventure":
                    templates = new[]
                    {
                        "children exploring forest adventure",
                        "mountain landscape sunrise dramatic",
                        "hiking trail nature adventure",
                        "children running open field free",
                        "map compass adventure exploring",
                        "treehouse children playing adventure",
                        "bicycle riding path outdoor adventure",
                    };
                    break;

                case "sad":
                    templates = new[]
                    {
                        "rain window drops melancholy",
                        "autumn leaves falling slow motion",
                        "foggy morning misty peaceful",
                        "candle flame flickering quiet",
                        "empty swing park gentle wind",
                        "cloudy sky peaceful grey tones",
                    };
                    break;

                case "school":
                    templates = new[]
                    {
                        "children classroom learning happy",
                        "books colorful school supplies",
                        "children raising hands class eager",
                        "school playground children playing",
                        "pencils crayons colorful drawing",
                        "children reading books curious",
                        "teacher children classroom warm",
                    };
                    break;

                case "christmas":
                    templates = new[]
                    {
                        "christmas tree lights colorful glowing",
                        "snow falling winter peaceful",
                        "children opening gifts excited christmas",
                        "fireplace warm cozy winter",
                        "snowflakes falling slow motion",
                        "family christmas dinner warm lights",
                        "santa claus gifts children happy",
                    };
                    break;

                default:
                    templates = new[]
                    {
                        "children running park sunny happy",
                        "family walking nature together",
                        "child laughing playing outdoor",
                        "friends sitting together outside",
                        "children jumping dancing joyful",
                        "sunlight through leaves warm",
                        "child riding bicycle street",
                        "family picnic grass sunny",
                        "children playground happy",
                        "grandparents child park walking",
                        "child chasing butterfly garden",
                        "friends playing ball together",
                        "child eating ice cream summer",
                        "family watching sunset together",
                        "children building sand castle beach",
                        "laughing children running camera",
                        "child feeding ducks river",
                        "family picking fruit garden",
                        "children playing autumn leaves",
                        "child painting drawing colorful",
                    };
                    break;
            }

            var result = new List<string>();
            for (int i = 0; i < count; i++)
                result.Add(templates[i % templates.Length]);

            LogToMainWindow($"🎵 Instrumental scene ({count} kadrova) — kontekst: '{context}', raspolozenje: '{mood}'");
            return result;
        }

        private string BuildLiteralSearchQuery(string keywords, string action, string energyBoost, string style)
        {
            var srToEn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"automobil","car"}, {"auto","car"}, {"kola","car"}, {"vozilo","car"},
                {"kamion","truck"}, {"bus","bus"}, {"autobus","bus"}, {"voz","train"},
                {"bicikl","bicycle"}, {"motor","motorcycle"}, {"avion","airplane"},
                {"brod","ship"}, {"čamac","boat"}, {"traktor","tractor"},

                {"mama","mother"}, {"tata","father"}, {"dete","child"}, {"djete","child"},
                {"deca","children"}, {"djeca","children"}, {"beba","baby"},
                {"baka","grandmother"}, {"deka","grandfather"}, {"sestra","sister"},
                {"brat","brother"}, {"porodica","family"}, {"prijatelj","friend"},
                {"drugar","friend"}, {"drugarica","friend"}, {"učitelj","teacher"},

                {"pas","dog"}, {"mačka","cat"}, {"konj","horse"}, {"krava","cow"},
                {"ptica","bird"}, {"ptice","birds"}, {"leptir","butterfly"},
                {"riba","fish"}, {"zec","rabbit"}, {"medved","bear"}, {"medvjed","bear"},
                {"lav","lion"}, {"tigar","tiger"}, {"slon","elephant"}, {"majmun","monkey"},
                {"ovca","sheep"}, {"patka","duck"}, {"pile","chicken"}, {"svinja","pig"},

                {"park","park"}, {"parkić","park"}, {"šuma","forest"}, {"suma","forest"},
                {"plaža","beach"}, {"more","sea"}, {"reka","river"}, {"rijeka","river"},
                {"planina","mountain"}, {"livada","meadow"}, {"bašta","garden"},
                {"dvorište","yard"}, {"ulica","street"}, {"grad","city"},
                {"škola","school"}, {"kuća","house"}, {"dom","home"},

                {"sladoled","ice cream"}, {"torta","cake"}, {"čokolada","chocolate"},
                {"jabuka","apple"}, {"banana","banana"}, {"hleb","bread"},
                {"čaj","tea"}, {"sok","juice"}, {"mleko","milk"},

                {"trči","running"}, {"trčanje","running"}, {"šeta","walking"},
                {"šetaj","walking"}, {"šetnja","walking"}, {"skače","jumping"},
                {"igra","playing"}, {"pleše","dancing"}, {"peva","singing"},
                {"čita","reading"}, {"crta","drawing"}, {"slika","painting"},
                {"spava","sleeping"}, {"jede","eating"}, {"pije","drinking"},
                {"pliva","swimming"}, {"vozi","riding"}, {"nosi","carrying"},
                {"grli","hugging"}, {"smeje","laughing"}, {"plače","crying"},

                {"prolece","spring"}, {"proleće","spring"}, {"proljeće","spring"},
                {"leto","summer"}, {"ljeto","summer"}, {"jesen","autumn"},
                {"zima","winter"}, {"sneg","snow"}, {"snijeg","snow"},
                {"kiša","rain"}, {"sunce","sun"}, {"vetar","wind"}, {"vjetar","wind"},

                {"lopta","ball"}, {"igračka","toy"}, {"lutka","doll"},
                {"knjiga","book"}, {"olovka","pencil"}, {"torba","bag"},
                {"kapa","hat"}, {"čizme","boots"}, {"rukavice","gloves"},
                {"šal","scarf"}, {"kaput","coat"}, {"haljina","dress"},

                {"sretan","happy"}, {"srečan","happy"}, {"vesel","joyful"},
                {"tužan","sad"}, {"ljut","angry"}, {"uplašen","scared"},
                {"zdravo","healthy"}, {"jako","strong"}, {"malo","little"},
                {"veliko","big"}, {"lepo","beautiful"}, {"lijepo","beautiful"},
            };

            var parts = new[] { keywords, action, energyBoost, style }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToList();

            var englishWords = new List<string>();
            foreach (var word in parts)
            {
                string clean = System.Text.RegularExpressions.Regex.Replace(word, @"[^a-zA-ZšđčćžŠĐČĆŽ\-]", "");
                if (string.IsNullOrEmpty(clean)) continue;

                if (srToEn.TryGetValue(clean, out string translated))
                {
                    if (!englishWords.Contains(translated))
                        englishWords.Add(translated);
                }
                else
                {
                    bool isEnglish = clean.All(c => c < 128 && char.IsLetter(c));
                    if (isEnglish && !englishWords.Contains(clean.ToLower()))
                        englishWords.Add(clean.ToLower());
                }
            }

            var styleNoise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "warm","colors","colour","soft","light","happy","atmosphere","child","friendly",
                "bright","colorful","colourful","beautiful","nice","good","calm","peaceful",
                "cheerful","uplifting","gentle","cozy","cosy"
            };

            var coreWords = englishWords.Where(w => !styleNoise.Contains(w)).Take(4).ToList();
            var styleWords = englishWords.Where(w => styleNoise.Contains(w)).Take(1).ToList();

            if (coreWords.Count == 0)
                coreWords = englishWords.Take(4).ToList();

            var finalWords = coreWords.Count < 3
                ? coreWords.Concat(styleWords).ToList()
                : coreWords;

            if (finalWords.Count == 0)
                return "child playing outdoor";

            string baseQuery = string.Join(" ", finalWords);

            string contextSuffix = _detectedContext switch
            {
                "wedding" or "love" or "romantic" => " bokeh shallow depth",
                "documentary" or "news" => " professional cinematic",
                "sad" or "melancholy" => " cinematic dramatic",
                "adventure" or "sport" or "action" => " action dynamic",
                "children" or "lullaby" or "fun" => "",
                _ => ""
            };

            return baseQuery + contextSuffix;
        }

        private string GenerateKeywordsFromLyric(string lyric, SongAnalysis analysis)
        {
            string lower = lyric.ToLower();

            if (lower.Contains("automobil") || lower.Contains(" auto ") || lower.Contains("kolima") ||
                lower.Contains("autić") || lower.Contains("kola ") || lower.Contains(" kola"))
                return "car driving street";
            if (lower.Contains("bicikl") || lower.Contains("biciklo") || lower.Contains("biciklu"))
                return "child riding bicycle park";
            if (lower.Contains("trotinet") || lower.Contains("romobil"))
                return "child riding scooter street";
            if (lower.Contains("kamion") || lower.Contains("kamionić"))
                return "truck driving road";
            if (lower.Contains("avion") || lower.Contains("avionić") || lower.Contains("helikopter"))
                return "airplane flying sky clouds";
            if (lower.Contains("voz") || lower.Contains("vozić") || lower.Contains("lokomot"))
                return "train railway station";
            if (lower.Contains("brod") || lower.Contains("čamac") || lower.Contains("jedrilica") || lower.Contains("barka"))
                return "boat sailing water";
            if (lower.Contains("traktor") || lower.Contains("kombajn"))
                return "tractor farm field";
            if (lower.Contains("motor") || lower.Contains("motocikl"))
                return "motorcycle driving road";
            if (lower.Contains("autobus") || lower.Contains("trollej"))
                return "bus city street children";
            if (lower.Contains("raketa") || lower.Contains("svemirsk") || lower.Contains("svemir"))
                return "rocket space stars universe";
            if (lower.Contains("tenkić") || lower.Contains("tenk"))
                return "toy tank children playing";
            if (lower.Contains("vatrogasn") || lower.Contains("vatrogasac"))
                return "fire truck firefighter action";
            if (lower.Contains("policij") || lower.Contains("policajac"))
                return "police car city";
            if (lower.Contains("ambulant") || lower.Contains("hitna"))
                return "ambulance medical help";
            if (lower.Contains("skuter"))
                return "scooter riding street";

            if (lower.Contains(" pas ") || lower.Contains("psa ") || lower.Contains("psić") ||
                lower.Contains("štenad") || lower.Contains("štene") || lower.Contains("psu "))
                return "dog playing happy";
            if (lower.Contains("mačka") || lower.Contains("maca") || lower.Contains("mačić") ||
                lower.Contains("mace") || lower.Contains("macu"))
                return "cat cute kitten";
            if (lower.Contains("konj") || lower.Contains("kobila") || lower.Contains("ždreb"))
                return "horse running field";
            if (lower.Contains("krava") || lower.Contains("telić") || lower.Contains("tele"))
                return "cow farm meadow";
            if (lower.Contains("ovca") || lower.Contains("ovce") || lower.Contains("jagnje"))
                return "sheep meadow farm";
            if (lower.Contains("svinja") || lower.Contains("prasić") || lower.Contains("prase"))
                return "pig farm cute";
            if (lower.Contains("kokoška") || lower.Contains("pilic") || lower.Contains("pilić") || lower.Contains("pile"))
                return "chicken farm chick cute";
            if (lower.Contains("patka") || lower.Contains("pačić"))
                return "duck pond water cute";
            if (lower.Contains("zec") || lower.Contains("kunić") || lower.Contains("zečić"))
                return "rabbit cute bunny";
            if (lower.Contains("miš") || lower.Contains("hrčak"))
                return "mouse hamster cute small animal";
            if (lower.Contains("papagaj") || lower.Contains("papigica"))
                return "parrot colorful bird";

            if (lower.Contains("lav") || lower.Contains("lavić") || lower.Contains("lavica"))
                return "lion savanna wild";
            if (lower.Contains("slon") || lower.Contains("slonić"))
                return "elephant nature wild";
            if (lower.Contains("medved") || lower.Contains("medvjed") || lower.Contains("medvedić"))
                return "bear forest nature";
            if (lower.Contains("leptir") || lower.Contains("leptiric"))
                return "butterfly flower garden";
            if (lower.Contains("pčela") || lower.Contains("bumbara") || lower.Contains("bumbar"))
                return "bee flower honey garden";
            if (lower.Contains("riba") || lower.Contains("ribica") || lower.Contains("ribe"))
                return "fish swimming water aquarium";
            if (lower.Contains("delfin"))
                return "dolphin ocean jumping";
            if (lower.Contains("kit"))
                return "whale ocean water";
            if (lower.Contains("kornjača") || lower.Contains("kornjace"))
                return "turtle slow nature";
            if (lower.Contains("zmija"))
                return "snake nature grass";
            if (lower.Contains("tigar") || lower.Contains("tigric"))
                return "tiger wild jungle";
            if (lower.Contains("gepard") || lower.Contains("leopard"))
                return "cheetah running fast wild";
            if (lower.Contains("majmun") || lower.Contains("majmunica"))
                return "monkey jungle climbing";
            if (lower.Contains("žirafa"))
                return "giraffe savanna tall";
            if (lower.Contains("kengur"))
                return "kangaroo australia jumping";
            if (lower.Contains("pingvin"))
                return "penguin ice cute";
            if (lower.Contains("polarni medved") || lower.Contains("polarni"))
                return "polar bear arctic snow";
            if (lower.Contains("lisica") || lower.Contains("lisičica"))
                return "fox forest cute";
            if (lower.Contains("vuk") || lower.Contains("vuče"))
                return "wolf forest nature";
            if (lower.Contains("jelen") || lower.Contains("srna"))
                return "deer forest nature";
            if (lower.Contains("jazavac") || lower.Contains("vjeverica") || lower.Contains("veverica"))
                return "squirrel forest tree";
            if (lower.Contains("krokodil") || lower.Contains("aligator"))
                return "crocodile water wild";
            if (lower.Contains("nilski konj") || lower.Contains("nilskog"))
                return "hippo water wildlife";
            if (lower.Contains("zebra"))
                return "zebra savanna africa";
            if (lower.Contains("nosorog"))
                return "rhino savanna wildlife";

            if (lower.Contains("ptic") || lower.Contains("vrabac") || lower.Contains("lastavic") ||
                lower.Contains("golub") || lower.Contains("sova") || lower.Contains("orao") ||
                lower.Contains("soko") || lower.Contains("kos ") || lower.Contains("slavuj") ||
                lower.Contains("čvorak") || lower.Contains("roda") || lower.Contains("čaplja"))
                return "birds flying sky nature";
            if (lower.Contains("papagaj"))
                return "parrot colorful tropical";

            if (lower.Contains("mama") && lower.Contains("tata")) return "family parents child walking";
            if (lower.Contains("mama") || lower.Contains("majka") || lower.Contains("majko"))
                return "mother child hug love";
            if (lower.Contains("tata") || lower.Contains("otac") || lower.Contains("oče"))
                return "father child playing outdoor";
            if (lower.Contains("baka") || lower.Contains("nana") || lower.Contains("baba"))
                return "grandmother child love tender";
            if (lower.Contains("deka") || lower.Contains("deda") || lower.Contains("djed"))
                return "grandfather child playing park";
            if (lower.Contains("brat ") || lower.Contains("brate") || lower.Contains("bratić"))
                return "siblings brothers playing";
            if (lower.Contains("sestra") || lower.Contains("sestrica"))
                return "sisters siblings playing";
            if (lower.Contains("drugar") || lower.Contains("prijatelj") || lower.Contains("drug "))
                return "children friends playing";
            if (lower.Contains("porodic") || lower.Contains("familij"))
                return "family outdoor together";
            if (lower.Contains("beba") || lower.Contains("bebac") || lower.Contains("novorođen"))
                return "baby newborn cute";
            if (lower.Contains("dete") || lower.Contains("dijete") || lower.Contains("dječak") ||
                lower.Contains("devojčica") || lower.Contains("djevojčica"))
                return "child playing outdoor happy";
            if (lower.Contains("učiteljic") || lower.Contains("nastavnic") || lower.Contains("profesor"))
                return "teacher classroom children learning";
            if (lower.Contains("doktor") || lower.Contains("lekar") || lower.Contains("ljekar"))
                return "doctor children hospital care";
            if (lower.Contains("heroj") || lower.Contains("superhero"))
                return "superhero children playing";
            if (lower.Contains("princeza") || lower.Contains("princ ") || lower.Contains("kralj") || lower.Contains("kraljica"))
                return "princess prince fairy tale children";
            if (lower.Contains("vila") || lower.Contains("vilenjak") || lower.Contains("bajk"))
                return "fairy tale magical children";

            if (lower.Contains("sladoled") && (lower.Contains("čaj") || lower.Contains("zima") || lower.Contains("zimi")))
                return "child eating ice cream summer park happy";
            if (lower.Contains("sladoled")) return "child eating ice cream summer park happy";
            if (lower.Contains("čokolada") || lower.Contains("cokolada"))
                return "child chocolate happy";
            if (lower.Contains("torta") || lower.Contains("kolač") || lower.Contains("cupcake"))
                return "birthday cake children celebrating";
            if (lower.Contains("jabuka")) return "child eating apple healthy";
            if (lower.Contains("banana") || lower.Contains("banane"))
                return "child eating banana fruit";
            if (lower.Contains("jagoda") || lower.Contains("jagode") || lower.Contains("malina"))
                return "child eating strawberry fruit summer";
            if (lower.Contains("lubenica"))
                return "child eating watermelon summer";
            if (lower.Contains("pomorandža") || lower.Contains("narandža") || lower.Contains("limun"))
                return "citrus fruit colorful fresh";
            if (lower.Contains("čaj")) return "child drinking hot chocolate warm cozy";
            if (lower.Contains("sok")) return "child drinking juice fresh";
            if (lower.Contains("mleko") || lower.Contains("mlijeko"))
                return "child drinking milk healthy";
            if (lower.Contains("pizza"))
                return "children eating pizza happy";
            if (lower.Contains("palačink") || lower.Contains("crepe"))
                return "child eating pancakes breakfast";
            if (lower.Contains("med ") || lower.Contains("medu"))
                return "honey jar sweet";
            if (lower.Contains("kokice") || lower.Contains("popcorn"))
                return "children eating popcorn movie";
            if (lower.Contains("bombon") || lower.Contains("slatkiš") || lower.Contains("gumeni"))
                return "child candy sweets colorful";
            if (lower.Contains("hleb") || lower.Contains("hljeb") || lower.Contains("sendvič"))
                return "child eating sandwich lunch";
            if (lower.Contains("večera") || lower.Contains("ručak") || lower.Contains("doručak"))
                return "family eating meal together";
            if (lower.Contains("kafa") || lower.Contains("espresso"))
                return "children drinking hot chocolate cozy winter";
            if (lower.Contains("voda ") || lower.Contains("vodu") || lower.Contains("pijem"))
                return "child drinking water healthy";

            if (lower.Contains("trči") || lower.Contains("trčanje") || lower.Contains("juriš") ||
                lower.Contains("trčati") || lower.Contains("trčeći"))
                return "children running park playground sunny happy";
            if (lower.Contains("šetaj") || lower.Contains("šeta ") || lower.Contains("šetnja") ||
                lower.Contains("šeće") || lower.Contains("šetati") || lower.Contains("šetnjicu"))
                return "children walking park sunny green happy";
            if (lower.Contains("skoči") || lower.Contains("skači") || lower.Contains("skakanje") ||
                lower.Contains("skakutanje") || lower.Contains("skokovit"))
                return "child jumping happy";
            if (lower.Contains("pliva") || lower.Contains("kupanje") || lower.Contains("plivanje"))
                return "child swimming pool water";
            if (lower.Contains("rolanje") || lower.Contains("klizanje") || lower.Contains("klizalište"))
                return "child ice skating winter";
            if (lower.Contains("skijanje") || lower.Contains("skija") || lower.Contains("ski"))
                return "child skiing winter snow mountain";
            if (lower.Contains("sankanje") || lower.Contains("sanke") || lower.Contains("saonice"))
                return "child sledding snow winter fun";
            if (lower.Contains("fudbal") || lower.Contains("nogomet") || lower.Contains("loptu") ||
                lower.Contains("lopta "))
                return "children playing football soccer";
            if (lower.Contains("košarka") || lower.Contains("koš "))
                return "basketball children playing";
            if (lower.Contains("tenis") || lower.Contains("reket"))
                return "tennis children sport";
            if (lower.Contains("penjanje") || lower.Contains("penj") || lower.Contains("penje"))
                return "child climbing tree playground";
            if (lower.Contains("plivanje") || lower.Contains("surf") || lower.Contains("surfanje"))
                return "surfing waves ocean sport";
            if (lower.Contains("yoga") || lower.Contains("meditacij"))
                return "yoga meditation peaceful";
            if (lower.Contains("gimnastik") || lower.Contains("akrobat"))
                return "gymnastics child flexible sport";
            if (lower.Contains("karate") || lower.Contains("džudo") || lower.Contains("borilačk"))
                return "martial arts children sport";
            if (lower.Contains("biciklizm") || lower.Contains("vozi bicikl"))
                return "child riding bicycle outdoor";
            if (lower.Contains("planinar") || lower.Contains("pohod") || lower.Contains("trekking"))
                return "hiking mountain trail nature";
            if (lower.Contains("ribolov") || lower.Contains("pecanje"))
                return "fishing lake child outdoor";
            if (lower.Contains("jedrilica") || lower.Contains("jedričarenje") || lower.Contains("vesla"))
                return "sailing boat water outdoor";
            if (lower.Contains("padobran") || lower.Contains("skakanje padobranom"))
                return "parachute sky adventure";

            if (lower.Contains("pleši") || lower.Contains("plešeš") || lower.Contains("ples") ||
                lower.Contains("tancuj") || lower.Contains("zaigra") || lower.Contains("igra kolo"))
                return "children dancing joyful";
            if (lower.Contains("peva") || lower.Contains("pjeva") || lower.Contains("pevaj") ||
                lower.Contains("pjevaj") || lower.Contains("pevanje"))
                return "child singing music";
            if (lower.Contains("crta") || lower.Contains("risanje") || lower.Contains("boji") ||
                lower.Contains("bojanka") || lower.Contains("akvarelom") || lower.Contains("kistom"))
                return "child drawing painting art colorful";
            if (lower.Contains("čita") || lower.Contains("knjig") || lower.Contains("priča") ||
                lower.Contains("bajka") || lower.Contains("lektir"))
                return "child reading book library";
            if (lower.Contains("igraj") || lower.Contains("igra ") || lower.Contains("igraju") ||
                lower.Contains("igrajmo") || lower.Contains("igrajte"))
                return "children playing joyful";
            if (lower.Contains("smej") || lower.Contains("smije") || lower.Contains("blistaj") ||
                lower.Contains("kesi") || lower.Contains("kikoće"))
                return "children laughing happy faces";
            if (lower.Contains("spava") || lower.Contains("toneš u san") || lower.Contains("zaspi") ||
                lower.Contains("drijema") || lower.Contains("drijemat"))
                return "child sleeping peaceful";
            if (lower.Contains("sanja") || lower.Contains("snovi") || lower.Contains("snoviđ"))
                return "child dreaming stars night sky";
            if (lower.Contains("grli") || lower.Contains("zagrli") || lower.Contains("zagrliti") ||
                lower.Contains("mazi") || lower.Contains("mazi"))
                return "children hugging friendship love";
            if (lower.Contains("ljuljaška") || lower.Contains("ljulja") || lower.Contains("tobogan"))
                return "child playground swing slide";
            if (lower.Contains("pesak") || lower.Contains("pješčanik") || lower.Contains("sanduk"))
                return "child playing sand sandbox";
            if (lower.Contains("lopta") || lower.Contains("balon") || lower.Contains("balonom"))
                return "child playing ball balloon colorful";
            if (lower.Contains("zmaj") || lower.Contains("zmajevima") || lower.Contains("zmajić"))
                return "child flying kite wind outdoor";
            if (lower.Contains("puzzle") || lower.Contains("slagalica"))
                return "child playing puzzle indoor";
            if (lower.Contains("lego") || lower.Contains("kocke") || lower.Contains("graditi") ||
                lower.Contains("gradi kulu"))
                return "child building blocks lego";
            if (lower.Contains("skrivač") || lower.Contains("žmure") || lower.Contains("sakriven"))
                return "children hide seek playing";
            if (lower.Contains("karnevar") || lower.Contains("karneval") || lower.Contains("maskaradu"))
                return "carnival costume children celebration";
            if (lower.Contains("pozorišt") || lower.Contains("kazalište") || lower.Contains("pozornica"))
                return "children theater stage performance";
            if (lower.Contains("lutak") || lower.Contains("lutke") || lower.Contains("marioneta"))
                return "puppet show children theater";
            if (lower.Contains("cirkus"))
                return "circus performance children";
            if (lower.Contains("video igric") || lower.Contains("kompjuter") || lower.Contains("tablet"))
                return "child playing video game";
            if (lower.Contains("bazen") || lower.Contains("vodeni park"))
                return "children water park swimming fun";
            if (lower.Contains("piknik"))
                return "family picnic outdoor nature";
            if (lower.Contains("kampovanje") || lower.Contains("kamp") || lower.Contains("šator"))
                return "camping tent nature outdoor";
            if (lower.Contains("vatromet") || lower.Contains("svečanost") || lower.Contains("proslava"))
                return "fireworks celebration night colorful";

            if (lower.Contains("uči") || lower.Contains("učiti") || lower.Contains("nauči") ||
                lower.Contains("uciti") || lower.Contains("uciš"))
                return "child learning school studying";
            if (lower.Contains("škola") || lower.Contains("razred") || lower.Contains("učionica"))
                return "school children classroom learning";
            if (lower.Contains("domaći zadatak") || lower.Contains("zadatak") || lower.Contains("domaći"))
                return "child doing homework studying";
            if (lower.Contains("pismo") || lower.Contains("pisanje") || lower.Contains("piše"))
                return "child writing letter paper";
            if (lower.Contains("muzička škola") || lower.Contains("hora") || lower.Contains("hor "))
                return "children choir singing school";
            if (lower.Contains("kuvanje") || lower.Contains("kuva") || lower.Contains("pravi kolač"))
                return "child cooking baking kitchen";
            if (lower.Contains("fotografij") || lower.Contains("fotoaparat") || lower.Contains("slika prirodu"))
                return "child photography camera nature";
            if (lower.Contains("pravi") || lower.Contains("izrađuj") || lower.Contains("kreacij"))
                return "child crafts making creative";
            if (lower.Contains("origami") || lower.Contains("papir"))
                return "child paper crafts origami";
            if (lower.Contains("botanik") || lower.Contains("biljke") || lower.Contains("cvećara"))
                return "child gardening plants flowers";
            if (lower.Contains("sadi") || lower.Contains("zaliva") || lower.Contains("bašta"))
                return "child planting garden watering";

            if (lower.Contains("parkić") || lower.Contains(" park ") || lower.Contains("parku"))
                return "children playing park playground sunny happy";
            if (lower.Contains("plaža") || lower.Contains("more ") || lower.Contains("mora") ||
                lower.Contains("obala") || lower.Contains("pesak mora"))
                return "child beach sea summer";
            if (lower.Contains("šuma") || lower.Contains("šumi") || lower.Contains("šumarak"))
                return "children forest nature trees";
            if (lower.Contains("planina") || lower.Contains("vrh") || lower.Contains("planinski"))
                return "mountain nature hiking landscape";
            if (lower.Contains("reka") || lower.Contains("potok") || lower.Contains("reci") ||
                lower.Contains("riječica"))
                return "river stream water nature";
            if (lower.Contains("jezero") || lower.Contains("jezerc"))
                return "lake water nature reflection";
            if (lower.Contains("livada") || lower.Contains("polje") || lower.Contains("njiva"))
                return "meadow flowers field nature";
            if (lower.Contains("bašta") || lower.Contains("vrt ") || lower.Contains("vrtu"))
                return "garden flowers colorful outdoor";
            if (lower.Contains("pećina") || lower.Contains("spilja"))
                return "cave nature adventure";
            if (lower.Contains("vodopad") || lower.Contains("kaskad"))
                return "waterfall nature beautiful";
            if (lower.Contains("desert") || lower.Contains("pustinja"))
                return "desert sand dunes landscape";
            if (lower.Contains("džungla") || lower.Contains("prašuma"))
                return "jungle tropical nature";
            if (lower.Contains("arktik") || lower.Contains("antarktik") || lower.Contains("led ") ||
                lower.Contains("ledenjak"))
                return "arctic ice polar landscape";
            if (lower.Contains("nebo ") || lower.Contains("nebom") || lower.Contains("oblaci") ||
                lower.Contains("oblak"))
                return "sky clouds blue beautiful";
            if (lower.Contains("zvezdano nebo") || lower.Contains("zvezde") || lower.Contains("zvijezde"))
                return "night sky stars milky way";
            if (lower.Contains("mesec ") || lower.Contains("mjesec ") || lower.Contains("punog meseca"))
                return "moon night sky stars";
            if (lower.Contains("sunce") || lower.Contains("sunčano") || lower.Contains("sunčani"))
                return "sunshine bright sunny outdoor";
            if (lower.Contains("duga") || lower.Contains("dugom"))
                return "rainbow colorful sky nature";
            if (lower.Contains("kiša") || lower.Contains("kišica") || lower.Contains("kaplje"))
                return "rain drops children playing puddle";
            if (lower.Contains("oluja") || lower.Contains("grmljavina") || lower.Contains("munja"))
                return "storm lightning dramatic sky";
            if (lower.Contains("magla") || lower.Contains("izmaglica"))
                return "fog misty morning nature";
            if (lower.Contains("vetar") || lower.Contains("vjetar") || lower.Contains("povjetarac"))
                return "wind blowing leaves nature";

            if (lower.Contains("grad ") || lower.Contains("gradu") || lower.Contains("gradić"))
                return "city street urban children";
            if (lower.Contains("ulica") || lower.Contains("ulici") || lower.Contains("trotoar"))
                return "street sidewalk city";
            if (lower.Contains("kuća") || lower.Contains("kući") || lower.Contains(" dom ") ||
                lower.Contains("doma") || lower.Contains("domov"))
                return "home family house cozy";
            if (lower.Contains("dvorišt") || lower.Contains("dvorište"))
                return "children backyard playing";
            if (lower.Contains("zoo") || lower.Contains("zoološki") || lower.Contains("zoovrt"))
                return "zoo animals children visiting";
            if (lower.Contains("muzej") || lower.Contains("galerij"))
                return "museum children visit art";
            if (lower.Contains("bioskop") || lower.Contains("kino") || lower.Contains("film"))
                return "cinema movie children popcorn";
            if (lower.Contains("bibliotek") || lower.Contains("knjižnica"))
                return "library books children reading";
            if (lower.Contains("tržni centar") || lower.Contains("prodavnica") || lower.Contains("prodavaonica"))
                return "shopping mall children family";
            if (lower.Contains("crkva") || lower.Contains("džamija") || lower.Contains("hram"))
                return "church architecture peaceful";
            if (lower.Contains("bolnica"))
                return "hospital doctor care";
            if (lower.Contains("aerodrom"))
                return "airport airplane travel";
            if (lower.Contains("kolodvor") || lower.Contains("železnička stanica") || lower.Contains("stanica"))
                return "train station travel";
            if (lower.Contains("luka") || lower.Contains("pristanište"))
                return "harbor boats port";
            if (lower.Contains("tržnica") || lower.Contains("pijaca") || lower.Contains("pijaci"))
                return "market colorful food outdoor";
            if (lower.Contains("kafić") || lower.Contains("restoran"))
                return "cafe restaurant family";
            if (lower.Contains("hotel") || lower.Contains("odmor") || lower.Contains("ljetovanje") ||
                lower.Contains("letovanje"))
                return "hotel vacation family travel";

            if (lower.Contains("proleć") || lower.Contains("proljeć") || lower.Contains("cvet") ||
                lower.Contains("cvijet") || lower.Contains("trešnja cveta") || lower.Contains("latica"))
                return "spring flowers blooming child";
            if (lower.Contains("jesen") || lower.Contains("opada") || lower.Contains("jesenj") ||
                lower.Contains("žuto lišće") || lower.Contains("zlatno lišće"))
                return "autumn leaves falling colorful";
            if (lower.Contains("zima ") || lower.Contains("zimska") || lower.Contains("zimski") ||
                lower.Contains("sneg ") || lower.Contains("snijeg") || lower.Contains("mraz"))
                return "winter snow child playing outdoor";
            if (lower.Contains("leto ") || lower.Contains("ljeto") || lower.Contains("letnji") ||
                lower.Contains("ljetni") || lower.Contains("toplo") || lower.Contains("vrelo"))
                return "summer sunny outdoor children";

            if (lower.Contains("čizme") || lower.Contains("gumene čizme"))
                return "child winter boots snow";
            if (lower.Contains("rukavice"))
                return "child winter gloves snow";
            if (lower.Contains("skafander") || lower.Contains("kombinezon"))
                return "child winter suit snow outdoor";
            if (lower.Contains("kapa ") || lower.Contains("kapu") || lower.Contains("šešir"))
                return "child hat winter colorful";
            if (lower.Contains("šal ") || lower.Contains("šalom"))
                return "child scarf winter cozy";
            if (lower.Contains("kaput") || lower.Contains("jakna") || lower.Contains("mantil"))
                return "child coat winter dressed";
            if (lower.Contains("kupaći") || lower.Contains("plivačke naočale"))
                return "child swimsuit pool summer";
            if (lower.Contains("čarapa") || lower.Contains("čarape"))
                return "colorful socks child cute";
            if (lower.Contains("haljina") || lower.Contains("suknjica"))
                return "girl dress colorful beautiful";
            if (lower.Contains("pantalone") || lower.Contains("traperice"))
                return "child casual clothes";
            if (lower.Contains("pidžama") || lower.Contains("pidzama"))
                return "child pajamas bedtime cute";

            if (lower.Contains("gitara") || lower.Contains("gitaru") || lower.Contains("gitarom"))
                return "child playing guitar music";
            if (lower.Contains("klavir") || lower.Contains("piano") || lower.Contains("pijanino"))
                return "child playing piano keyboard";
            if (lower.Contains("bubanj") || lower.Contains("bubnjevi") || lower.Contains("bubi"))
                return "child playing drums percussion";
            if (lower.Contains("violina") || lower.Contains("violinu"))
                return "child playing violin music";
            if (lower.Contains("flaut") || lower.Contains("flauta"))
                return "child playing flute music";
            if (lower.Contains("truba") || lower.Contains("trombon") || lower.Contains("saksofon"))
                return "child playing trumpet brass music";
            if (lower.Contains("harmonika") || lower.Contains("harmoniku"))
                return "child playing accordion music";
            if (lower.Contains("ukulele") || lower.Contains("mandolina"))
                return "child playing ukulele music";
            if (lower.Contains("svira") || lower.Contains("instrument") || lower.Contains("orkestar"))
                return "child playing musical instrument";
            if (lower.Contains("pesm") || lower.Contains("pjesm") || lower.Contains("pjesmica"))
                return "child singing microphone stage";
            if (lower.Contains("slušaj") || lower.Contains("sluša ") || lower.Contains("slušati") ||
                lower.Contains("slušamo") || lower.Contains("slušaš"))
                return "child listening headphones music";
            if (lower.Contains("melodija") || lower.Contains("nota") || lower.Contains("note "))
                return "music notes flying colorful";
            if (lower.Contains("ritam") || lower.Contains("takt") || lower.Contains("tempo"))
                return "music beat rhythm children dancing";
            if (lower.Contains("koncert") || lower.Contains("nastup") || lower.Contains("pozornica"))
                return "music concert stage performance";
            if (lower.Contains("muzik") || lower.Contains("glazb"))
                return "music colorful notes children";
            if (lower.Contains("radio") || lower.Contains("zvučnik"))
                return "music radio listening colorful";

            if (lower.Contains("srećan") || lower.Contains("sretna") || lower.Contains("sretan") ||
                lower.Contains("sreća") || lower.Contains("sreca") || lower.Contains("radost") ||
                lower.Contains("veseo") || lower.Contains("vesela"))
                return "happy child joyful smiling";
            if (lower.Contains("tužan") || lower.Contains("tužna") || lower.Contains("plač") ||
                lower.Contains("suze") || lower.Contains("placem"))
                return "child sad crying emotional";
            if (lower.Contains("ljut") || lower.Contains("besn") || lower.Contains("srdit"))
                return "child angry frustrated emotional";
            if (lower.Contains("uplašen") || lower.Contains("strah") || lower.Contains("bojim"))
                return "child scared surprised";
            if (lower.Contains("izneneđen") || lower.Contains("iznenađenje") || lower.Contains("wow"))
                return "child surprised amazed face";
            if (lower.Contains("ponosan") || lower.Contains("ponosna") || lower.Contains("ponos"))
                return "child proud achievement success";
            if (lower.Contains("umoran") || lower.Contains("umorna") || lower.Contains("pospanost"))
                return "child tired sleepy yawning";
            if (lower.Contains("bolestan") || lower.Contains("bolesna") || lower.Contains("boli me"))
                return "child sick bed rest";
            if (lower.Contains("zdrav") || lower.Contains("zdravlje") || lower.Contains("fit"))
                return "child healthy active outdoor";
            if (lower.Contains("zaljubljen") || lower.Contains("voli") || lower.Contains("ljubav") ||
                lower.Contains("srce") || lower.Contains("dragi") || lower.Contains("draga"))
                return "love heart children friendship";
            if (lower.Contains("osećanj") || lower.Contains("osjećanj") || lower.Contains("emocij"))
                return "child expressive emotional face";
            if (lower.Contains("smireno") || lower.Contains("mirno") || lower.Contains("spokojno"))
                return "child calm peaceful serene";
            if (lower.Contains("uzbuđen") || lower.Contains("uzbuđena") || lower.Contains("euforičan"))
                return "child excited happy energetic";

            if (lower.Contains("rođendan") || lower.Contains("rodjendan"))
                return "birthday party children celebration cake";
            if (lower.Contains("božić") || lower.Contains("bozic") || lower.Contains("jelka") ||
                lower.Contains("deda mraz") || lower.Contains("santa"))
                return "christmas tree gifts children";
            if (lower.Contains("nova godina") || lower.Contains("silvest") || lower.Contains("doček"))
                return "new year celebration fireworks children";
            if (lower.Contains("uskrs") || lower.Contains("vaskrs") || lower.Contains("jaje") ||
                lower.Contains("jaja") || lower.Contains("zec uskrs"))
                return "easter eggs colorful spring children";
            if (lower.Contains("halloween") || lower.Contains("noć vještica") || lower.Contains("bundeva"))
                return "halloween pumpkin children costumes";
            if (lower.Contains("valentinovo") || lower.Contains("srce poklanjam"))
                return "valentines day heart love flowers";
            if (lower.Contains("dan majki") || lower.Contains("majčin dan"))
                return "mothers day flowers love family";
            if (lower.Contains("dan očeva") || lower.Contains("očev dan"))
                return "fathers day family love outdoor";
            if (lower.Contains("školski praznici") || lower.Contains("raspust"))
                return "school holidays children vacation";
            if (lower.Contains("festival") || lower.Contains("svečanost"))
                return "festival celebration colorful people";
            if (lower.Contains("vjenčanje") || lower.Contains("venčanje") || lower.Contains("svadba"))
                return "wedding celebration flowers";
            if (lower.Contains("penzij") || lower.Contains("odlazak u penziju"))
                return "retirement celebration family";

            if (lower.Contains("crvena") || lower.Contains("crveno") || lower.Contains("crven"))
                return "red color bright vibrant";
            if (lower.Contains("plava") || lower.Contains("plavo") || lower.Contains("plav"))
                return "blue sky water bright";
            if (lower.Contains("zelena") || lower.Contains("zeleno") || lower.Contains("zelen"))
                return "green nature grass outdoor";
            if (lower.Contains("žuta") || lower.Contains("žuto") || lower.Contains("žut") ||
                lower.Contains("sunčano žut"))
                return "yellow sunshine bright cheerful";
            if (lower.Contains("narančasta") || lower.Contains("narandžasta") || lower.Contains("oranž"))
                return "orange colorful warm sunset";
            if (lower.Contains("ljubičasta") || lower.Contains("violetna") || lower.Contains("lila"))
                return "purple violet colorful flowers";
            if (lower.Contains("ružičasta") || lower.Contains("roze") || lower.Contains("pink"))
                return "pink flowers cute colorful";
            if (lower.Contains("zlatna") || lower.Contains("zlatno") || lower.Contains("zlato"))
                return "golden sunlight treasure bright";
            if (lower.Contains("bela") || lower.Contains("bijela") || lower.Contains("snežno bel"))
                return "white pure clean snow";
            if (lower.Contains("šarena") || lower.Contains("šareno") || lower.Contains("raznobojn"))
                return "colorful rainbow bright children";
            if (lower.Contains("duga") || lower.Contains("duginih boja"))
                return "rainbow colors bright beautiful";

            if (lower.Contains("oči") || lower.Contains("okice") || lower.Contains("oko ") ||
                lower.Contains("okom") || lower.Contains("pogled") || lower.Contains("gleda") ||
                lower.Contains("vidi") || lower.Contains("otvori oči") || lower.Contains("utvori"))
                return "child eyes open looking curious";
            if (lower.Contains("ruke") || lower.Contains("ruku") || lower.Contains("rukom") ||
                lower.Contains("rukica") || lower.Contains("rukice") || lower.Contains("šaka") ||
                lower.Contains("prsti") || lower.Contains("drži se za ruku"))
                return "child hands holding together";
            if (lower.Contains("noge") || lower.Contains("nogama") || lower.Contains("nogice") ||
                lower.Contains("stopala") || lower.Contains("stopalo") || lower.Contains("nožice"))
                return "child feet walking barefoot grass";
            if (lower.Contains("glava") || lower.Contains("glavica") || lower.Contains("kosa") ||
                lower.Contains("kosom") || lower.Contains("pletenice") || lower.Contains("frizura"))
                return "child hair cute portrait";
            if (lower.Contains("uši") || lower.Contains("uho") || lower.Contains("ušice") ||
                lower.Contains("čuje") || lower.Contains("sluša") || lower.Contains("čuti"))
                return "child listening ears music";
            if (lower.Contains("nos") || lower.Contains("nosić") || lower.Contains("miriše") ||
                lower.Contains("miris") || lower.Contains("vonj"))
                return "child smelling flowers nature";
            if (lower.Contains("usta") || lower.Contains("usne") || lower.Contains("osmeh") ||
                lower.Contains("osmijeh") || lower.Contains("smiješak") || lower.Contains("zubi") ||
                lower.Contains("zub ") || lower.Contains("jezik"))
                return "child smile teeth happy portrait";
            if (lower.Contains("lice") || lower.Contains("lica") || lower.Contains("obrazi") ||
                lower.Contains("obraz") || lower.Contains("crvenila"))
                return "child face portrait expression";
            if (lower.Contains("srce ") || lower.Contains("srcem") || lower.Contains("kuca srce") ||
                lower.Contains("srčeko"))
                return "heart love warm feeling";
            if (lower.Contains("stomak") || lower.Contains("trbušić") || lower.Contains("stomačić"))
                return "child belly laughing cute";
            if (lower.Contains("leđa") || lower.Contains("ramena") || lower.Contains("rame"))
                return "child back shoulder outdoor";
            if (lower.Contains("koljena") || lower.Contains("kolena") || lower.Contains("koljenice"))
                return "child kneeling sitting outdoor";
            if (lower.Contains("telo") || lower.Contains("tijelo") || lower.Contains("celo telo") ||
                lower.Contains("cijelo tijelo"))
                return "child body healthy active outdoor";
            if (lower.Contains("koža") || lower.Contains("put") || lower.Contains("ten"))
                return "child skin healthy outdoor";
            if (lower.Contains("mišić") || lower.Contains("jak") || lower.Contains("snažan") ||
                lower.Contains("snazna"))
                return "child strong muscles active sport";

            if (lower.Contains("jutro") || lower.Contains("zora") || lower.Contains("osvanu") ||
                lower.Contains("počni dan") || lower.Contains("pocni dan") || lower.Contains("probudi"))
                return "child morning waking up sunrise";
            if (lower.Contains("podne") || lower.Contains("podnev"))
                return "sunny midday outdoor children";
            if (lower.Contains("veče") || lower.Contains("večer") || lower.Contains("sumrak") ||
                lower.Contains("zalazak"))
                return "sunset evening colorful sky";
            if (lower.Contains("noć ") || lower.Contains("noću") || lower.Contains("laku noć") ||
                lower.Contains("završi dan") || lower.Contains("zavrsi dan"))
                return "child evening bedtime stars";
            if (lower.Contains("ponoć") || lower.Contains("u ponoć"))
                return "midnight stars moon night";

            if (lower.Contains("sanja") || lower.Contains("snovi") || lower.Contains("maštam") ||
                lower.Contains("sanjar"))
                return "child dreaming stars imagination";
            if (lower.Contains("mašta") || lower.Contains("fantazij") || lower.Contains("imaginacij"))
                return "children imagination magical fantasy";
            if (lower.Contains("sloboda") || lower.Contains("slobodan") || lower.Contains("leti slobodn"))
                return "freedom outdoor running open";
            if (lower.Contains("prijateljs") || lower.Contains("drugarst"))
                return "children friendship together happy";
            if (lower.Contains("ljubav") || lower.Contains("volim") || lower.Contains("voljet") ||
                lower.Contains("ljubi"))
                return "love children heart warm";
            if (lower.Contains("nada") || lower.Contains("nadaj") || lower.Contains("nadamo"))
                return "hope bright future children";
            if (lower.Contains("mir ") || lower.Contains("miru") || lower.Contains("miran"))
                return "peace calm nature serene";
            if (lower.Contains("hrabrost") || lower.Contains("hrabar") || lower.Contains("odvažan"))
                return "brave child confident strong";
            if (lower.Contains("blago") || lower.Contains("dragocen") || lower.Contains("najveć"))
                return "treasure gift golden child";
            if (lower.Contains("čudo") || lower.Contains("čudesno") || lower.Contains("magičn"))
                return "magic wonder children amazed";
            if (lower.Contains("rast") || lower.Contains("sazrev") || lower.Contains("odrastanj"))
                return "child growing learning achievement";
            if (lower.Contains("zajedno") || lower.Contains("svi zajed") || lower.Contains("svi smo"))
                return "children group together teamwork";
            if (lower.Contains("priroda") || lower.Contains("sve oko nas") || lower.Contains("okol"))
                return "nature outdoor children exploring";
            if (lower.Contains("simbol") || lower.Contains("znak") || lower.Contains("moćan"))
                return "child happiness joy celebration";
            if (lower.Contains("kraj") || lower.Contains("završetak") || lower.Contains("finali"))
                return "children celebration finish happy";
            if (lower.Contains("početak") || lower.Contains("novi poče") || lower.Contains("polazak"))
                return "child new beginning adventure";
            if (lower.Contains("put ") || lower.Contains("putovan") || lower.Contains("avantum"))
                return "journey adventure children travel";
            if (lower.Contains("zvuk") || lower.Contains("buka") || lower.Contains("tišina"))
                return "sound waves music colorful";
            if (lower.Contains("svetlost") || lower.Contains("svjetlost") || lower.Contains("sjaj") ||
                lower.Contains("sija") || lower.Contains("blista"))
                return "light bright shining beautiful";
            if (lower.Contains("tama") || lower.Contains("mrak") || lower.Contains("noćna"))
                return "night stars moon dark sky";
            if (lower.Contains("toplina") || lower.Contains("toplota") || lower.Contains("greje"))
                return "warm cozy home family";
            if (lower.Contains("hladnoć") || lower.Contains("hladno") || lower.Contains("smrzava"))
                return "cold winter snow outdoor";

            if (!string.IsNullOrEmpty(analysis.Context) && !string.IsNullOrEmpty(analysis.Mood))
            {
                string moodVisual = analysis.Mood switch
                {
                    "happy" => "happy joyful bright",
                    "calm" => "peaceful calm serene",
                    "excited" => "energetic vibrant colorful",
                    "playful" => "playful fun children",
                    "joyful" => "joyful celebration bright",
                    "energetic" => "active dynamic movement",
                    "upbeat" => "cheerful bright uplifting",
                    _ => "happy bright colorful"
                };
                string contextVisual = analysis.Context switch
                {
                    "dance" => "children dancing",
                    "lullaby" => "child peaceful quiet",
                    "party" => "children celebrating",
                    "nature" => "nature outdoor green",
                    "school" => "children learning",
                    "health" => "child active outdoor",
                    "animal" => "animals cute",
                    _ => "child playing"
                };
                return $"{contextVisual} {moodVisual}";
            }

            return analysis.Context switch
            {
                "outdoor" => "child outdoor activity playing",
                "dance" => "children dancing joyful",
                "lullaby" => "child sleeping peaceful bedroom soft light",
                "party" => "child celebrating birthday party",
                "nature" => "children nature outdoor exploring",
                "health" => "child active healthy running outdoor",
                _ => "child playing outdoor happy"
            };
        }
        private string InferAmbientFromLyric(string lyric, string context)
        {
            string lower = lyric.ToLower();
            bool isJoyfulContext = context is "outdoor" or "health" or "fun" or "party"
                                             or "dance" or "seasons" or "animal" or "school";

            if (lower.Contains("ptic") || lower.Contains("cvrkut") ||
                lower.Contains("pjev") || lower.Contains("lastavic") ||
                lower.Contains("vrabac") || lower.Contains("slavuj") ||
                lower.Contains("paunov") || lower.Contains("golub"))
                return "animal bird chirp";

            if (lower.Contains("reka") || lower.Contains("rijeka") ||
                lower.Contains("potok") || lower.Contains("bujica") ||
                lower.Contains("izvor") || lower.Contains("voda teč") ||
                lower.Contains("fontana") || lower.Contains("česma"))
                return "ambience creek stream";

            if (lower.Contains("more") || lower.Contains("plaža") ||
                lower.Contains("talas") || lower.Contains("obala") ||
                lower.Contains("brod") || lower.Contains("ocean"))
                return "ambience ocean shore";

            if (lower.Contains("prska") || lower.Contains("pljas") ||
                lower.Contains("bara") || lower.Contains("lokva"))
                return "liquid water splash";

            if (lower.Contains("kiša") || lower.Contains("pada kiša") ||
                lower.Contains("kaplja") || lower.Contains("mokro") ||
                lower.Contains("drizzle"))
                return "weather ambience rain drips";

            if (lower.Contains("grmljavina") || lower.Contains("oluja") ||
                lower.Contains("munja") || lower.Contains("grom"))
                return "weather ambience thunderstorm";

            if (lower.Contains("vetar") || lower.Contains("vjetar") ||
                lower.Contains("povjetarac") || lower.Contains("duva"))
                return isJoyfulContext
                    ? "ambience nature field windy"
                    : "weather ambience hurricane wind";

            if (lower.Contains("zima") || lower.Contains("sneg") ||
                lower.Contains("snijeg") || lower.Contains("mraz") ||
                lower.Contains("led") || lower.Contains("mećava"))
                return isJoyfulContext
                    ? "weather snow boots jumping"
                    : "weather ambience blizzard";

            if (lower.Contains("čizme") || lower.Contains("rukavice") ||
                lower.Contains("skafander") || lower.Contains("kapu") ||
                lower.Contains("šal") || lower.Contains("kaput"))
                return isJoyfulContext
                    ? "weather snow footstep"
                    : "weather ambience blizzard";

            if (lower.Contains("šuma") || lower.Contains("suma") ||
                lower.Contains("drveć") || lower.Contains("grana") ||
                lower.Contains("lisće") || lower.Contains("lišće") ||
                lower.Contains("šušti"))
                return "ambience nature trail";

            if (lower.Contains("proljeć") || lower.Contains("proleć") ||
                lower.Contains("cvijet") || lower.Contains("cvece") ||
                lower.Contains("bujanje") || lower.Contains("procvat"))
                return "animal bird chirp";

            if (lower.Contains("leto") || lower.Contains("ljeto") ||
                lower.Contains("sunce") || lower.Contains("toplo") ||
                lower.Contains("vrućina") || lower.Contains("cvrčci"))
                return "animal ambience crickets";

            if (lower.Contains("jesen") || lower.Contains("lišće pada") ||
                lower.Contains("opada") || lower.Contains("zlatno") ||
                lower.Contains("žuto lišće"))
                return "ambience dirt road woods";

            if (lower.Contains("trči") || lower.Contains("trčanje") ||
                lower.Contains("skoči") || lower.Contains("skači") ||
                lower.Contains("blistaj") || lower.Contains("juri"))
                return "ambience children group playground";

            if (lower.Contains("smej") || lower.Contains("smije") ||
                lower.Contains("smeh") || lower.Contains("veselo") ||
                lower.Contains("haha") || lower.Contains("raduj"))
                return "ambience children group playground";

            if (lower.Contains("šetaj") || lower.Contains("šetnja") ||
                lower.Contains("hodaj") || lower.Contains("korak") ||
                lower.Contains("prošetaj") || lower.Contains("idi"))
                return "ambience nature trail";

            if (lower.Contains("igraj") || lower.Contains("igra ") ||
                lower.Contains("igrice") || lower.Contains("zabav"))
                return "ambience children group playground";

            if (lower.Contains("prskal") || lower.Contains("tušir") ||
                lower.Contains("kupan"))
                return "ambience children sprinkler";

            if (lower.Contains("park") || lower.Contains("parkić") ||
                lower.Contains("klackalica") || lower.Contains("ljuljaška") ||
                lower.Contains("tobogan") || lower.Contains("peskovnik"))
                return "ambience children group playground distant";

            if (lower.Contains("dvorišt") || lower.Contains("bašt") ||
                lower.Contains("vrt"))
                return "ambience backyard road";

            if (lower.Contains("grad") || lower.Contains("ulica") ||
                lower.Contains("sokak") || lower.Contains("centar"))
                return "ambience downtown area";

            if (lower.Contains("kuća") || lower.Contains("kuca") ||
                lower.Contains("dom") || lower.Contains("soba") ||
                lower.Contains("unutra") || lower.Contains("topla"))
                return isJoyfulContext
                    ? "ambience backyard road"
                    : "ambience nature 180";

            if (lower.Contains("planina") || lower.Contains("vrh") ||
                lower.Contains("klisura") || lower.Contains("pećina"))
                return "ambience nature field windy";

            if (lower.Contains("movar") || lower.Contains("bara") ||
                lower.Contains("ritov"))
                return "ambience nature near swamp";

            if (lower.Contains("sladoled"))
                return "ambience children group playground";

            if (lower.Contains("čaj") || lower.Contains("kakao") ||
                lower.Contains("čokolada") || lower.Contains("topli napit"))
                return isJoyfulContext
                    ? "ambience backyard road"
                    : "ambience nature 180";

            if (lower.Contains("torta") || lower.Contains("kolač") ||
                lower.Contains("slatkiš") || lower.Contains("roćendan"))
                return "ambience children group playground";

            if (lower.Contains("pas") || lower.Contains("kučić") ||
                lower.Contains("štene"))
                return "animal dog bark";

            if (lower.Contains("maca") || lower.Contains("mačka") ||
                lower.Contains("mače"))
                return "animal mammal cat domestic meow";

            if (lower.Contains("konj") || lower.Contains("konjanik"))
                return "animal horse canters";

            if (lower.Contains("pčela") || lower.Contains("leptir") ||
                lower.Contains("buba"))
                return "animal ambience crickets";

            if (lower.Contains("žaba") || lower.Contains("kvakav"))
                return "animal frog chirp";

            if (lower.Contains("zec") || lower.Contains("vjeverica") ||
                lower.Contains("jelenić"))
                return "ambience nature trail";

            if (lower.Contains("majmun") || lower.Contains("džungla"))
                return "animal ambience jungle";

            if (lower.Contains("mama") || lower.Contains("majka") ||
                lower.Contains("tata") || lower.Contains("otac"))
                return "ambience backyard road";

            if (lower.Contains("baka") || lower.Contains("deka") ||
                lower.Contains("porodic"))
                return "ambience nature trail";

            if (lower.Contains("drugar") || lower.Contains("prijatelj") ||
                lower.Contains("zajedno") || lower.Contains("svi"))
                return "ambience children group playground";

            if (lower.Contains("spavaj") || lower.Contains("zaspi") ||
                lower.Contains("laku noć") || lower.Contains("usni") ||
                lower.Contains("san ") || lower.Contains("sanjaj"))
                return isJoyfulContext
                    ? "animal bird chirp"
                    : "ambience nature 180";

            if (lower.Contains("noć") || lower.Contains("zvijezd") ||
                lower.Contains("mesec") || lower.Contains("tišina"))
                return isJoyfulContext
                    ? "animal bird chirp"
                    : "ambience night crickets";

            if (lower.Contains("zdravo") || lower.Contains("zdravlje") ||
                lower.Contains("jako") || lower.Contains("snažno") ||
                lower.Contains("sport") || lower.Contains("trening"))
                return "ambience children group playground";

            return context switch
            {
                "lullaby" => "ambience nature 180",
                "outdoor" => "ambience children group playground",
                "health" => "ambience nature trail",
                "nature" => "ambience creek stream",
                "adventure" => "ambience nature trail",
                "sad" => "weather ambience rain drips",
                "party" => "ambience children group playground",
                "christmas" => "weather snow boots jumping",
                "animal" => "animal bird chirp",
                "seasons" => "animal bird chirp",
                "dance" => "ambience children group playground",
                "school" => "ambience children group playground distant",
                "love" => "ambience nature trail",
                "fun" => "ambience children group playground",
                _ => "animal bird chirp"
            };
        }

        private string ExtractJson(string text, bool isArray = false)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"```json|```", "").Trim();
            try
            {
                if (isArray)
                {
                    int start = text.IndexOf('[');
                    int end = text.LastIndexOf(']');
                    if (start >= 0 && end > start) return text.Substring(start, end - start + 1);
                }
                else
                {
                    int start = text.IndexOf('{');
                    int end = text.LastIndexOf('}');
                    if (start >= 0 && end > start) return text.Substring(start, end - start + 1);
                }
            }
            catch { }
            return null;
        }

        private async Task<StoryBoard> GenerateStoryBoard(List<string> lyrics, CancellationToken ct)
        {
            var analysis = await AnalyseSongWithAI(lyrics, ct);
            _detectedContext = string.IsNullOrWhiteSpace(analysis.Context) ? "fun" : analysis.Context;
            _detectedMood = string.IsNullOrWhiteSpace(analysis.Mood) ? "happy" : analysis.Mood;

            if (_contextKeywords.TryGetValue(_detectedContext, out var ctxList))
                _universalKeywords = new List<string>(ctxList);

            var perLyricKeywords = await GenerateKeywordsPerLyric(lyrics, analysis, ct);

            var scenes = new List<StoryScene>();
            for (int i = 0; i < lyrics.Count; i++)
            {
                var kw = perLyricKeywords.FirstOrDefault(k => k.Line == i + 1);
                string keywords = kw?.Keywords ?? GenerateKeywordsFromLyric(lyrics[i], analysis);
                string ambient = kw?.Ambient ?? InferAmbientFromLyric(lyrics[i], analysis.Context);
                ambient = NormalizeAmbientFromAI(ambient, lyrics[i], analysis.Context ?? "outdoor");
                int energy = CalculateEnergy(i, lyrics.Count, analysis.Context);

                scenes.Add(new StoryScene
                {
                    SceneNumber = i + 1,
                    Description = lyrics[i].Length > 60 ? lyrics[i].Substring(0, 57) + "..." : lyrics[i],
                    Emotion = analysis.Mood ?? "happy",
                    Energy = energy,
                    Characters = analysis.MainSubject ?? "children",
                    Action = ExtractActionFromLyric(lyrics[i]),
                    Location = analysis.Setting ?? "outdoor",
                    Keywords = keywords,
                    AmbientSound = ambient
                });
            }

            string overallTheme = analysis.Theme ?? (_detectedContext switch
            {
                "lullaby" => "Mirna uspavanka",
                "party" => "Vesela proslava",
                "love" => "Ljubavna priča",
                "sad" => "Emotivno putovanje",
                "adventure" => "Uzbudljiva avantura",
                "dance" => "Veseli ples",
                "christmas" => "Čarobni Božić",
                "outdoor" => "Aktivni život na otvorenom",
                "seasons" => "Ljepota godišnjih doba",
                "health" => "Zdravo i aktivno dijete",
                _ => "Vesela dječija pjesma"
            });

            LogToMainWindow($"✅ Story board kreiran: {scenes.Count} scena za temu: '{overallTheme}'");
            return new StoryBoard { Scenes = scenes, MainCharacter = analysis.MainSubject ?? "Happy child", OverallTheme = overallTheme };
        }

        private int CalculateEnergy(int index, int total, string context)
        {
            if (context == "lullaby") return 1;
            if (context == "sad") return index < 2 ? 1 : 2;
            double pos = (double)index / total;
            if (pos < 0.15) return 2;
            if (pos < 0.30) return 3;
            if (pos >= 0.60 && pos < 0.80) return 5;
            if (pos >= 0.85) return 2;
            return 4;
        }

        private string ExtractActionFromLyric(string lyric)
        {
            string lower = lyric.ToLower();

            if (lower.Contains("trči") || lower.Contains("trčanje") || lower.Contains("juri")) return "running";
            if (lower.Contains("šetaj") || lower.Contains("šeta") || lower.Contains("šetnja") || lower.Contains("šeće") || lower.Contains("hoda")) return "walking";
            if (lower.Contains("skoči") || lower.Contains("skači") || lower.Contains("skakanje") || lower.Contains("poskakuje")) return "jumping";
            if (lower.Contains("leti") || lower.Contains("leteći") || lower.Contains("lebdi")) return "flying";
            if (lower.Contains("pliva") || lower.Contains("kupanje") || lower.Contains("ronjen")) return "swimming";
            if (lower.Contains("vozi") || lower.Contains("bicikl")) return "riding bicycle";
            if (lower.Contains("pleši") || lower.Contains("plešeš") || lower.Contains("zaigraj") || lower.Contains("zaigra") || lower.Contains("brza") || lower.Contains("tancuj")) return "dancing";
            if (lower.Contains("penje") || lower.Contains("penjanje") || lower.Contains("penj")) return "climbing";

            if (lower.Contains("peva") || lower.Contains("pjeva") || lower.Contains("pevaj") || lower.Contains("pjevaj") || lower.Contains("zapeva")) return "singing";
            if (lower.Contains("svira") || lower.Contains("sviranje")) return "playing instrument";
            if (lower.Contains("crta") || lower.Contains("slika") || lower.Contains("boji") || lower.Contains("pravi")) return "drawing painting";
            if (lower.Contains("čita") || lower.Contains("knjig")) return "reading book";
            if (lower.Contains("igraj") || lower.Contains("igra") || lower.Contains("igraju")) return "playing";
            if (lower.Contains("gradi") || lower.Contains("pravi") || lower.Contains("kocke")) return "building blocks";
            if (lower.Contains("lopta") || lower.Contains("šutiraj") || lower.Contains("baca")) return "playing ball";

            if (lower.Contains("smej") || lower.Contains("smije") || lower.Contains("smeh") || lower.Contains("smijeh") || lower.Contains("blistaj")) return "laughing";
            if (lower.Contains("plač") || lower.Contains("suza")) return "crying";
            if (lower.Contains("grli") || lower.Contains("zagrli") || lower.Contains("mazi")) return "hugging";
            if (lower.Contains("ljubi") || lower.Contains("polj")) return "kissing cheek";

            if (lower.Contains("spava") || lower.Contains("sanja") || lower.Contains("zaspi") || lower.Contains("zadrema") || lower.Contains("lagana")) return "sleeping";
            if (lower.Contains("odmara") || lower.Contains("sedi") || lower.Contains("sjedi") || lower.Contains("leži")) return "relaxing";
            if (lower.Contains("slušaj") || lower.Contains("sluša") || lower.Contains("slusaj")) return "listening";

            if (lower.Contains("jede") || lower.Contains("jedi") || lower.Contains("sladoled") || lower.Contains("torta")) return "eating";
            if (lower.Contains("pije") || lower.Contains("pij") || lower.Contains("sok") || lower.Contains("čaj")) return "drinking";

            if (lower.Contains("upoznaj") || lower.Contains("otkriva") || lower.Contains("istražu")) return "exploring";
            if (lower.Contains("gleda") || lower.Contains("posmatra") || lower.Contains("vidi") || lower.Contains("viri")) return "watching";

            return "enjoying";
        }

        private bool ValidateStoryBoard(StoryBoard storyBoard)
        {
            if (storyBoard?.Scenes == null || storyBoard.Scenes.Count < 3)
            {
                LogToMainWindow("❌ Validacija neuspješna: Nema dovoljno scena");
                return false;
            }

            var scenes = storyBoard.Scenes;
            bool valid = true;

            bool requiresPeak = _detectedContext != "lullaby" && _detectedContext != "sad";

            if (requiresPeak)
            {
                bool hasPeak = scenes.Any(s => s.Energy >= 4);
                if (!hasPeak)
                {
                    LogToMainWindow("⚠️ Nema scene sa visokom energijom (peak)");
                    valid = false;
                }
            }

            if (_detectedContext == "lullaby")
            {
                bool allCalm = scenes.All(s => s.Energy <= 2);
                if (!allCalm)
                {
                    LogToMainWindow("⚠️ Uspavanka ima scenu sa previsoko energijom");
                    valid = false;
                }
            }

            if (valid)
                LogToMainWindow("✅ Story board validacija uspješna!");
            else
                LogToMainWindow("❌ Story board validacija neuspješna");

            return valid;
        }

        private StoryBoard ParseStoryBoard(string response)
        {
            try
            {
                int start = response.IndexOf('[');
                int end = response.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    string json = response.Substring(start, end - start + 1);
                    json = System.Text.RegularExpressions.Regex.Replace(json, @"\]\s*,\s*\[", "],[");

                    if (!json.TrimStart().StartsWith("[") && json.Contains("{"))
                    {
                        json = $"[{json}]";
                    }

                    var scenes = Newtonsoft.Json.JsonConvert.DeserializeObject<List<StoryScene>>(json);

                    if (scenes != null && scenes.Count > 0)
                    {
                        LogToMainWindow($"✅ Uspješno parsirano {scenes.Count} scena");

                        string overallTheme = _detectedContext switch
                        {
                            "lullaby" => "Peaceful bedtime story",
                            "party" => "Joyful birthday celebration",
                            "love" => "Romantic love journey",
                            "sad" => "Emotional reflection",
                            "adventure" => "Exciting exploration",
                            "dance" => "Joyful dance story",
                            "christmas" => "Magical Christmas story",
                            "animal" => "Animal friends adventure",
                            "school" => "Learning and discovery",
                            "nature" => "Beautiful nature journey",
                            _ => _selectedTheme == "calm" ? "Peaceful adventure" :
                                 _selectedTheme == "educational" ? "Learning adventure" :
                                 _selectedTheme == "action" ? "Exciting adventure" : "Fun adventure"
                        };

                        return new StoryBoard
                        {
                            Scenes = scenes,
                            MainCharacter = scenes[0]?.Characters ?? "Happy child",
                            OverallTheme = overallTheme
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow($"❌ Greška pri parsiranju: {ex.Message}");
            }

            return CreateFallbackStoryBoard();
        }

        private StoryBoard CreateFallbackStoryBoard()
        {
            LogToMainWindow("🔄 Koristim fallback story board");
            var scenes = new List<StoryScene>();

            string mainCharacter = _detectedContext switch
            {
                "lullaby" => "Sleeping baby in cozy crib",
                "party" => "Happy child at birthday party",
                "love" => "Couple walking in golden sunset",
                "sad" => "Child looking out rainy window",
                "adventure" => "Boy with backpack exploring forest",
                "dance" => "Girl spinning and dancing joyfully",
                "christmas" => "Children opening Christmas gifts",
                "animal" => "Child playing with cute puppy",
                "school" => "Children learning in colorful classroom",
                "nature" => "Child running through flower meadow",
                _ => "Happy child playing in sunny park"
            };

            string[] emotions = _detectedContext switch
            {
                "lullaby" => new[] { "sleepy", "peaceful", "calm", "dreamy", "gentle" },
                "party" => new[] { "excited", "joyful", "happy", "funny", "surprised" },
                "love" => new[] { "loving", "romantic", "happy", "nostalgic", "peaceful" },
                "sad" => new[] { "sad", "nostalgic", "calm", "peaceful", "hopeful" },
                "adventure" => new[] { "curious", "excited", "brave", "happy", "triumphant" },
                "dance" => new[] { "joyful", "excited", "playful", "happy", "energetic" },
                _ => new[] { "happy", "playful", "excited", "calm", "curious" }
            };

            string[] actions = _detectedContext switch
            {
                "lullaby" => new[] { "sleeping", "yawning", "snuggling", "dreaming", "resting" },
                "party" => new[] { "blowing candles", "opening gifts", "dancing", "laughing", "celebrating" },
                "love" => new[] { "walking together", "holding hands", "smiling", "hugging", "looking at sunset" },
                "sad" => new[] { "sitting quietly", "looking out window", "walking alone", "reflecting", "watching rain" },
                "adventure" => new[] { "exploring", "climbing", "running", "discovering", "hiking" },
                "dance" => new[] { "dancing", "spinning", "jumping", "moving", "swaying" },
                _ => new[] { "playing", "running", "dancing", "walking", "jumping" }
            };

            string[] locations = _detectedContext switch
            {
                "lullaby" => new[] { "cozy bedroom", "moonlit nursery", "starry night room", "soft lamp lit bedroom" },
                "party" => new[] { "birthday party room", "decorated garden", "festive hall", "colorful backyard" },
                "love" => new[] { "sunset beach", "flower garden", "park at golden hour", "romantic forest path" },
                "sad" => new[] { "rainy window", "empty park", "autumn forest", "grey cloudy day" },
                "adventure" => new[] { "forest clearing", "mountain path", "meadow", "hidden trail", "river bank" },
                "dance" => new[] { "dance floor", "sunny park", "colorful stage", "outdoor festival" },
                _ => new[] { "park", "garden", "playground", "sunny field" }
            };

            string[] keywordsList = _universalKeywords.ToArray();
            string[] ambientSounds = _detectedContext switch
            {
                "lullaby" => new[] { "soft lullaby music", "none", "gentle wind", "none", "soft music box" },
                "party" => new[] { "children laughing", "party noise", "children laughing", "joyful celebration sounds" },
                "sad" => new[] { "rain", "wind", "rain", "none", "distant birds" },
                "nature" => new[] { "birds", "leaves", "forest", "water", "wind" },
                "christmas" => new[] { "none", "fireplace", "children laughing", "none", "wind" },
                _ => new[] { "birds", "children laughing", "wind", "leaves", "playground" }
            };

            for (int i = 0; i < _lyricLines.Count; i++)
            {
                int energy = 3;
                if (_detectedContext == "lullaby")
                {
                    energy = 1;
                }
                else if (_detectedContext == "sad")
                {
                    energy = i < 2 ? 1 : 2;
                }
                else if (_detectedContext == "party" || _detectedContext == "dance")
                {
                    if (i < 1) energy = 2;
                    else if (i >= _lyricLines.Count - 1) energy = 2;
                    else energy = 4;
                }
                else
                {
                    if (i < 2) energy = 2;
                    else if (i > _lyricLines.Count - 3) energy = 2;
                    else if (i > 2 && i < _lyricLines.Count - 3) energy = 4;
                    if (_lyricLines.Count >= 10 && i == (int)(_lyricLines.Count * 0.7)) energy = 5;
                }

                scenes.Add(new StoryScene
                {
                    SceneNumber = i + 1,
                    Description = _lyricLines[i].Length > 50 ? _lyricLines[i].Substring(0, 47) + "..." : _lyricLines[i],
                    Emotion = emotions[i % emotions.Length],
                    Energy = energy,
                    Characters = mainCharacter,
                    Action = actions[i % actions.Length],
                    Location = locations[i % locations.Length],
                    Keywords = keywordsList[i % keywordsList.Length],
                    AmbientSound = ambientSounds[i % ambientSounds.Length]
                });
            }

            string overallTheme = _detectedContext switch
            {
                "lullaby" => "Peaceful bedtime",
                "party" => "Joyful celebration",
                "love" => "Romantic love story",
                "sad" => "Emotional journey",
                "adventure" => "Exciting adventure",
                "dance" => "Joyful dance",
                "christmas" => "Magical Christmas",
                "animal" => "Animal friends",
                "school" => "Learning adventure",
                "nature" => "Nature beauty",
                _ => _selectedTheme == "calm" ? "Peaceful adventure" :
                       _selectedTheme == "educational" ? "Learning adventure" :
                       _selectedTheme == "action" ? "Exciting adventure" : "Fun adventure"
            };

            return new StoryBoard
            {
                Scenes = scenes,
                MainCharacter = mainCharacter,
                OverallTheme = overallTheme
            };
        }

        #endregion

        #region Pixabay Music API (samo za muziku, NE za zvukove)

        private async Task<string> DownloadBackgroundMusic(string mood, double targetDuration, string tempDir, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_pixabayApiKey))
                return null;

            string searchQuery = _detectedContext switch
            {
                "lullaby" => "lullaby soft piano baby sleep",
                "party" => "happy upbeat birthday kids celebration",
                "love" => "romantic piano soft acoustic love",
                "sad" => "melancholy piano emotional soft",
                "adventure" => "adventure orchestral energetic exploration",
                "dance" => "upbeat dance kids fun energetic",
                "christmas" => "christmas joyful holiday bells warm",
                "animal" => "playful cute whimsical animals fun",
                "school" => "playful educational kids learning fun",
                "nature" => "nature acoustic peaceful gentle outdoor",
                _ => mood?.ToLower() switch
                {
                    "happy" or "joyful" => "happy children upbeat",
                    "calm" or "peaceful" => "calm relaxing piano",
                    "excited" or "energetic" => "upbeat energetic fun",
                    "playful" => "playful funny kids",
                    "melancholy" => "melancholy emotional soft piano",
                    "romantic" => "romantic acoustic soft piano",
                    _ => "children happy uplifting"
                }
            };

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await _apiRateLimiter.WaitAsync(ct);

                    try
                    {
                        string url = $"https://pixabay.com/api/music/?key={_pixabayApiKey}&q={Uri.EscapeDataString(searchQuery)}&duration=30-300";
                        string response = await _httpClient.GetStringAsync(url, ct);
                        var json = JObject.Parse(response);
                        var hits = json["hits"] as JArray;

                        if (hits == null || hits.Count == 0)
                        {
                            url = $"https://pixabay.com/api/music/?key={_pixabayApiKey}&q=happy%20kids&duration=30-300";
                            response = await _httpClient.GetStringAsync(url, ct);
                            json = JObject.Parse(response);
                            hits = json["hits"] as JArray;
                        }

                        if (hits != null && hits.Count > 0)
                        {
                            var rng = new Random(Guid.NewGuid().GetHashCode());
                            var hit = hits[rng.Next(Math.Min(5, hits.Count))];
                            string audioUrl = hit["audio"]?["url"]?.ToString();

                            if (!string.IsNullOrEmpty(audioUrl))
                            {
                                string fileName = $"background_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3";
                                string outputPath = Path.Combine(tempDir, fileName);

                                LogToMainWindow($"🎵 Preuzimam pozadinsku muziku: {hit["title"]?.ToString() ?? searchQuery}");

                                using var dlStream = await _dlHttpClient.GetStreamAsync(audioUrl, ct);
                                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                                await dlStream.CopyToAsync(fileStream, ct);

                                double duration = hit["duration"]?.Value<double>() ?? 0;
                                if (duration < targetDuration && duration > 0)
                                {
                                    return await LoopAudio(outputPath, targetDuration, tempDir, ct);
                                }

                                return outputPath;
                            }
                        }
                    }
                    finally
                    {
                        _apiRateLimiter.Release();
                    }
                }
                catch (Exception ex)
                {
                    LogToMainWindow($"❌ Greška pri preuzimanju muzike: {ex.Message}");
                }
            }

            return null;
        }

        private async Task<string> LoopAudio(string audioPath, double targetDuration, string tempDir, CancellationToken ct)
        {
            string outputPath = Path.Combine(tempDir, $"looped_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
                return audioPath;

            string args = $"-nostdin -stream_loop -1 -i \"{audioPath}\" -t {targetDuration.ToString(CultureInfo.InvariantCulture)} -c copy -y \"{outputPath}\"";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true
                }
            };
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(ct);

            return (process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                ? outputPath : audioPath;
        }

        private async Task<double> GetAudioDuration(string audioPath)
        {
            try
            {
                string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath)) return 180.0;

                string args = $"-nostdin -i \"{audioPath}\" -f null -";
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true
                    }
                };
                process.Start();
                process.StandardInput.Close();
                var _so1 = process.StandardOutput.ReadToEndAsync();
                var _se1 = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(_so1, _se1);
                await process.WaitForExitAsync();
                string output = _se1.Result;

                var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                if (match.Success)
                {
                    int hours = int.Parse(match.Groups[1].Value);
                    int minutes = int.Parse(match.Groups[2].Value);
                    double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    return hours * 3600 + minutes * 60 + seconds;
                }
                return 180.0;
            }
            catch { return 180.0; }
        }

        #endregion

        #region Audio Processing (Lokalni zvukovi — bez Freesound)

        private async Task<string> GetAmbientSoundPath(string soundType, string tempDir, CancellationToken ct)
        {
            LogToMainWindow($"🔊 GetAmbientSoundPath: soundType='{soundType}', context='{_detectedContext}', AmbientSoundsEnabled={_enableAmbientSounds}");

            if (!_enableAmbientSounds || string.IsNullOrEmpty(soundType) || soundType == "none")
            {
                LogToMainWindow($"🔊 Ambijentalni zvukovi isključeni ili soundType prazan");
                return null;
            }

            string localPath = await GetLocalAmbientSound(soundType, tempDir);
            if (localPath != null)
            {
                LogToMainWindow($"✅ Lokalni ambijentalni zvuk: {localPath}");
                return localPath;
            }

            LogToMainWindow($"⚠️ Nema ambijentalnog zvuka za '{soundType}' u lokalnoj biblioteci.");
            return null;
        }

        private string NormalizeAmbientFromAI(string aiAmbient, string lyric, string context)
        {
            if (string.IsNullOrWhiteSpace(aiAmbient))
                return InferAmbientFromLyric(lyric, context);

            string lower = aiAmbient.ToLower();

            var knownTypes = new[] {
                "animal bird", "ambience creek", "ambience stream", "ambience ocean",
                "ambience nature", "ambience children", "ambience backyard",
                "ambience downtown", "ambience dirt road", "ambience night",
                "animal dog", "animal horse", "animal frog", "animal mammal cat",
                "animal ambience crickets", "animal ambience jungle",
                "weather ambience rain", "weather ambience thunderstorm",
                "weather ambience hurricane", "weather ambience blizzard",
                "weather snow", "liquid water"
            };
            if (knownTypes.Any(k => lower.StartsWith(k)))
                return aiAmbient;

            if (lower.Contains("ptic") || lower.Contains("bird") ||
                lower.Contains("cvrkut") || lower.Contains("šume") ||
                lower.Contains("šuma") || lower.Contains("drveć") ||
                lower.Contains("prirode") || lower.Contains("grana"))
                return "animal bird chirp";

            if (lower.Contains("park") || lower.Contains("šetanj") ||
                lower.Contains("staza") || lower.Contains("pješčan") ||
                lower.Contains("koraka") || lower.Contains("hodanj"))
                return "ambience nature trail";

            if (lower.Contains("djec") || lower.Contains("djet") || lower.Contains("child") ||
                lower.Contains("igre") || lower.Contains("playground") ||
                lower.Contains("kretanj") || lower.Contains("trčanj"))
                return "ambience children group playground";

            if (lower.Contains("zim") || lower.Contains("snijeg") || lower.Contains("sneg") ||
                lower.Contains("snow") || lower.Contains("hlad") || lower.Contains("mraz"))
                return "weather snow boots jumping";

            if (lower.Contains("kiša") || lower.Contains("rain") ||
                lower.Contains("kaplja") || lower.Contains("mokro"))
                return "weather ambience rain drips";

            if (lower.Contains("grmlj") || lower.Contains("thunder") ||
                lower.Contains("oluja") || lower.Contains("munja"))
                return "weather ambience thunderstorm";

            if (lower.Contains("vetar") || lower.Contains("wind") ||
                lower.Contains("vjetar") || lower.Contains("povjetar"))
                return "ambience nature field windy";

            if (lower.Contains("voda") || lower.Contains("water") ||
                lower.Contains("potok") || lower.Contains("rijeka") ||
                lower.Contains("reka") || lower.Contains("stream"))
                return "ambience creek stream";

            if (lower.Contains("more") || lower.Contains("ocean") ||
                lower.Contains("plaža") || lower.Contains("talas"))
                return "ambience ocean shore";

            if (lower.Contains("kuć") || lower.Contains("restoran") || lower.Contains("indoor") ||
                lower.Contains("topli") || lower.Contains("dom") || lower.Contains("soba"))
                return "ambience backyard road";

            if (lower.Contains("ljeto") || lower.Contains("leto") || lower.Contains("summer") ||
                lower.Contains("sunce") || lower.Contains("toplo") || lower.Contains("cvrčci"))
                return "animal ambience crickets";

            if (lower.Contains("noć") || lower.Contains("night") ||
                lower.Contains("zvijezd") || lower.Contains("mesec"))
                return "ambience night crickets";

            if (lower.Contains("džungla") || lower.Contains("jungle") ||
                lower.Contains("tropsk") || lower.Contains("majmun"))
                return "animal ambience jungle";

            if (lower.Contains("pas") || lower.Contains("dog") ||
                lower.Contains("kučić") || lower.Contains("bark"))
                return "animal dog bark";

            if (lower.Contains("konj") || lower.Contains("horse") ||
                lower.Contains("kopita"))
                return "animal horse canters";

            if (lower.Contains("mačka") || lower.Contains("cat") ||
                lower.Contains("maca") || lower.Contains("meow"))
                return "animal mammal cat domestic meow";

            if (lower.Contains("žaba") || lower.Contains("frog"))
                return "animal frog chirp";

            return InferAmbientFromLyric(lyric, context);
        }

        private static Dictionary<string, List<string>> _soundIndex = null;
        private static Dictionary<string, List<string>> _sfxIndex = null;
        private static readonly object _soundIndexLock = new object();

        private static Dictionary<string, List<string>> BuildSoundIndex(string folder)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(folder)) return index;

            var supportedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp3", ".wav", ".flac", ".ogg", ".aiff", ".aif" };

            foreach (string file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (!supportedExt.Contains(Path.GetExtension(file))) continue;

                string name = Path.GetFileNameWithoutExtension(file).ToLower();

                var rawTokens = System.Text.RegularExpressions.Regex.Split(name, @"[_\-\s]+");
                var tags = new List<string>();
                foreach (string token in rawTokens)
                {
                    var camel = System.Text.RegularExpressions.Regex.Replace(
                        token, @"([a-z])([A-Z])", "$1 $2").ToLower().Split(' ');
                    tags.AddRange(camel.Where(t => t.Length > 1));
                }

                tags.Add(name.Replace("_", " ").Replace("-", " "));

                index[file] = tags;
            }

            return index;
        }

        private static string FindBestMatch(Dictionary<string, List<string>> index,
                                            IEnumerable<string> queryTags,
                                            HashSet<string> usedFiles = null)
        {
            if (index == null || index.Count == 0) return null;

            var queryList = queryTags
                .Select(t => t.ToLower().Trim())
                .Where(t => t.Length > 1)
                .ToList();

            if (queryList.Count == 0) return null;

            string bestFile = null;
            int bestScore = 0;

            foreach (var (file, tags) in index)
            {
                if (usedFiles != null && usedFiles.Contains(file)) continue;

                int score = 0;
                foreach (string q in queryList)
                {
                    if (tags.Any(t => t == q)) score += 3;
                    else if (tags.Any(t => t.Contains(q) || q.Contains(t))) score += 1;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestFile = file;
                }
            }

            return bestScore > 0 ? bestFile : null;
        }

        private async Task<string> GetLocalAmbientSound(string soundType, string tempDir)
        {
            string soundsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds");

            lock (_soundIndexLock)
            {
                if (_soundIndex == null)
                {
                    _soundIndex = BuildSoundIndex(soundsDir);
                    LogToMainWindow($"🎵 Zvučni indeks: {_soundIndex.Count} fajlova u Assets/Sounds/");
                }
            }

            if (_soundIndex.Count == 0)
            {
                LogToMainWindow($"⚠️ Assets/Sounds/ prazan ili ne postoji");
                return null;
            }

            bool isOutdoorContext = _detectedContext is "outdoor" or "health" or "fun" or "party"
                                    or "dance" or "seasons" or "animal" or "school" or "nature"
                                    or "adventure" or "love" or "";

            var outdoorBlacklistTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "boiler", "factory", "transmission", "collision", "workshop", "repair",
                "construction", "wood chipper", "turbine", "generator", "compressor",
                "parking", "garage", "stairway", "restroom", "bathroom", "toilet",
                "sewer", "drain", "lockdown", "police", "dispatch", "scanner",
                "theater", "concession", "popcorn", "bowling", "hockey", "ice rink",
                "helicopter", "airplane", "plane", "train yard", "welding",
                "shipping", "stamp", "bottle return", "grocery", "vending",
                "interior", "inside", "indoor",
                "air conditioner", "ceiling fan", "platform fans", "appliance",
                "car drive", "car interior", "car parked", "car wash", "car exterior",
                "driving", "van driving", "road after rain from van",
                "machine", "equipment", "electrical", "lockdown",
            };

            var queryTags = soundType.ToLower()
                .Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2)
                .ToList();

            var synonymMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["bird"] = new[] { "animal", "chirp", "goldfinch", "cowbird", "vireo", "tanager", "pewee", "stilt" },
                ["chirp"] = new[] { "bird", "animal", "goldfinch", "chirp" },
                ["animal"] = new[] { "ambience", "bird", "chirp", "bark", "frog" },

                ["creek"] = new[] { "ambience", "stream", "bridge", "woods", "water" },
                ["stream"] = new[] { "ambience", "creek", "water", "river", "flowing" },
                ["water"] = new[] { "liquid", "creek", "stream", "river", "ambience" },
                ["river"] = new[] { "liquid", "water", "flowing", "creek" },
                ["ocean"] = new[] { "ambience", "shore", "sea", "waves" },
                ["shore"] = new[] { "ambience", "ocean", "sea" },
                ["splash"] = new[] { "liquid", "water", "splashing", "hands" },
                ["liquid"] = new[] { "water", "splash", "pour", "creek" },

                ["children"] = new[] { "ambience", "group", "playground", "distant", "sprinkler" },
                ["playground"] = new[] { "ambience", "children", "group", "distant" },
                ["group"] = new[] { "ambience", "children", "playground" },
                ["sprinkler"] = new[] { "ambience", "children", "sprinkler" },

                ["nature"] = new[] { "ambience", "trail", "field", "windy", "wilderness", "swamp" },
                ["trail"] = new[] { "ambience", "nature", "trail" },
                ["wilderness"] = new[] { "ambience", "wilderness", "nature" },
                ["forest"] = new[] { "ambience", "nature", "trail", "woods", "creek" },
                ["woods"] = new[] { "ambience", "creek", "dirt", "road", "nature" },
                ["dirt"] = new[] { "ambience", "road", "woods", "dirt" },

                ["backyard"] = new[] { "ambience", "backyard", "road" },
                ["park"] = new[] { "ambience", "backyard", "children", "group" },
                ["garden"] = new[] { "ambience", "backyard", "nature" },

                ["downtown"] = new[] { "ambience", "downtown", "area", "city" },
                ["city"] = new[] { "ambience", "downtown", "road", "traffic" },
                ["urban"] = new[] { "ambience", "downtown", "traffic" },

                ["rain"] = new[] { "weather", "ambience", "drips", "drizzle", "light" },
                ["drizzle"] = new[] { "weather", "ambience", "rain", "drips" },
                ["drips"] = new[] { "weather", "ambience", "rain" },

                ["thunder"] = new[] { "weather", "ambience", "thunderstorm", "heavy", "rain" },
                ["storm"] = new[] { "weather", "ambience", "heavy", "rain", "thunder" },
                ["thunderstorm"] = new[] { "weather", "ambience", "thunder", "heavy" },

                ["wind"] = new[] { "weather", "ambience", "hurricane", "windy", "field" },
                ["windy"] = new[] { "ambience", "nature", "field", "windy" },
                ["breeze"] = new[] { "ambience", "nature", "field", "windy" },
                ["hurricane"] = new[] { "weather", "ambience", "hurricane", "wind", "gusts" },

                ["snow"] = new[] { "weather", "snow", "boots", "footstep", "jumping" },
                ["winter"] = new[] { "weather", "snow", "boots", "blizzard" },
                ["boots"] = new[] { "weather", "snow", "boots", "jumping" },
                ["blizzard"] = new[] { "weather", "ambience", "wind", "blizzard", "snow" },
                ["jumping"] = new[] { "weather", "snow", "boots", "jumping" },
                ["footstep"] = new[] { "weather", "snow", "footstep", "single" },

                ["night"] = new[] { "ambience", "night", "crickets", "bullfrog" },
                ["crickets"] = new[] { "ambience", "night", "crickets", "animal" },
                ["bullfrog"] = new[] { "ambience", "night", "bullfrog" },

                ["cricket"] = new[] { "animal", "ambience", "crickets", "stream" },
                ["insects"] = new[] { "animal", "ambience", "crickets", "jungle" },
                ["summer"] = new[] { "animal", "ambience", "crickets" },

                ["jungle"] = new[] { "animal", "ambience", "jungle", "rain", "forest", "insects" },
                ["tropical"] = new[] { "animal", "ambience", "jungle", "insects" },

                ["dog"] = new[] { "animal", "dog", "bark", "labrador", "maltese" },
                ["bark"] = new[] { "animal", "dog", "bark" },
                ["puppy"] = new[] { "animal", "dog", "bark" },

                ["horse"] = new[] { "animal", "horse", "canters", "gravel", "trots" },
                ["canters"] = new[] { "animal", "horse", "canters" },
                ["hooves"] = new[] { "animal", "horse", "canters", "trot" },

                ["cat"] = new[] { "animal", "mammal", "carnivore", "domestic", "meow" },
                ["meow"] = new[] { "animal", "mammal", "cat", "domestic", "meow" },
                ["domestic"] = new[] { "animal", "mammal", "cat", "domestic" },

                ["frog"] = new[] { "animal", "frog", "chirp", "california", "tree" },
                ["quack"] = new[] { "animal", "frog", "toad", "spadefoot" },

                ["outdoor"] = new[] { "ambience", "nature", "children", "backyard", "trail" },
                ["outside"] = new[] { "ambience", "nature", "backyard", "outdoor" },
                ["morning"] = new[] { "animal", "bird", "chirp", "ambience", "nature" },
                ["spring"] = new[] { "animal", "bird", "chirp" },
                ["gentle"] = new[] { "ambience", "nature", "trail", "bird" },
                ["quiet"] = new[] { "ambience", "nature", "trail" },
                ["calm"] = new[] { "ambience", "nature", "trail", "bird" },
            };

            var expandedTags = new HashSet<string>(queryTags, StringComparer.OrdinalIgnoreCase);
            foreach (string tag in queryTags.ToList())
            {
                if (synonymMap.TryGetValue(tag, out string[] syns))
                    foreach (string s in syns) expandedTags.Add(s);
            }

            Dictionary<string, List<string>> filteredIndex = _soundIndex;
            if (isOutdoorContext && outdoorBlacklistTags.Count > 0)
            {
                filteredIndex = new Dictionary<string, List<string>>(_soundIndex.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (file, tags) in _soundIndex)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
                    bool isBlacklisted = outdoorBlacklistTags.Any(bt =>
                        fileName.Contains(bt.ToLower()));
                    if (!isBlacklisted)
                        filteredIndex[file] = tags;
                }
                LogToMainWindow($"🔊 Outdoor filter aktivan: {filteredIndex.Count}/{_soundIndex.Count} fajlova u pretrazi");
            }

            string found = FindBestMatch(filteredIndex, expandedTags);

            if (found == null && filteredIndex.Count < _soundIndex.Count)
            {
                LogToMainWindow($"⚠️ Blacklist filter nije dao rezultat — pokušavam full indeks");
                found = FindBestMatch(_soundIndex, expandedTags);
            }

            if (found != null)
            {
                string ext = Path.GetExtension(found);
                string outputPath = Path.Combine(tempDir, $"ambient_{Guid.NewGuid().ToString().Substring(0, 8)}{ext}");
                await Task.Run(() => File.Copy(found, outputPath, true));
                LogToMainWindow($"🔊 Lokalni zvuk: '{Path.GetFileName(found)}' za '{soundType}'");
                return outputPath;
            }

            LogToMainWindow($"🔊 Nema lokalnog zvuka za '{soundType}' u Assets/Sounds/");
            return null;
        }

        private async Task<string> GetLocalSFX(string sfxType, string tempDir)
        {
            string sfxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "SFX");

            lock (_soundIndexLock)
            {
                if (_sfxIndex == null)
                {
                    _sfxIndex = BuildSoundIndex(sfxDir);
                    if (_sfxIndex.Count > 0)
                        LogToMainWindow($"🎵 SFX indeks: {_sfxIndex.Count} fajlova u Assets/SFX/");
                }
            }

            if (_sfxIndex == null || _sfxIndex.Count == 0) return null;

            var queryTags = sfxType.ToLower()
                .Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1)
                .ToList();

            string found = FindBestMatch(_sfxIndex, queryTags);
            if (found == null) return null;

            string ext = Path.GetExtension(found);
            string outputPath = Path.Combine(tempDir, $"sfx_{Guid.NewGuid().ToString().Substring(0, 8)}{ext}");
            await Task.Run(() => File.Copy(found, outputPath, true));
            LogToMainWindow($"🎵 SFX: '{Path.GetFileName(found)}' za '{sfxType}'");
            return outputPath;
        }

        public static string GetSoundLibraryReport()
        {
            var sb = new System.Text.StringBuilder();
            string soundsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds");
            string sfxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "SFX");

            var allDirs = new[] { ("Sounds", soundsDir), ("SFX", sfxDir) };
            foreach (var (label, dir) in allDirs)
            {
                if (!Directory.Exists(dir)) continue;
                var idx = BuildSoundIndex(dir);
                sb.AppendLine($"\n═══ {label} ({idx.Count} fajlova) ═══");
                foreach (var (file, tags) in idx.OrderBy(x => Path.GetFileName(x.Key)))
                    sb.AppendLine($"  {Path.GetFileName(file)}\n    Tagovi: {string.Join(", ", tags.Distinct().Take(8))}");
            }
            return sb.ToString();
        }

        private async Task<string> MixAmbientWithMusic(string musicPath, List<StoryScene> scenes, double totalDuration, string tempDir)
        {
            if (!_enableAmbientSounds || scenes.All(s => string.IsNullOrEmpty(s.AmbientPath)))
            {
                LogToMainWindow("🔊 Ambijentalni zvukovi isključeni ili nema zvukova za miksanje");
                return musicPath;
            }

            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) return musicPath;

            double ambientVolume = _detectedContext switch
            {
                "lullaby" => 0.12,
                "sad" => 0.13,
                "love" => 0.15,
                "nature" => 0.18,
                "outdoor" => 0.18,
                "health" => 0.18,
                "adventure" => 0.20,
                "christmas" => 0.20,
                "party" => 0.25,
                "dance" => 0.25,
                "fun" => 0.20,
                _ => 0.18
            };

            LogToMainWindow($"🔊 Miksiram ambijentalne zvukove: {scenes.Count(s => !string.IsNullOrEmpty(s.AmbientPath))} zvukova, gain={ambientVolume:F2}");

            var filterParts = new List<string>();
            var inputs = new List<string>();

            inputs.Add($"-i \"{musicPath}\"");
            filterParts.Add($"[0:a]volume=1.0[a0]");

            const double crossfadeDuration = 1.0;
            int ambientIndex = 1;

            for (int i = 0; i < scenes.Count; i++)
            {
                if (!string.IsNullOrEmpty(scenes[i].AmbientPath) && File.Exists(scenes[i].AmbientPath))
                {
                    inputs.Add($"-i \"{scenes[i].AmbientPath}\"");
                    double startTime = _timelineCursorOffset + scenes[i].StartTime;
                    double duration = scenes[i].Duration;
                    string startMs = ((long)(startTime * 1000)).ToString();
                    double endTime = startTime + duration;

                    bool hasPrevAmbient = i > 0 && !string.IsNullOrEmpty(scenes[i - 1].AmbientPath);
                    double fadeInDuration = hasPrevAmbient ? crossfadeDuration : 0.3;
                    double fadeOutDuration = 0.5;

                    filterParts.Add(
                        $"[{ambientIndex}:a]" +
                        $"aloop=loop=-1:size=2e+09," +
                        $"adelay={startMs}|{startMs}," +
                        $"atrim=end={endTime.ToString("F3", CultureInfo.InvariantCulture)}," +
                        $"afade=t=in:st={startTime.ToString("F3", CultureInfo.InvariantCulture)}:d={fadeInDuration.ToString("F2", CultureInfo.InvariantCulture)}," +
                        $"afade=t=out:st={(endTime - fadeOutDuration).ToString("F3", CultureInfo.InvariantCulture)}:d={fadeOutDuration.ToString("F2", CultureInfo.InvariantCulture)}," +
                        $"volume={ambientVolume.ToString("F2", CultureInfo.InvariantCulture)}[a{ambientIndex}]");

                    ambientIndex++;
                    LogToMainWindow($"🔊 Scena {i + 1}: ambient start={startTime:F1}s dur={duration:F1}s");
                }
            }

            if (ambientIndex == 1) return musicPath;

            string allInputs = string.Join(" ", inputs);
            string filterGraph = string.Join(";", filterParts);

            var mixInputs = new List<string>();
            mixInputs.Add("[a0]");
            for (int i = 1; i < ambientIndex; i++)
                mixInputs.Add($"[a{i}]");

            string mixFilter = string.Join("", mixInputs);
            string outputPath = Path.Combine(tempDir, $"mixed_audio_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");

            string args = $"-nostdin {allInputs} -filter_complex \"{filterGraph};{mixFilter}amix=inputs={mixInputs.Count}:duration=first:normalize=0\" -t {totalDuration.ToString(CultureInfo.InvariantCulture)} -y \"{outputPath}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                LogToMainWindow("⚠️ Miksanje zvuka prekoračilo timeout (120s) - koristim originalnu muziku");
                try { process.Kill(); } catch { }
                return musicPath;
            }

            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                LogToMainWindow($"✅ Ambijentalni zvukovi uspješno miksnani (gain={ambientVolume:F2}, crossfade=1s)");
                return outputPath;
            }

            return musicPath;
        }

        #endregion

        #region Transition Sounds

        private async Task<string> GetTransitionSound(string tempDir, string type = "pop")
        {
            if (!_enableTransitionSounds) return null;

            if (_transitionSoundCache.TryGetValue(type, out string cachedSource) && File.Exists(cachedSource))
            {
                string copyPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");
                File.Copy(cachedSource, copyPath, true);
                return copyPath;
            }

            string soundFile = type == "pop" ? "transition-pop.mp3" : "transition-whoosh.mp3";
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds", soundFile);
            if (File.Exists(localPath))
            {
                string outputPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");
                File.Copy(localPath, outputPath, true);
                _transitionSoundCache[type] = outputPath;
                return outputPath;
            }

            try
            {
                string genPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.wav");
                bool ok = type == "pop"
                    ? GeneratePopSound(genPath)
                    : GenerateWhooshSound(genPath);

                if (ok && File.Exists(genPath))
                {
                    _transitionSoundCache[type] = genPath;
                    LogToMainWindow($"🔊 Tranzicioni zvuk generisan: {type}");
                    string copyPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.wav");
                    File.Copy(genPath, copyPath, true);
                    return copyPath;
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow($"⚠️ Greška pri generisanju zvuka: {ex.Message}");
            }

            return null;
        }

        private bool GeneratePopSound(string outputPath)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            double totalSeconds = 0.18;
            int totalSamples = (int)(sampleRate * totalSeconds);

            var samples = new float[totalSamples * channels];
            var rng = new Random(42);

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double tNorm = t / totalSeconds;

                double envelope;
                if (t < 0.005)
                    envelope = t / 0.005;
                else
                    envelope = Math.Exp(-18.0 * (t - 0.005));

                double fundamental = Math.Sin(2 * Math.PI * 80 * t) * 0.6;
                double harmonic = Math.Sin(2 * Math.PI * 160 * t) * 0.25;
                double click = (rng.NextDouble() * 2 - 1) * Math.Exp(-120 * t) * 0.3;

                double signal = (fundamental + harmonic + click) * envelope * 0.15;

                float sample = (float)Math.Clamp(signal, -1.0, 1.0);
                samples[i * channels] = sample;
                samples[i * channels + 1] = sample;
            }

            return WriteWav(outputPath, samples, sampleRate, channels);
        }

        private bool GenerateWhooshSound(string outputPath)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            double totalSeconds = 0.45;
            int totalSamples = (int)(sampleRate * totalSeconds);

            var samples = new float[totalSamples * channels];
            var rng = new Random(42);

            double filterState = 0;
            const double filterCoeff = 0.12;

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double tNorm = t / totalSeconds;

                double envelope;
                if (tNorm < 0.18)
                    envelope = tNorm / 0.18;
                else if (tNorm < 0.60)
                    envelope = 1.0;
                else
                    envelope = 1.0 - (tNorm - 0.60) / 0.40;

                double noise = rng.NextDouble() * 2 - 1;
                filterState = filterState * (1 - filterCoeff) + noise * filterCoeff;

                double signal = filterState * envelope * 0.12;

                float sample = (float)Math.Clamp(signal, -1.0, 1.0);
                samples[i * channels] = sample;
                samples[i * channels + 1] = sample;
            }

            return WriteWav(outputPath, samples, sampleRate, channels);
        }

        private bool WriteWav(string path, float[] samples, int sampleRate, int channels)
        {
            try
            {
                using var writer = new NAudio.Wave.WaveFileWriter(
                    path,
                    NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
                writer.WriteSamples(samples, 0, samples.Length);
                return true;
            }
            catch (Exception ex)
            {
                LogToMainWindow($"⚠️ WriteWav greška: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Video Processing sa B-roll za instrumentalne dijelove

        private async Task ProcessVideoCreation(string audioPath, double totalDuration)
        {
            _audioPath = audioPath;
            _totalDuration = totalDuration;

            btnGenerate.Content = "🎬 AI kreira priču...";
            AnnounceToUser(L("ai_analyzing_song_story"), 5);

            var storyBoard = await GenerateStoryBoard(_lyricLines, _cts?.Token ?? CancellationToken.None);

            if (storyBoard?.Scenes == null || storyBoard.Scenes.Count == 0)
            {
                LogToMainWindow("❌ Story board nije generisan, koristim fallback");
                storyBoard = CreateFallbackStoryBoard();
            }

            LogToMainWindow($"📖 Priča kreirana: {storyBoard.Scenes.Count} scena");
            LogToMainWindow($"👤 Glavni lik: {storyBoard.MainCharacter}");
            LogToMainWindow($"🎬 Tema: {storyBoard.OverallTheme}");

            string ffmpegForBeat = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            _beatInfo = await BeatDetection.AnalyzeAudio(audioPath, ffmpegForBeat, _cts?.Token ?? CancellationToken.None);
            if (_beatInfo.IsValid)
                LogToMainWindow($"🥁 Beat detection: {_beatInfo.BPM:F0} BPM, {_beatInfo.BeatTimes.Count} udaraca, confidence={_beatInfo.Confidence:F2}");
            else
                LogToMainWindow("🥁 Beat detection: nije pronađen ritam, koristim Whisper trajanja");

            bool visionOk = await VisionAnalyzer.InitializeAsync(LogToMainWindow, _cts?.Token ?? CancellationToken.None);
            LogToMainWindow(visionOk ? "🧠 VisionAnalyzer: ONNX aktivan" : "🧠 VisionAnalyzer: FFmpeg mod");

            MotionAnalyzer.ClearCache();
            _lastClipMotion = null;

            _tempVideoFolder = Path.Combine(Path.GetTempPath(), $"UVE_Story_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempVideoFolder);

            _segments = new List<TimelineSegment>();

            _usedMediaUrls.Clear();
            _queryUseCount.Clear();

            int sceneCount = storyBoard.Scenes.Count;
            double estimatedSegDur = sceneCount > 0
                ? Math.Round(totalDuration / sceneCount, 2)
                : 0;

            double MAX_LYRIC_SCENE_DURATION;
            if (_beatInfo != null && _beatInfo.IsValid)
            {
                if (_beatInfo.BPM > 120) MAX_LYRIC_SCENE_DURATION = 6.0;
                else if (_beatInfo.BPM > 80) MAX_LYRIC_SCENE_DURATION = 9.0;
                else MAX_LYRIC_SCENE_DURATION = 12.0;
                LogToMainWindow($"🥁 BPM {_beatInfo.BPM:F0} → max trajanje scene: {MAX_LYRIC_SCENE_DURATION}s");
            }
            else
            {
                MAX_LYRIC_SCENE_DURATION = _detectedContext switch
                {
                    "children" or "lullaby" or "fun" or "party" => 9.0,
                    "wedding" or "love" or "romantic" => 12.0,
                    "adventure" or "sport" or "action" => 6.0,
                    "sad" or "melancholy" or "documentary" => 15.0,
                    _ => 9.0
                };
                LogToMainWindow($"🎬 Context '{_detectedContext}' → max trajanje scene: {MAX_LYRIC_SCENE_DURATION}s");
            }

            for (int si = 0; si < storyBoard.Scenes.Count; si++)
            {
                var sc = storyBoard.Scenes[si];
                if (_lyricTimestamps.Count > 0 && _lyricTimestamps.ContainsKey(si))
                {
                    sc.StartTime = _lyricTimestamps[si];
                    double nxt = _lyricTimestamps.ContainsKey(si + 1)
                        ? _lyricTimestamps[si + 1]
                        : sc.StartTime + MAX_LYRIC_SCENE_DURATION;
                    double rawDur = Math.Max(1.0, Math.Round(nxt - sc.StartTime, 2));
                    sc.Duration = Math.Min(MAX_LYRIC_SCENE_DURATION, rawDur);

                    if (_beatInfo != null && _beatInfo.IsValid && _beatInfo.BeatTimes?.Count > 4)
                    {
                        double snapRadius = 0.150;
                        double nearest = _beatInfo.BeatTimes
                            .OrderBy(b => Math.Abs(b - sc.StartTime))
                            .FirstOrDefault();
                        double diff = Math.Abs(nearest - sc.StartTime);
                        if (diff > 0.02 && diff <= snapRadius)
                        {
                            sc.StartTime = Math.Round(nearest, 3);
                            if (_lyricTimestamps.ContainsKey(si + 1))
                            {
                                double maxDur = _lyricTimestamps[si + 1] - sc.StartTime;
                                sc.Duration = Math.Min(sc.Duration, Math.Max(1.0, maxDur));
                            }
                        }
                    }
                }
                else
                {
                    sc.StartTime = Math.Round(si * estimatedSegDur, 2);
                    sc.Duration = (si == storyBoard.Scenes.Count - 1)
                        ? Math.Min(MAX_LYRIC_SCENE_DURATION,
                            Math.Max(1.0, Math.Round(totalDuration - sc.StartTime, 2)))
                        : Math.Round(estimatedSegDur, 2);
                }
            }

            double actualLyricsEnd;
            if (_lyricTimestamps.Count > 0 && storyBoard.Scenes.Count > 0)
            {
                var lastScene = storyBoard.Scenes[storyBoard.Scenes.Count - 1];
                int lastIdx = storyBoard.Scenes.Count - 1;
                if (_lyricTimestamps.ContainsKey(lastIdx))
                {
                    double lastStart = _lyricTimestamps[lastIdx];
                    double lastEnd = _lyricTimestamps.ContainsKey(lastIdx + 1)
                        ? _lyricTimestamps[lastIdx + 1]
                        : lastScene.StartTime + lastScene.Duration;
                    actualLyricsEnd = lastEnd;
                }
                else
                {
                    actualLyricsEnd = lastScene.StartTime + lastScene.Duration;
                }
            }
            else
            {
                double cappedSegDur = Math.Min(estimatedSegDur, MAX_LYRIC_SCENE_DURATION);
                actualLyricsEnd = cappedSegDur * sceneCount;
            }

            double lyricsTotalDuration = Math.Round(actualLyricsEnd, 2);
            double remainingDuration = Math.Round(totalDuration - lyricsTotalDuration, 2);

            LogToMainWindow($"📊 Trajanje audio: {FormatTime(totalDuration)} | Stihovi: {FormatTime(lyricsTotalDuration)} | Instrumentalni rep: {FormatTime(Math.Max(0, remainingDuration))}");

            if (remainingDuration > 1.5)
            {
                LogToMainWindow($"🎵 Detektovan instrumentalni dio: {FormatTime(remainingDuration)} (ukupno trajanje: {FormatTime(totalDuration)}, stihovi: {FormatTime(lyricsTotalDuration)})");

                List<string> bRollKeywords = new List<string>();

                switch (_detectedContext)
                {
                    case "lullaby":
                        bRollKeywords.AddRange(new[] {
                            "peaceful sleeping baby soft light cozy bedroom",
                            "moonlight night stars quiet gentle atmosphere",
                            "soft clouds floating gentle time lapse sky",
                            "slow motion nature peaceful waterfall calm",
                            "baby crib mobile turning soft light",
                            "mother kissing baby forehead goodnight warm",
                            "starry night sky twinkling stars peaceful"
                        });
                        break;
                    case "party":
                        bRollKeywords.AddRange(new[] {
                            "colorful abstract shapes celebration joyful background",
                            "happy kids dancing colorful clothes energetic",
                            "confetti colorful festive celebration falling",
                            "celebration lights bokeh happy joyful",
                            "birthday cake candles children excited cheering",
                            "balloons floating up colorful celebration",
                            "children jumping happy celebration fun"
                        });
                        break;
                    case "sad":
                        bRollKeywords.AddRange(new[] {
                            "rain on window melancholy quiet indoor",
                            "autumn leaves falling slow motion lonely",
                            "empty park bench cloudy day grey",
                            "gentle water ripples calm peaceful reflective",
                            "person sitting alone window thinking",
                            "foggy morning misty forest quiet",
                            "candle flickering alone dark room"
                        });
                        break;
                    case "adventure":
                        bRollKeywords.AddRange(new[] {
                            "children exploring forest adventure excited",
                            "nature landscape cinematic mountains sunrise",
                            "adventure travel scenic journey hiking",
                            "mountains clouds time lapse dramatic",
                            "kids running through field grass happy",
                            "forest path sunlight dappled exploring",
                            "river crossing bridge adventure outdoors"
                        });
                        break;
                    case "dance":
                        bRollKeywords.AddRange(new[] {
                            "colorful abstract dance background movement",
                            "children dancing joyful colorful clothes spinning",
                            "children dancing party colorful balloons happy",
                            "happy kids dancing background energetic",
                            "dance floor colorful lights movement",
                            "spinning colorful skirts dancing joyful",
                            "children group dance happy celebration"
                        });
                        break;
                    case "christmas":
                        bRollKeywords.AddRange(new[] {
                            "christmas lights cozy background warm",
                            "snow falling slow motion winter wonderland",
                            "fireplace crackling warm glow cozy indoor",
                            "holiday decorations magical sparkle",
                            "children opening christmas gifts excited happy",
                            "snowflakes gently falling winter landscape",
                            "christmas tree ornaments twinkling lights"
                        });
                        break;
                    case "nature":
                        bRollKeywords.AddRange(new[] {
                            "nature landscape cinematic beautiful mountains",
                            "flowers blooming time lapse colorful garden",
                            "sunset clouds peaceful orange sky",
                            "forest river flowing gentle water nature",
                            "butterflies flying flowers meadow spring",
                            "waterfall cascade nature peaceful green",
                            "meadow flowers wind gentle breeze"
                        });
                        break;
                    case "outdoor":
                    case "health":
                        bRollKeywords.AddRange(new[] {
                            "children walking park path sunny morning cinematic",
                            "birds singing trees nature spring peaceful bokeh",
                            "stream flowing forest nature gentle peaceful",
                            "family walking together park golden hour warm",
                            "child running grass meadow joyful slow motion",
                            "flowers blooming spring garden colorful nature",
                            "sunrise morning park mist golden light cinematic",
                            "park bench children playing background sunny day",
                            "autumn leaves park path children walking colorful",
                            "winter children snow playing cozy warm colors",
                        });
                        break;
                    case "seasons":
                        bRollKeywords.AddRange(new[] {
                            "spring flowers blooming children garden colorful",
                            "summer beach children playing sunshine warm",
                            "autumn leaves falling park path golden colors",
                            "winter snow children playing cozy warm bokeh",
                            "rain puddles children boots splashing happy",
                            "children park golden hour sunset warm light",
                            "morning dew nature close-up cinematic beautiful",
                            "rainbow sky colorful nature children wonder",
                        });
                        break;
                    case "music":
                        bRollKeywords.AddRange(new[] {
                            "children singing choir joyful colorful",
                            "child playing piano happy lesson",
                            "kids dancing music classroom joyful",
                            "children instruments school band music",
                            "child headphones bedroom happy music listening",
                            "children singing together choir joyful classroom",
                            "girl boy guitar playing happy smiling",
                            "children clapping hands singing together fun",
                            "kids music class teacher colorful instruments",
                            "child microphone singing stage joyful performance"
                        });
                        break;
                    case "school":
                        bRollKeywords.AddRange(new[] {
                            "children classroom learning happy school",
                            "school desk pencil drawing children art",
                            "playground children running school recess",
                            "books colorful backpack school education",
                            "teacher children reading story classroom"
                        });
                        break;
                    case "animal":
                        bRollKeywords.AddRange(new[] {
                            "cute animals farm children petting",
                            "puppy dog playing children happy outdoor",
                            "baby animals cute nature soft",
                            "children zoo animals watching excited",
                            "kitten cat playing children home"
                        });
                        break;
                    default:
                        if (!string.IsNullOrEmpty(storyBoard?.OverallTheme))
                        {
                            string themeLower = storyBoard.OverallTheme.ToLower();
                            if (themeLower.Contains("muzik") || themeLower.Contains("pesm"))
                                bRollKeywords.AddRange(new[] { "music concert children stage", "musical notes colorful animation", "children singing together group" });
                            else if (themeLower.Contains("prirod") || themeLower.Contains("šum"))
                                bRollKeywords.AddRange(new[] { "nature outdoor children exploring", "forest path sunlight children", "meadow flowers wind children" });
                            else if (themeLower.Contains("porodic") || themeLower.Contains("ljubav"))
                                bRollKeywords.AddRange(new[] { "family together outdoor happy warm", "parents children hugging love", "home family cozy warm happy" });
                        }
                        if (bRollKeywords.Count == 0)
                            bRollKeywords.AddRange(new[] {
                                "children playing park nature sunny day cinematic",
                                "birds chirping morning trees nature peaceful",
                                "stream brook water nature sounds peaceful outdoor",
                                "children laughing running park sunny slow motion",
                                "nature landscape beautiful trees sky cinematic",
                                "flowers garden colorful spring bokeh nature",
                                "family outdoor together park warm golden light",
                                "child wonder exploring nature curiosity bokeh",
                            });
                        break;
                }

                double bRollDurationPerClip = 4.0;
                int bRollCount = Math.Max(1, (int)Math.Ceiling(remainingDuration / bRollDurationPerClip));
                double actualDurationPerClip = remainingDuration / bRollCount;

                LogToMainWindow($"🎬 Kreiranje {bRollCount} B-roll kadrova za instrumentalni dio (po {actualDurationPerClip:F1}s)");

                double introPortion = Math.Min(remainingDuration * 0.4, 10.0);
                int introClips = Math.Max(1, (int)Math.Ceiling(introPortion / actualDurationPerClip));
                double introClipDuration = introPortion / introClips;

                double bRollStartTime = 0;
                string bRollMediaType = (cmbMediaType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "video";

                var introSegments = new List<TimelineSegment>();
                for (int i = 0; i < introClips; i++)
                {
                    string kw = bRollKeywords[i % bRollKeywords.Count];
                    string styleEnhance = _detectedContext switch
                    {
                        "lullaby" => "soft light peaceful calm",
                        "party" => "colorful joyful energetic",
                        "sad" => "gentle melancholy quiet",
                        "adventure" => "exciting dynamic outdoor",
                        "dance" => "rhythmic colorful movement",
                        "christmas" => "warm cozy magical",
                        "nature" => "peaceful natural soft",
                        _ => "warm colors happy cheerful"
                    };
                    string enhancedQuery = $"{kw} {styleEnhance}";

                    AnnounceToUser(LF("b_roll_intro", i + 1, introClips), 5);
                    string bMediaPath = await SearchAndDownloadMedia(enhancedQuery, 1080, bRollMediaType, _cts?.Token ?? CancellationToken.None);

                    if (string.IsNullOrEmpty(bMediaPath))
                    {
                        string altKw = bRollKeywords[(i + 1) % bRollKeywords.Count];
                        bMediaPath = await SearchAndDownloadMedia(altKw, 1080, bRollMediaType, _cts?.Token ?? CancellationToken.None);
                    }

                    if (!string.IsNullOrEmpty(bMediaPath) && bRollMediaType == "video")
                    {
                        double bVidDur = await GetVideoDuration(bMediaPath);
                        if (Math.Abs(bVidDur - introClipDuration) > 0.5)
                            bMediaPath = await TrimVideoToDuration(bMediaPath, introClipDuration, _tempVideoFolder);
                    }

                    introSegments.Add(new TimelineSegment
                    {
                        Path = bMediaPath ?? "",
                        Duration = introClipDuration,
                        StartTime = bRollStartTime + i * introClipDuration,
                        Description = i == 0 ? "🎵 Uvod - instrumental" : "🎵 Uvod - nastavak",
                        LyricText = "",
                        AmbientSoundPath = null,
                        Energy = 2,
                        Emotion = _detectedContext == "lullaby" ? "calm" : "happy"
                    });
                    LogToMainWindow($"   B-roll uvod {i + 1}: {introClipDuration:F1}s - '{kw}' → {(string.IsNullOrEmpty(bMediaPath) ? "❌ nema medija" : "✅ OK")}");
                }

                for (int i = introSegments.Count - 1; i >= 0; i--)
                    _segments.Insert(0, introSegments[i]);

                double firstLyricTimestamp = _lyricTimestamps.Count > 0
                    ? _lyricTimestamps[0]
                    : 0.0;
                double shift = introPortion - firstLyricTimestamp;
                if (shift < 0) shift = 0;

                foreach (var scene in storyBoard.Scenes)
                    scene.StartTime += shift;

                double outroPortion = remainingDuration - introPortion;
                if (outroPortion > 0.5)
                {
                    int outroClips = Math.Max(1, (int)Math.Ceiling(outroPortion / actualDurationPerClip));
                    double outroClipDuration = outroPortion / outroClips;
                    double outroStartTime = totalDuration - outroPortion;

                    for (int i = 0; i < outroClips; i++)
                    {
                        string kw = bRollKeywords[(introClips + i) % bRollKeywords.Count];
                        string styleEnhance = _detectedContext switch
                        {
                            "lullaby" => "soft light peaceful calm ending",
                            "party" => "colorful joyful fading out",
                            "sad" => "gentle melancholy quiet fade",
                            "adventure" => "exciting dynamic conclusion",
                            "dance" => "rhythmic colorful fade",
                            "christmas" => "warm cozy magical ending",
                            "nature" => "peaceful natural soft conclusion",
                            _ => "warm colors happy cheerful ending"
                        };
                        string enhancedQuery = $"{kw} {styleEnhance}";

                        AnnounceToUser(LF("b_roll_outro", i + 1, outroClips), 92);
                        string bMediaPath = await SearchAndDownloadMedia(enhancedQuery, 1080, bRollMediaType, _cts?.Token ?? CancellationToken.None);

                        if (string.IsNullOrEmpty(bMediaPath))
                        {
                            string altKw = bRollKeywords[(introClips + i + 1) % bRollKeywords.Count];
                            bMediaPath = await SearchAndDownloadMedia(altKw, 1080, bRollMediaType, _cts?.Token ?? CancellationToken.None);
                        }

                        if (!string.IsNullOrEmpty(bMediaPath) && bRollMediaType == "video")
                        {
                            double bVidDur = await GetVideoDuration(bMediaPath);
                            if (Math.Abs(bVidDur - outroClipDuration) > 0.5)
                                bMediaPath = await TrimVideoToDuration(bMediaPath, outroClipDuration, _tempVideoFolder);
                        }

                        _segments.Add(new TimelineSegment
                        {
                            Path = bMediaPath ?? "",
                            Duration = outroClipDuration,
                            StartTime = outroStartTime + (i * outroClipDuration),
                            Description = i == outroClips - 1 ? "🎵 Outro - kraj" : "🎵 Outro - instrumental",
                            LyricText = "",
                            AmbientSoundPath = null,
                            Energy = 2,
                            Emotion = _detectedContext == "lullaby" ? "calm" : "happy"
                        });
                        LogToMainWindow($"   B-roll outro {i + 1}: {outroClipDuration:F1}s - '{kw}' → {(string.IsNullOrEmpty(bMediaPath) ? "❌ nema medija" : "✅ OK")}");
                    }
                }

                totalDuration = introPortion + lyricsTotalDuration + outroPortion;
                LogToMainWindow($"📊 Nakon dodavanja B-roll: uvod={introPortion:F1}s, stihovi={lyricsTotalDuration:F1}s, outro={outroPortion:F1}s, UKUPNO={totalDuration:F1}s");
            }

            try
            {
                string mediaType = (cmbMediaType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "video";
                int count = storyBoard.Scenes.Count;

                for (int i = 0; i < count; i++)
                {
                    _cts?.Token.ThrowIfCancellationRequested();
                    var scene = storyBoard.Scenes[i];

                    string styleConsistency = _detectedContext switch
                    {
                        "lullaby" => "soft light cozy warm peaceful night children",
                        "party" => "colorful bright happy festive vibrant children",
                        "love" => "warm golden romantic soft glow family children",
                        "sad" => "grey moody soft desaturated melancholy",
                        "adventure" => "outdoor bright natural energetic children",
                        "dance" => "colorful joyful movement bright children",
                        "christmas" => "warm holiday lights cozy festive children",
                        "animal" => "cute nature soft light warm children",
                        "school" => "bright colorful educational warm children",
                        "nature" => "natural sunlight green peaceful children",
                        "music" => "children joyful colorful bright fun",
                        "outdoor" => "children park playground sunny green happy",
                        "health" => "children running active park outdoor healthy sunny",
                        _ => "children warm colors happy atmosphere soft light"
                    };
                    string energyBoost = scene.Energy >= 4 ? " action fast dynamic exciting" : scene.Energy <= 2 ? " calm peaceful slow gentle" : "";
                    string emotionBoost = scene.Emotion;

                    string primaryQuery = BuildLiteralSearchQuery(scene.Keywords, scene.Action, energyBoost, styleConsistency);
                    string fallbackQuery = BuildLiteralSearchQuery(scene.Keywords, "", "", "");

                    string faceStyleQuery = _detectedContext switch
                    {
                        "outdoor" or "health" => "child face smiling laughing outdoor park happy",
                        "music" => "child face singing smiling joyful happy",
                        "dance" => "child face dancing smiling happy joyful",
                        _ => "child face smiling happy closeup"
                    };
                    string fallback2Query = (i % 3 == 2)
                        ? BuildLiteralSearchQuery(faceStyleQuery, "", "", styleConsistency)
                        : BuildLiteralSearchQuery("", scene.Action, "", styleConsistency);

                    LogToMainWindow($"🎬 Scena {i + 1}: Energy={scene.Energy}, Action={scene.Action}, Duration={scene.Duration:F1}s");
                    LogToMainWindow($"   📝 Stih: '{(scene.Description.Length > 50 ? scene.Description.Substring(0, 50) + "..." : scene.Description)}'");
                    LogToMainWindow($"   🔑 Keywords: '{scene.Keywords}'");
                    LogToMainWindow($"   🔍 Pixabay query: '{primaryQuery}'");

                    AnnounceToUser(LF("scene_progress", i + 1, count, scene.Description), 10 + (i * 70 / count));

                    string mediaPath = null;

                    var subSceneQueries = DetectSubSceneQueries(scene.Description, styleConsistency);
                    if (subSceneQueries != null && subSceneQueries.Count >= 2 && mediaType == "video")
                    {
                        LogToMainWindow($"   🎬 Sub-scene detekcija: {subSceneQueries.Count} vizuelnih objekata → montaža klipova");
                        mediaPath = await BuildSubSceneVideo(subSceneQueries, scene.Duration, _tempVideoFolder, _cts?.Token ?? CancellationToken.None);
                        if (mediaPath != null)
                            LogToMainWindow($"   ✅ Sub-scene video kreiran: {subSceneQueries.Count} klipa");
                        else
                            LogToMainWindow($"   ⚠ Sub-scene video nije uspio, koristim standardni path");
                    }

                    double effectiveDuration = Math.Min(scene.Duration, MAX_LYRIC_SCENE_DURATION);
                    if (effectiveDuration != scene.Duration)
                    {
                        LogToMainWindow($"   ⚠️ Scena {i + 1}: Duration {scene.Duration:F1}s → ograničeno na {effectiveDuration:F1}s (MAX_LYRIC_SCENE_DURATION)");
                        scene.Duration = effectiveDuration;
                    }

                    if (mediaType == "video" && scene.Duration > 6.0)
                    {
                        mediaPath = await SearchAndDownloadMultipleMedia(
                            primaryQuery, fallbackQuery, mediaType,
                            scene.Duration, _tempVideoFolder, _cts?.Token ?? CancellationToken.None);
                    }

                    if (string.IsNullOrEmpty(mediaPath))
                        mediaPath = await SearchAndDownloadMedia(primaryQuery, 1080, mediaType, _cts?.Token ?? CancellationToken.None, scene.Duration);

                    if (string.IsNullOrEmpty(mediaPath))
                        mediaPath = await SearchAndDownloadMedia(fallbackQuery, 1080, mediaType, _cts?.Token ?? CancellationToken.None, scene.Duration);

                    if (string.IsNullOrEmpty(mediaPath))
                    {
                        var rng = new Random(i);
                        string fallback = _universalKeywords[rng.Next(_universalKeywords.Count)];
                        mediaPath = await SearchAndDownloadMedia(fallback, 1080, mediaType, _cts?.Token ?? CancellationToken.None, scene.Duration);
                    }

                    if (string.IsNullOrEmpty(mediaPath))
                    {
                        mediaPath = await GenerateMoodGradient(scene.Emotion ?? _detectedMood, scene.Duration, _tempVideoFolder);
                        LogToMainWindow($"   🎨 Fallback: mood gradient za scenu {i + 1}");
                    }

                    if (!string.IsNullOrEmpty(mediaPath))
                    {
                        string finalPath = mediaPath;
                        if (mediaType == "video")
                        {
                            double videoDuration = await GetVideoDuration(mediaPath);
                            LogToMainWindow($"📹 Video trajanje: {videoDuration:F1}s, potrebno: {scene.Duration:F1}s");

                            if (videoDuration > scene.Duration + 0.5)
                            {
                                finalPath = await TrimVideoToDuration(mediaPath, scene.Duration, _tempVideoFolder);
                            }
                            else if (videoDuration < scene.Duration - 0.5)
                            {
                                LogToMainWindow($"   🔁 Kratak video ({videoDuration:F1}s < {scene.Duration:F1}s) — RenderEngine će loopovati");
                                finalPath = mediaPath;
                            }
                        }

                        string ambientPath = await GetAmbientSoundPath(scene.AmbientSound, _tempVideoFolder, _cts?.Token ?? CancellationToken.None);
                        if (ambientPath != null)
                        {
                            scene.AmbientPath = ambientPath;
                            LogToMainWindow($"🔊 Ambijentalni zvuk za scenu {i + 1}: {scene.AmbientSound}");
                        }

                        scene.VisionScore = _lastDownloadedVisionScore;
                        scene.IsStaticClip = _lastDownloadedIsStatic;

                        if (scene.IsStaticClip)
                            LogToMainWindow($"   🎬 Statičan klip — Ken Burns će se primijeniti u renderu");

                        _segments.Add(new TimelineSegment
                        {
                            Path = finalPath,
                            Duration = scene.Duration,
                            StartTime = scene.StartTime,
                            Description = scene.Description,
                            LyricText = _showLyrics ? _lyricLines[i] : "",
                            AmbientSoundPath = ambientPath,
                            Energy = scene.Energy,
                            Emotion = scene.Emotion,
                            MoodTag = $"mood:{scene.Emotion ?? _detectedMood}|context:{_detectedContext}",
                            VisionScore = scene.VisionScore,
                            IsStaticClip = scene.IsStaticClip
                        });
                        LogToMainWindow($"✅ Scena {i + 1}: medij preuzet");
                    }
                    else
                    {
                        LogToMainWindow($"❌ Scena {i + 1}: nema medija - preskačem!");
                    }

                    await Task.Delay(300, _cts?.Token ?? CancellationToken.None);
                }

                if (_segments.Any(s => !string.IsNullOrEmpty(s.AmbientSoundPath)) && _enableAmbientSounds)
                {
                    for (int idx = 0; idx < storyBoard.Scenes.Count && idx < _segments.Count; idx++)
                    {
                        if (!string.IsNullOrEmpty(_segments[idx].AmbientSoundPath))
                            storyBoard.Scenes[idx].AmbientPath = _segments[idx].AmbientSoundPath;
                    }
                    LogToMainWindow("🔊 Ambijentalni zvukovi pripremljeni — miksanje u toku sa video timeline-om...");

                }

                if (_segments.Count >= 8)
                {
                    var lowQuality = _segments
                        .Where(s => s.VisionScore < 5.5 && !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path))
                        .OrderBy(s => s.VisionScore)
                        .Take(Math.Min(2, _segments.Count - 6))
                        .ToList();

                    foreach (var bad in lowQuality)
                    {
                        LogToMainWindow($"   🗑 Quality Gate: uklanjam scenu '{bad.Description}' (Score={bad.VisionScore:F1}/10)");
                        _segments.Remove(bad);
                    }

                    if (lowQuality.Count > 0)
                        LogToMainWindow($"✂️ Quality Gate: uklonjen(o) {lowQuality.Count} slab(ih) kadar(a) — ostalo {_segments.Count} scena");
                }

                AnnounceToUser(L("generation_done_reviewing"), 95);
                LogToMainWindow("═══════════════════════════════════════");
                LogToMainWindow($"📋 PREGLED SCENE — {_segments.Count} scena spremno");
                LogToMainWindow("═══════════════════════════════════════");

                const double DISPLAY_CURSOR = 4.0;
                for (int si = 0; si < _segments.Count; si++)
                {
                    var seg = _segments[si];
                    string hasMedia = string.IsNullOrEmpty(seg.Path) || !File.Exists(seg.Path)
                        ? "❌ NEMA MEDIJA" : "✅";
                    string lyric = !string.IsNullOrEmpty(seg.LyricText) ? $" | Stih: \"{seg.LyricText}\"" : "";
                    double displayStart = DISPLAY_CURSOR + seg.StartTime;
                    LogToMainWindow($"  [{si + 1:D2}] {hasMedia} {FormatTime(displayStart)}-{FormatTime(displayStart + seg.Duration)} " +
                                   $"({seg.Duration:F1}s) — {seg.Description}{lyric}");
                }
                LogToMainWindow("═══════════════════════════════════════");

                int missingCount = _segments.Count(s => string.IsNullOrEmpty(s.Path) || !File.Exists(s.Path));
                if (missingCount > 0)
                    LogToMainWindow($"⚠ {missingCount} scena nema medija — biće preskočene u renderu");
                else
                    LogToMainWindow($"✅ Sve scene imaju medij. Pokrećem render...");

                GenerateValidationReport();

                AnnounceToUser(LF("review_done_creating", _segments.Count), 97);

                await CreateVideo(storyBoard);
            }
            catch (Exception ex) { WpfMessageBox.Show(LF("generic_error", ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { btnGenerate.IsEnabled = true; btnGenerate.Content = "🎬 KREIRAJ VIDEO"; }
        }

        #endregion

        #region TrimVideoToDuration

        private void GenerateValidationReport()
        {
            var segs = _segments.Where(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path)).ToList();
            if (segs.Count == 0) return;

            int totalSegs = segs.Count;
            double avgScore = segs.Average(s => s.VisionScore);
            int staticCount = segs.Count(s => s.IsStaticClip);
            int outdoorCount = segs.Count(s => s.Description != null && (
                s.Description.Contains("park") || s.Description.Contains("outdoor") ||
                s.Description.Contains("nature") || s.Description.Contains("street") ||
                s.Description.Contains("playground") || s.Description.Contains("garden")));
            int childrenCount = segs.Count(s => s.Description != null && (
                s.Description.Contains("child") || s.Description.Contains("kid") ||
                s.Description.Contains("family") || s.Description.Contains("deca") ||
                s.Description.Contains("djeca") || s.Description.Contains("dete") ||
                s.Description.Contains("baby")));
            double totalDur = segs.Sum(s => s.Duration);
            int highEnergy = segs.Count(s => s.Energy >= 4);
            int lowEnergy = segs.Count(s => s.Energy <= 2);

            LogToMainWindow("═══════════════════════════════════════");
            LogToMainWindow("📊 VALIDATION REPORT — Automatska provjera kvaliteta");
            LogToMainWindow("═══════════════════════════════════════");
            LogToMainWindow($"  ✅ Ukupno scena: {totalSegs}");
            LogToMainWindow($"  ⏱ Ukupno trajanje: {FormatTime(totalDur)}");
            LogToMainWindow($"  ⭐ Prosječan Vision Score: {avgScore:F1}/10" +
                            (avgScore >= 6.5 ? " — Dobar" : avgScore >= 5.0 ? " — Prihvatljiv" : " — ⚠ Slab"));
            LogToMainWindow($"  🌿 Priroda/Outdoor kadrovi: {outdoorCount}/{totalSegs}" +
                            (outdoorCount >= totalSegs / 2 ? " ✅" : " ⚠ Malo outdoor"));
            LogToMainWindow($"  👧 Dječiji/Porodični kadrovi: {childrenCount}/{totalSegs}" +
                            (childrenCount > 0 ? " ✅" : " ⚠ Nema djece na snimcima"));
            LogToMainWindow($"  🎬 Statični kadrovi (Ken Burns primijenjen): {staticCount}/{totalSegs}");
            LogToMainWindow($"  ⚡ Visoka energija (scena 4-5): {highEnergy} scena");
            LogToMainWindow($"  🌊 Niska energija (scena 1-2): {lowEnergy} scena");

            bool hasWarnings = false;
            if (avgScore < 5.0)
            { LogToMainWindow("  ⚠ UPOZORENJE: Prosječni score je nizak. Razmotri ponovnu generaciju."); hasWarnings = true; }
            if (outdoorCount < totalSegs / 3)
            { LogToMainWindow("  ⚠ UPOZORENJE: Malo outdoor scena — moguće indoor/mračni kadrovi."); hasWarnings = true; }
            if (staticCount > totalSegs / 2)
            { LogToMainWindow($"  ⚠ INFO: Više od polovine scena je statično ({staticCount}) — Ken Burns primijenjen na sve."); }

            if (!hasWarnings)
                LogToMainWindow("  🎉 Sve provjere prošle — video bi trebao biti bez 'gluposti'!");

            LogToMainWindow($"  🎵 Kontekst: {_detectedContext} | Mood: {_detectedMood}");
            LogToMainWindow("  📝 Opis: Snimci su pretežno " +
                (outdoorCount > childrenCount ? "priroda i vanjski prostori" : "djeca i porodica") +
                $", ukupno {FormatTime(totalDur)} materijala.");
            LogToMainWindow("═══════════════════════════════════════");

            AnnounceToUser($"Validation Report: {totalSegs} scena, prosječan score {avgScore:F1}, " +
                $"{outdoorCount} outdoor, {childrenCount} dječijih kadrova.", 0);
        }

        private string _lastShotType = "";

        private async Task<bool> CheckBrightnessAndTint(string videoPath, string ffmpegPath, CancellationToken ct)
        {
            if (!File.Exists(ffmpegPath) || !File.Exists(videoPath)) return true;

            try
            {
                string args = $"-nostdin -i \"{videoPath}\" " +
                              $"-vf \"select=eq(n\\,5),signalstats=stat=tout+brng\" " +
                              $"-f null -";

                var psi = new ProcessStartInfo(ffmpegPath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                var soTask = proc.StandardOutput.ReadToEndAsync();
                var seTask = proc.StandardError.ReadToEndAsync();
                await Task.WhenAll(soTask, seTask);
                await proc.WaitForExitAsync(ct);
                string output = seTask.Result + soTask.Result;

                var yavgMatch = System.Text.RegularExpressions.Regex.Match(output, @"YAVG:(\d+\.?\d*)");
                if (yavgMatch.Success && double.TryParse(yavgMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double yavg))
                {
                    if (yavg < 40)
                    {
                        LogToMainWindow($"   📊 Brightness {yavg:F0}/255 — previše tamno (min=40)");
                        return false;
                    }
                    if (yavg > 235)
                    {
                        LogToMainWindow($"   📊 Brightness {yavg:F0}/255 — preekspozirano (max=235)");
                        return false;
                    }
                }

                var ravgMatch = System.Text.RegularExpressions.Regex.Match(output, @"RAVG:(\d+\.?\d*)");
                var gavgMatch = System.Text.RegularExpressions.Regex.Match(output, @"GAVG:(\d+\.?\d*)");
                if (ravgMatch.Success && gavgMatch.Success)
                {
                    double ravg = double.Parse(ravgMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double gavg = double.Parse(gavgMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double pinkDiff = ravg - gavg;

                    if (pinkDiff > 18)
                    {
                        LogToMainWindow($"   📊 Tint R-G={pinkDiff:F1} — pink sadržaj (max=18)");
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private async Task<string> TrimVideoToDuration(string inputPath, double targetDuration, string tempDir)
        {
            var sw = Stopwatch.StartNew();
            LogToMainWindow($"✂️ Trim/Loop: {Path.GetFileName(inputPath)} → {targetDuration:F1}s");

            try
            {
                string outputPath = Path.Combine(tempDir, $"trimmed_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");
                string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath)) return inputPath;

                double actualDuration = await GetVideoDuration(inputPath);

                string args;
                if (actualDuration >= targetDuration - 0.5)
                {
                    args = $"-ss 0 -i \"{inputPath}\" -t {targetDuration.ToString(CultureInfo.InvariantCulture)} -c copy -avoid_negative_ts make_zero -y \"{outputPath}\"";
                    LogToMainWindow($"   ✂ Trim: {actualDuration:F1}s → {targetDuration:F1}s");
                }
                else
                {
                    LogToMainWindow($"   🔁 Kratak video ({actualDuration:F1}s < {targetDuration:F1}s) — RenderEngine će loopovati");
                    return inputPath;
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };
                process.Start();
                var trimSo = process.StandardOutput.ReadToEndAsync();
                var trimSe = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(trimSo, trimSe);
                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                {
                    LogToMainWindow($"✅ Trim/Loop uspješan za {sw.ElapsedMilliseconds}ms");
                    return outputPath;
                }

                LogToMainWindow($"⚠️ Trim/Loop neuspješan — koristim original");
                return inputPath;
            }
            catch (Exception ex)
            {
                LogToMainWindow($"❌ Trim greška: {ex.Message}");
                return inputPath;
            }
        }

        #endregion

        private async Task<double> GetVideoDuration(string videoPath)
        {
            try
            {
                string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath)) return 5.0;

                string args = $"-nostdin -i \"{videoPath}\" -f null -";
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true
                    }
                };
                process.Start();
                process.StandardInput.Close();
                var _so1 = process.StandardOutput.ReadToEndAsync();
                var _se1 = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(_so1, _se1);
                await process.WaitForExitAsync();
                string output = _se1.Result;

                var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                if (match.Success)
                {
                    int hours = int.Parse(match.Groups[1].Value);
                    int minutes = int.Parse(match.Groups[2].Value);
                    double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    return hours * 3600 + minutes * 60 + seconds;
                }
                return 5.0;
            }
            catch { return 5.0; }
        }

        private async Task<string> SearchAndDownloadMedia(string keywords, int minWidth, string mediaType, CancellationToken ct, double minDurationSeconds = 0)
        {
            LogToMainWindow($"🔍 SearchAndDownloadMedia: '{keywords.Substring(0, Math.Min(60, keywords.Length))}...', type={mediaType}");

            string apiKey = null;
            try
            {
                string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
                string keyFile = Path.Combine(settingsPath, "pixabay_key.bin");

                if (File.Exists(keyFile))
                {
                    byte[] encrypted = File.ReadAllBytes(keyFile);
                    byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    apiKey = Encoding.UTF8.GetString(decrypted).Trim();
                    LogToMainWindow($"✅ API ključ učitan, dužina: {apiKey.Length}");
                }
                else
                {
                    string txtFile = Path.Combine(settingsPath, "pixabay_key.txt");
                    if (File.Exists(txtFile))
                    {
                        apiKey = File.ReadAllText(txtFile).Trim();
                        LogToMainWindow($"✅ API ključ učitan iz TXT, dužina: {apiKey.Length}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow($"❌ Greška pri čitanju API ključa: {ex.Message}");
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                string newKey = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new ApiKeyDialog("Pixabay",
                        L("pixabay_key_not_found_msg"));
                    if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.ApiKey))
                    {
                        newKey = dlg.ApiKey;
                        try
                        {
                            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
                            Directory.CreateDirectory(settingsPath);
                            string keyFile = Path.Combine(settingsPath, "pixabay_key.bin");
                            byte[] data = Encoding.UTF8.GetBytes(newKey);
                            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                            File.WriteAllBytes(keyFile, encrypted);
                        }
                        catch (Exception ex) { LogToMainWindow($"❌ Greška pri čuvanju: {ex.Message}"); }
                        apiKey = newKey;
                    }
                });

                if (string.IsNullOrEmpty(apiKey))
                {
                    AnnounceToUser(L("pixabay_key_not_entered"));
                    return null;
                }
            }

            _pixabayApiKey = apiKey;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await _apiRateLimiter.WaitAsync(ct);

                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        string searchQ = attempt == 0 ? keywords : _universalKeywords[new Random().Next(_universalKeywords.Count)];

                        string url = mediaType == "video"
                            ? (minDurationSeconds > 20
                                ? $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=30&video_type=film&min_duration=20&max_duration=60"
                                : minDurationSeconds > 4
                                    ? $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=30&video_type=film&min_duration=4&max_duration=60"
                                    : $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=30&video_type=film")
                            : $"https://pixabay.com/api/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&image_type=photo&safesearch=true&per_page=30&min_width={minWidth}";

                        string response = await _httpClient.GetStringAsync(url, ct);
                        var json = JObject.Parse(response);
                        var hits = json["hits"] as JArray;

                        if (hits == null || hits.Count == 0) continue;

                        if (mediaType == "video")
                        {
                            var whitelistTags = new[] { "nature","park","family","kid","children","child",
                                "smiling","bright","daytime","outdoor","sunshine","happy","playing",
                                "playground","flowers","grass","blue sky" };
                            var sortedHits = hits.Cast<JToken>().OrderByDescending(h => {
                                string tags = h["tags"]?.ToString()?.ToLower() ?? "";
                                return whitelistTags.Count(w => tags.Contains(w));
                            }).ToList();

                            string queryKey = searchQ.ToLowerInvariant().Trim();
                            if (!_queryUseCount.ContainsKey(queryKey))
                                _queryUseCount[queryKey] = 0;

                            int startIdx = _queryUseCount[queryKey] % sortedHits.Count;
                            _queryUseCount[queryKey]++;

                            for (int hitOffset = 0; hitOffset < sortedHits.Count; hitOffset++)
                            {
                                int hitIdx = (startIdx + hitOffset) % sortedHits.Count;
                                var hit = sortedHits[hitIdx];
                                var videos = hit["videos"] as JObject;
                                if (videos == null) continue;

                                string[] formats = { "large", "medium", "small" };
                                foreach (var fmt in formats)
                                {
                                    var videoObj = videos[fmt];
                                    if (videoObj == null) continue;

                                    string dlUrl = videoObj["url"]?.ToString();
                                    if (string.IsNullOrEmpty(dlUrl)) continue;

                                    if (_usedMediaUrls.Contains(dlUrl))
                                    {
                                        LogToMainWindow($"   ⏭ Preskačem već korišćeni video URL (hit {hitIdx + 1})");
                                        continue;
                                    }

                                    string hitTags = hit["tags"]?.ToString()?.ToLower() ?? "";

                                    var universalBadTags = new[] {
                                        "corridor", "hallway", "lobby", "atrium", "marble",
                                        "frog", "toad", "reptile", "snake", "spider",
                                        "black and white", "monochrome", "silhouette",
                                        "animation", "cartoon", "cartoons", "animated", "3d", "render", "rendered",
                                        "illustration", "drawing", "painting", "sketch", "art",
                                        "ai generated", "ai-generated", "generative", "abstract",
                                        "background", "texture", "pattern", "graphics", "vector",
                                        "fantasy", "surreal", "watercolor", "cg", "vfx", "particles"
                                    };

                                    string[] contextBadTags;
                                    switch (_detectedContext)
                                    {
                                        case "children":
                                        case "lullaby":
                                        case "fun":
                                        case "school":
                                            contextBadTags = new[] {
                                                "coffee", "tea cup", "beer", "wine", "whiskey", "alcohol",
                                                "cigarette", "smoking", "office", "corporate", "business",
                                                "meeting", "suit", "formal", "briefcase", "laptop work",
                                                "nightclub", "bar", "pub", "casino", "violence",
                                                "cemetery", "funeral", "weapon", "gun", "knife"
                                            };
                                            break;
                                        case "wedding":
                                        case "love":
                                        case "romantic":
                                            contextBadTags = new[] {
                                                "violence", "weapon", "gun", "knife", "horror",
                                                "cemetery", "funeral", "accident", "crash"
                                            };
                                            break;
                                        case "adventure":
                                        case "sport":
                                            contextBadTags = new[] {
                                                "hospital", "sick", "death", "violence", "gun",
                                                "office", "corporate", "boring", "slow"
                                            };
                                            break;
                                        default:
                                            contextBadTags = new[] {
                                                "violence", "weapon", "gun", "knife", "horror",
                                                "cemetery", "funeral", "accident", "crash"
                                            };
                                            break;
                                    }

                                    bool hasBadTag = universalBadTags.Any(t => hitTags.Contains(t))
                                                  || contextBadTags.Any(t => hitTags.Contains(t));
                                    if (hasBadTag)
                                    {
                                        LogToMainWindow($"   ⏭ Preskačem neprikladan sadrzaj (tags: {hitTags.Substring(0, Math.Min(60, hitTags.Length))})");
                                        continue;
                                    }

                                    _usedMediaUrls.Add(dlUrl);

                                    string fileName = $"AI_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4";
                                    string fullPath = Path.Combine(GetCurrentProjectFolder(), fileName);

                                    LogToMainWindow($"   ⬇ Preuzimam video hit {hitIdx + 1}/{sortedHits.Count} (format: {fmt}): {dlUrl.Substring(dlUrl.LastIndexOf('/') + 1)}");
                                    using var dlStream = await _dlHttpClient.GetStreamAsync(dlUrl, ct);
                                    using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                                    await dlStream.CopyToAsync(fileStream, ct);

                                    string ffmpegForBright = System.IO.Path.Combine(
                                        AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                                    var brightnessOk = await CheckBrightnessAndTint(fullPath, ffmpegForBright, ct);
                                    if (!brightnessOk && hitOffset < sortedHits.Count - 2)
                                    {
                                        LogToMainWindow($"   ⏭ Odbačen zbog brightness/tint (previše tamno/svijetlo ili pink tint)");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    string ffmpegForVision = System.IO.Path.Combine(
                                        AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                                    var vision = await VisionAnalyzer.AnalyzeClipAsync(fullPath, ffmpegForVision, ct);

                                    if (vision.Score < 4.0 && hitOffset < hits.Count - 2)
                                    {
                                        LogToMainWindow($"   ⚠ VisionScore {vision.Score:F1}/10 — odbacujem ({vision.RejectReason}), trazim bolji...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        break;
                                    }

                                    var clipMotion = await MotionAnalyzer.AnalyzeAsync(fullPath, ffmpegForVision, ct);
                                    bool motionOk = MotionResult.IsCompatible(_lastClipMotion, clipMotion);

                                    if (!motionOk && hitOffset < sortedHits.Count - 2)
                                    {
                                        LogToMainWindow("   Motion mismatch (" + (_lastClipMotion?.Direction.ToString() ?? "null") + " -> " + clipMotion.Direction + ") -- trazim bolji...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        break;
                                    }

                                    string hitTagsForShot = hit["tags"]?.ToString()?.ToLower() ?? "";
                                    string shotType = "medium";
                                    if (hitTagsForShot.Contains("aerial") || hitTagsForShot.Contains("drone") ||
                                        hitTagsForShot.Contains("landscape") || hitTagsForShot.Contains("panorama") ||
                                        hitTagsForShot.Contains("wide") || vision.IsOutdoor && !vision.HasChildren)
                                        shotType = "wide";
                                    else if (hitTagsForShot.Contains("close") || hitTagsForShot.Contains("portrait") ||
                                             hitTagsForShot.Contains("face") || vision.HasChildren)
                                        shotType = "close";

                                    if (shotType == _lastShotType && _lastShotType != "" && hitOffset < sortedHits.Count - 2)
                                    {
                                        LogToMainWindow($"   🎬 Shot composition: dva uzastopna '{shotType}' — tražim drugi tip...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    _lastClipMotion = clipMotion;
                                    _lastShotType = shotType;
                                    string visionTag = vision.OnnxUsed ? $"ONNX:{vision.TopLabel}" : "FFmpeg";
                                    LogToMainWindow($"   ✅ Score {vision.Score:F1}/10 [{visionTag}] | Motion:{clipMotion.Direction} | Shot:{shotType} | " +
                                        $"Children:{vision.HasChildren} Outdoor:{vision.IsOutdoor}");

                                    _lastDownloadedVisionScore = vision.Score;
                                    _lastDownloadedIsStatic = clipMotion.Direction == MotionDirection.Unknown
                                                              || clipMotion.Direction == MotionDirection.Static;
                                    return fullPath;
                                }
                            }

                            LogToMainWindow($"   ⚠ Svi {sortedHits.Count} video rezultati za '{searchQ.Substring(0, Math.Min(40, searchQ.Length))}' su već korišćeni");
                        }
                        else
                        {
                            string queryKey = searchQ.ToLowerInvariant().Trim() + "_img";
                            if (!_queryUseCount.ContainsKey(queryKey))
                                _queryUseCount[queryKey] = 0;

                            int startIdx = _queryUseCount[queryKey] % hits.Count;
                            _queryUseCount[queryKey]++;

                            for (int hitOffset = 0; hitOffset < hits.Count; hitOffset++)
                            {
                                int hitIdx = (startIdx + hitOffset) % hits.Count;
                                var hit = hits[hitIdx];
                                string dlUrl = hit["largeImageURL"]?.ToString() ?? hit["webformatURL"]?.ToString();

                                if (string.IsNullOrEmpty(dlUrl)) continue;
                                if (_usedMediaUrls.Contains(dlUrl))
                                {
                                    LogToMainWindow($"   ⏭ Preskačem već korišćenu sliku (hit {hitIdx + 1})");
                                    continue;
                                }

                                _usedMediaUrls.Add(dlUrl);
                                string fileName = $"AI_{Guid.NewGuid().ToString().Substring(0, 8)}.jpg";
                                string fullPath = Path.Combine(GetCurrentProjectFolder(), fileName);

                                using var dlStream = await _dlHttpClient.GetStreamAsync(dlUrl, ct);
                                using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                                await dlStream.CopyToAsync(fileStream, ct);
                                return fullPath;
                            }
                            LogToMainWindow($"   ⚠ Sve slike za '{searchQ.Substring(0, Math.Min(40, searchQ.Length))}' su već korišćene");
                        }
                    }
                    finally
                    {
                        _apiRateLimiter.Release();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex) when (ex.Message.Contains("429"))
                {
                    await Task.Delay(2000, ct);
                }
                catch (Exception ex)
                {
                    LogToMainWindow($"❌ Greška u attempt {attempt + 1}: {ex.Message}");
                }
            }
            return null;
        }

        private async Task CreateVideo(StoryBoard storyBoard = null)
        {
            var mainWindow = WpfApp.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            await Dispatcher.InvokeAsync(() => mainWindow.SaveState());

            var itemsToRemove = mainWindow.timelineItems
                .Where(i => i.Type == "Image" || i.Type == "Video" ||
                            (i.Type == "Audio" && i.Name.StartsWith("🔊")))
                .ToList();
            foreach (var item in itemsToRemove)
                mainWindow.timelineItems.Remove(item);

            double cursor = 0;
            var addedItems = new List<TimelineItem>();

            if (chkShowLogo.IsChecked == true && !string.IsNullOrEmpty(txtLogoPath.Text) && File.Exists(txtLogoPath.Text))
            {
                double logoDuration = double.TryParse(txtLogoDuration.Text, out double ld) ? ld : 5;
                var logoItem = new TimelineItem
                {
                    Path = txtLogoPath.Text,
                    Duration = logoDuration,
                    Start = cursor,
                    End = cursor + logoDuration,
                    Name = "Logo kanala - Rastimo uz Iskru",
                    Type = "Image",
                    Volume = 100,
                    TrackIndex = 0,
                    VideoEffect = new VideoEffectData()
                };
                mainWindow.timelineItems.Add(logoItem);
                addedItems.Add(logoItem);
                cursor += logoDuration;
            }

            string songTitle = string.IsNullOrEmpty(txtIntroText.Text) ? "🎵 Nova pjesmica" : txtIntroText.Text;
            double introDuration = 4;
            string introImagePath = await CreateTextImage(songTitle, introDuration, true);
            var introItem = new TimelineItem
            {
                Path = introImagePath,
                Duration = introDuration,
                Start = cursor,
                End = cursor + introDuration,
                Name = $"Naslov: {songTitle}",
                Type = "Image",
                Volume = 100,
                TrackIndex = 0,
                VideoEffect = new VideoEffectData()
            };
            mainWindow.timelineItems.Add(introItem);
            addedItems.Add(introItem);
            cursor += introDuration;

            _timelineCursorOffset = cursor;

            if (_enableAmbientSounds && storyBoard?.Scenes != null &&
                storyBoard.Scenes.Any(s => !string.IsNullOrEmpty(s.AmbientPath)))
            {
                AnnounceToUser(L("mixing_ambience"), 90);
                _ambientAudioPath = await MixAmbientWithMusic(
                    _audioPath, storyBoard.Scenes, _totalDuration, _tempVideoFolder);
            }

            var sortedSegments = _segments.OrderBy(s => s.StartTime).ToList();
            double maxEnd = cursor;

            double anchorBrightnessValue = 0;
            if (sortedSegments.Count > 2)
            {
                var anchorSegment = sortedSegments
                    .Where(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path))
                    .OrderByDescending(s => s.VisionScore)
                    .FirstOrDefault();
                if (anchorSegment != null)
                {
                    anchorBrightnessValue = 0.3 + (anchorSegment.VisionScore / 10.0) * 0.4;
                    LogToMainWindow($"🎨 Color Match anchor: '{anchorSegment.Description}' (Score={anchorSegment.VisionScore:F1}, Brightness={anchorBrightnessValue:F3})");
                }
            }

            foreach (var segment in sortedSegments)
            {
                if (!File.Exists(segment.Path)) continue;

                string ext = Path.GetExtension(segment.Path).ToLower();
                string type = (ext == ".mp4" || ext == ".avi" || ext == ".mov" || ext == ".mkv") ? "Video" : "Image";

                var newItem = new TimelineItem
                {
                    Path = segment.Path,
                    Duration = segment.Duration,
                    Start = cursor + segment.StartTime,
                    End = cursor + segment.StartTime + segment.Duration,
                    Name = segment.Description,
                    Type = type,
                    Volume = 100,
                    TrackIndex = 0,
                    VideoEffect = new VideoEffectData(),
                    AudioDescription = !string.IsNullOrEmpty(segment.MoodTag)
                        ? $"{segment.MoodTag}|energy={segment.Energy}" +
                          (segment.IsStaticClip ? "|static=1" : "") +
                          (anchorBrightnessValue > 0 ? $"|anchor_brightness={anchorBrightnessValue.ToString("F3", CultureInfo.InvariantCulture)}" : "")
                        : $"{segment.Description}|energy={segment.Energy}" +
                          (segment.IsStaticClip ? "|static=1" : "")
                };
                mainWindow.timelineItems.Add(newItem);
                addedItems.Add(newItem);

                float zoomEnd = 1.03f;
                float xEnd = 5f;
                float yEnd = 3f;

                if (segment.Energy >= 4)
                {
                    zoomEnd = 1.08f;
                    xEnd = 15f;
                    yEnd = 8f;
                }
                else if (segment.Energy <= 2)
                {
                    zoomEnd = 1.02f;
                    xEnd = 3f;
                    yEnd = 2f;
                }

                newItem.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0f, X = 0, Y = 0 });
                newItem.Keyframes.Add(new AnimationKeyframe { Time = segment.Duration * 0.3f, Zoom = 1.02f, X = 3f, Y = 2f });
                newItem.Keyframes.Add(new AnimationKeyframe { Time = segment.Duration * 0.7f, Zoom = zoomEnd - 0.02f, X = xEnd - 5f, Y = yEnd - 3f });
                newItem.Keyframes.Add(new AnimationKeyframe { Time = segment.Duration, Zoom = zoomEnd, X = xEnd, Y = yEnd });

                if (_showLyrics && !string.IsNullOrEmpty(segment.LyricText))
                    mainWindow.AddSubtitle(segment.LyricText, newItem.Start, newItem.End);

                if (newItem.End > maxEnd) maxEnd = newItem.End;
            }

            double totalDuration = maxEnd;

            string outroText = string.IsNullOrEmpty(txtOutroText.Text) ?
                "Autor: Iskra Ajvazi. Muzika i tekst: Iskra Ajvazi. Za još predivnih pesmica, zapratite naš YouTube kanal: @Rastimo uz Iskru" :
                txtOutroText.Text;

            double outroDuration = 7;
            double outroStart = maxEnd;
            totalDuration = maxEnd + outroDuration;
            string outroImagePath = await CreateTextImage(outroText, outroDuration, false);
            var outroItem = new TimelineItem
            {
                Path = outroImagePath,
                Duration = outroDuration,
                Start = outroStart,
                End = outroStart + outroDuration,
                Name = "Odjavni tekst",
                Type = "Image",
                Volume = 100,
                TrackIndex = 0,
                VideoEffect = new VideoEffectData()
            };
            mainWindow.timelineItems.Add(outroItem);
            addedItems.Add(outroItem);

            var audioItem = mainWindow.timelineItems.FirstOrDefault(i => i.Type == "Audio");
            if (audioItem != null)
            {
                if (!string.IsNullOrEmpty(_ambientAudioPath) && File.Exists(_ambientAudioPath))
                {
                    audioItem.Path = _ambientAudioPath;
                    LogToMainWindow("🎵 Koristim miksani audio sa ambijentalnim zvukovima");
                }
                audioItem.Start = 0;
                audioItem.End = totalDuration;
                audioItem.Duration = totalDuration;
            }

            var videoImageItems = addedItems
                .Where(i => i.Type == "Video" || i.Type == "Image")
                .OrderBy(i => i.Start)
                .ToList();

            for (int i = 0; i < videoImageItems.Count - 1; i++)
            {
                var currentItem = videoImageItems[i];
                var nextItem = videoImageItems[i + 1];
                double gap = nextItem.Start - currentItem.End;

                int currentEnergy = i < _segments.Count ? _segments[i]?.Energy ?? 3 : 3;
                int nextEnergy = i + 1 < _segments.Count ? _segments[i + 1]?.Energy ?? 3 : 3;
                int transEnergy = (currentEnergy + nextEnergy) / 2;
                double fadeDuration = transEnergy >= 4 ? 0.3 : 0.4;

                bool addTransitionSound = _enableTransitionSounds && transEnergy >= 3;
                string soundType = transEnergy >= 4 ? "whoosh" : "pop";
                int transitionVolume = transEnergy switch
                {
                    5 => 85,
                    4 => 75,
                    3 => 65,
                    _ => 55
                };
                double transitionDuration = transEnergy >= 4 ? 0.55 : 0.4;

                if (gap > 0.1)
                {
                    AddSmoothFadeOut(currentItem, fadeDuration);
                    AddSmoothFadeIn(nextItem, fadeDuration);

                    if (addTransitionSound)
                    {
                        string transSound = await GetTransitionSound(_tempVideoFolder, soundType);
                        if (transSound != null && File.Exists(transSound))
                        {
                            mainWindow.timelineItems.Add(new TimelineItem
                            {
                                Path = transSound,
                                Duration = transitionDuration,
                                Start = currentItem.End - 0.1,
                                End = currentItem.End - 0.1 + transitionDuration,
                                Name = $"🔊 {(soundType == "whoosh" ? "Whoosh" : "Pop")}",
                                Type = "Audio",
                                Volume = transitionVolume,
                                TrackIndex = 1
                            });
                        }
                    }
                }
                else
                {
                    AddCrossfadeWithEnergy(currentItem, nextItem, fadeDuration, transEnergy);

                    if (addTransitionSound)
                    {
                        string popSound = await GetTransitionSound(_tempVideoFolder, "pop");
                        if (popSound != null && File.Exists(popSound))
                        {
                            mainWindow.timelineItems.Add(new TimelineItem
                            {
                                Path = popSound,
                                Duration = transitionDuration,
                                Start = currentItem.End - 0.1,
                                End = currentItem.End - 0.1 + transitionDuration,
                                Name = "🔊 Pop",
                                Type = "Audio",
                                Volume = transitionVolume,
                                TrackIndex = 1
                            });
                        }
                    }
                }
            }

            LogToMainWindow("🎬 Ažuriram timeline prikaz...");
            await Dispatcher.InvokeAsync(() => mainWindow.UpdateTimelineDisplay());
            LogToMainWindow("✅ Timeline prikaz ažuriran");

            int segmentsCount = _segments?.Count ?? 0;
            double segmentsTotal = _segments?.Sum(s => s.Duration) ?? 0;

            WpfMessageBox.Show(
                LF("creator_finished", segmentsCount, FormatTime(segmentsTotal), segmentsTotal, FormatTime(totalDuration), totalDuration),
                L("creator_finished_title"), MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        private void AddSmoothFadeOut(TimelineItem item, double duration)
        {
            double startTime = Math.Max(0, item.Duration - duration);
            item.Keyframes.Add(new AnimationKeyframe { Time = startTime, Opacity = 1 });
            item.Keyframes.Add(new AnimationKeyframe { Time = item.Duration, Opacity = 0 });
        }

        private void AddSmoothFadeIn(TimelineItem item, double duration)
        {
            item.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 0 });
            item.Keyframes.Add(new AnimationKeyframe { Time = duration, Opacity = 1 });
        }

        private void AddCrossfadeWithEnergy(TimelineItem currentItem, TimelineItem nextItem, double duration, int nextEnergy)
        {
            double startFade = Math.Max(0, currentItem.Duration - duration);
            currentItem.Keyframes.Add(new AnimationKeyframe { Time = startFade, Opacity = 1 });
            currentItem.Keyframes.Add(new AnimationKeyframe { Time = currentItem.Duration, Opacity = 0 });

            double fadeInDuration = nextEnergy >= 4 ? duration * 0.6 : duration;
            nextItem.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 0 });
            nextItem.Keyframes.Add(new AnimationKeyframe { Time = fadeInDuration, Opacity = 1 });
        }

        private async Task<string> CreateTextImage(string text, double duration, bool isIntro = false)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"text_{Guid.NewGuid()}.png");

            await Task.Run(() =>
            {
                using (var surface = SKSurface.Create(new SKImageInfo(1920, 1080)))
                {
                    var canvas = surface.Canvas;
                    canvas.Clear(isIntro ? new SKColor(46, 125, 50) : SKColors.Black);

                    using (var paint = new SKPaint())
                    {
                        paint.Color = SKColors.White;
                        paint.TextSize = isIntro ? 96 : 56;
                        paint.IsAntialias = true;
                        paint.TextAlign = SKTextAlign.Center;

                        if (isIntro)
                            paint.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

                        var words = text.Split(' ');
                        var lines = new List<string>();
                        var currentLine = "";
                        int maxCharsPerLine = isIntro ? 20 : 40;

                        foreach (var word in words)
                        {
                            var testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                            if (testLine.Length > maxCharsPerLine)
                            {
                                lines.Add(currentLine);
                                currentLine = word;
                            }
                            else
                            {
                                currentLine = testLine;
                            }
                        }
                        if (!string.IsNullOrEmpty(currentLine)) lines.Add(currentLine);

                        float startY = 540 - (lines.Count - 1) * (isIntro ? 50 : 35);
                        float y = startY;
                        foreach (var line in lines)
                        {
                            canvas.DrawText(line, 960, y, paint);
                            y += isIntro ? 80 : 60;
                        }
                    }

                    using (var image = surface.Snapshot())
                    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                    using (var stream = File.OpenWrite(tempPath))
                    {
                        data.SaveTo(stream);
                    }
                }
            });

            return tempPath;
        }

        private string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");

        private string GetPixabayApiKey()
        {
            try
            {
                string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
                string keyFile = Path.Combine(settingsPath, "pixabay_key.bin");

                if (File.Exists(keyFile))
                {
                    byte[] encrypted = File.ReadAllBytes(keyFile);
                    byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decrypted).Trim();
                }

                string txtFile = Path.Combine(settingsPath, "pixabay_key.txt");
                if (File.Exists(txtFile))
                    return File.ReadAllText(txtFile).Trim();
            }
            catch (Exception ex)
            {
                AnnounceToUser(LF("pixabay_key_read_error", ex.Message));
            }
            return null;
        }

        private void SavePixabayApiKey(string key)
        {
            try
            {
                string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
                Directory.CreateDirectory(settingsPath);
                string keyFile = Path.Combine(settingsPath, "pixabay_key.bin");
                byte[] data = Encoding.UTF8.GetBytes(key);
                byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(keyFile, encrypted);
            }
            catch (Exception ex)
            {
                AnnounceToUser(LF("pixabay_key_save_error", ex.Message));
            }
        }

        #region Mood Color Grading i Gradient Fallback

        private async Task<string> GenerateMoodGradient(string mood, double duration, string tempDir)
        {
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) return null;

            string color = mood?.ToLower() switch
            {
                "sad" or "melancholy" => "0x3a5f8a",
                "happy" or "joyful" => "0xf4a020",
                "calm" or "peaceful" => "0x4a9e6b",
                "excited" or "upbeat" => "0xe03060",
                "romantic" or "love" => "0xc04070",
                "christmas" => "0xb01010",
                "lullaby" => "0x6a5acd",
                _ => "0x2060a0"
            };

            string outputPath = Path.Combine(tempDir, $"gradient_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");
            string dStr = duration.ToString(System.Globalization.CultureInfo.InvariantCulture);

            double fadeOutStart = Math.Max(0, duration - 1);
            string fadeOutStr = fadeOutStart.ToString(System.Globalization.CultureInfo.InvariantCulture);

            string args = $"-f lavfi -i \"color=c={color}:s=1280x720:d={dStr},format=yuv420p\" " +
                          $"-vf \"fade=in:0:25,fade=out:st={fadeOutStr}:d=1\" " +
                          $"-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1 -pix_fmt yuv420p -an -y \"{outputPath}\"";

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();
            var _bgSo = proc.StandardOutput.ReadToEndAsync();
            var _bgSe = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(_bgSo, _bgSe);
            await proc.WaitForExitAsync();

            return (proc.ExitCode == 0 && File.Exists(outputPath)) ? outputPath : null;
        }

        public static string GetMoodColorFilter(string mood, string context)
        {
            string m = (mood ?? "").ToLower();
            string c = (context ?? "").ToLower();

            if (c == "lullaby")
                return "eq=brightness=0.04:saturation=0.85:contrast=0.95,vignette=PI/4";
            if (c == "christmas")
                return "eq=saturation=1.25:contrast=1.08," +
                       "curves=r='0/0 0.5/0.56 1/1':g='0/0 0.5/0.52 1/1'";
            if (c == "party")
                return "eq=saturation=1.35:brightness=0.04:contrast=1.08," +
                       "curves=r='0/0 0.5/0.54 1/1':g='0/0 0.5/0.53 1/1'";
            if (c == "nature")
                return "eq=saturation=1.12:contrast=1.04:brightness=0.02," +
                       "curves=g='0/0 0.5/0.53 1/1'";
            if (c == "outdoor" || c == "health")
                return "eq=saturation=1.18:brightness=0.03:contrast=1.04," +
                       "curves=r='0/0 0.5/0.53 1/1':g='0/0 0.5/0.525 1/1'";
            if (c == "music" || c == "dance")
                return "eq=saturation=1.25:brightness=0.03:contrast=1.06," +
                       "curves=r='0/0 0.5/0.535 1/1':g='0/0 0.5/0.525 1/1'";
            if (c == "school")
                return "eq=saturation=1.10:brightness=0.02:contrast=1.03," +
                       "curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'";
            if (c == "animal")
                return "eq=saturation=1.08:brightness=0.02:contrast=1.03," +
                       "curves=g='0/0 0.5/0.515 1/1'";
            if (c == "sad")
                return "eq=saturation=0.88:contrast=0.96:brightness=-0.01";

            return m switch
            {
                "sad" or "melancholy" =>
                    "eq=saturation=0.90:contrast=0.97",
                "happy" or "joyful" =>
                    "eq=saturation=1.20:brightness=0.03:contrast=1.05," +
                    "curves=r='0/0 0.5/0.53 1/1':g='0/0 0.5/0.52 1/1'",
                "calm" or "peaceful" =>
                    "eq=saturation=1.02:contrast=0.98:brightness=0.01",
                "excited" or "upbeat" =>
                    "eq=saturation=1.30:contrast=1.08:brightness=0.04," +
                    "curves=r='0/0 0.5/0.54 1/1':g='0/0 0.5/0.53 1/1'",
                "romantic" or "love" =>
                    "eq=saturation=1.12:brightness=0.02," +
                    "curves=r='0/0 0.5/0.535 1/1':g='0/0 0.5/0.52 1/1'",
                "playful" =>
                    "eq=saturation=1.25:brightness=0.03:contrast=1.06," +
                    "curves=r='0/0 0.5/0.535 1/1':g='0/0 0.5/0.525 1/1'",
                _ =>
                    "eq=saturation=1.10:brightness=0.02:contrast=1.03," +
                    "curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'"
            };
        }

        #endregion

        #region Sub-scene detekcija — Literal Visual Sync

        private List<string> DetectSubSceneQueries(string lyric, string style)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return null;
            string lower = lyric.ToLower();

            bool hasWinterClothing = (lower.Contains("čizme") || lower.Contains("izme")) &&
                                     (lower.Contains("rukavice") || lower.Contains("skafander") ||
                                      lower.Contains("kapu") || lower.Contains("šal") || lower.Contains("kaput"));
            if (hasWinterClothing)
            {
                var q = new List<string>();
                if (lower.Contains("čizme") || lower.Contains("izme"))
                    q.Add(BuildLiteralSearchQuery("child winter boots snow outdoor", "", "", style));
                if (lower.Contains("rukavice"))
                    q.Add(BuildLiteralSearchQuery("child winter gloves snow hands", "", "", style));
                if (lower.Contains("skafander") || lower.Contains("kombinezon"))
                    q.Add(BuildLiteralSearchQuery("child winter snowsuit playing snow", "", "", style));
                if (lower.Contains("kapu") || lower.Contains("kapi") || lower.Contains("šešir"))
                    q.Add(BuildLiteralSearchQuery("child wearing winter hat snow smiling", "", "", style));
                if (lower.Contains("šal") || lower.Contains("salom"))
                    q.Add(BuildLiteralSearchQuery("child scarf winter cozy warm", "", "", style));
                if (lower.Contains("kaput") || lower.Contains("jakna"))
                    q.Add(BuildLiteralSearchQuery("child winter coat dressed warm outdoor", "", "", style));
                if (q.Count >= 2) return q;
            }

            int seasonCount = 0;
            if (lower.Contains("proleć") || lower.Contains("proljeć") || lower.Contains("spring")) seasonCount++;
            if (lower.Contains("jesen") || lower.Contains("autumn") || lower.Contains("fall")) seasonCount++;
            if (lower.Contains("zima") || lower.Contains("winter")) seasonCount++;
            if (lower.Contains("leto") || lower.Contains("ljeto") || lower.Contains("summer")) seasonCount++;

            if (seasonCount >= 2)
            {
                var q = new List<string>();
                if (lower.Contains("proleć") || lower.Contains("proljeć"))
                    q.Add(BuildLiteralSearchQuery("child spring flowers park playing", "", "", style));
                if (lower.Contains("jesen") || lower.Contains("autumn") || lower.Contains("fall"))
                    q.Add(BuildLiteralSearchQuery("child autumn leaves park playing", "", "", style));
                if (lower.Contains("zima") || lower.Contains("winter"))
                    q.Add(BuildLiteralSearchQuery("child winter snow playing outdoor", "", "", style));
                if (lower.Contains("leto") || lower.Contains("ljeto") || lower.Contains("summer"))
                    q.Add(BuildLiteralSearchQuery("child summer sunny park playing", "", "", style));
                if (q.Count >= 2) return q;
            }

            int instCount = 0;
            if (lower.Contains("gitara") || lower.Contains("guitar")) instCount++;
            if (lower.Contains("klavir") || lower.Contains("piano")) instCount++;
            if (lower.Contains("bubanj") || lower.Contains("drum")) instCount++;
            if (lower.Contains("violina") || lower.Contains("violin")) instCount++;
            if (lower.Contains("flauta") || lower.Contains("flute")) instCount++;

            if (instCount >= 2)
            {
                var q = new List<string>();
                if (lower.Contains("gitara") || lower.Contains("guitar"))
                    q.Add(BuildLiteralSearchQuery("child playing guitar music happy", "", "", style));
                if (lower.Contains("klavir") || lower.Contains("piano"))
                    q.Add(BuildLiteralSearchQuery("child playing piano keyboard music", "", "", style));
                if (lower.Contains("bubanj") || lower.Contains("drum"))
                    q.Add(BuildLiteralSearchQuery("child playing drums music fun", "", "", style));
                if (lower.Contains("violina") || lower.Contains("violin"))
                    q.Add(BuildLiteralSearchQuery("child playing violin music", "", "", style));
                if (lower.Contains("flauta") || lower.Contains("flute"))
                    q.Add(BuildLiteralSearchQuery("child playing flute music", "", "", style));
                if (q.Count >= 2) return q;
            }

            int actionCount = 0;
            bool hasRun = lower.Contains("trči") || lower.Contains("trčanje");
            bool hasJump = lower.Contains("skače") || lower.Contains("skoči");
            bool hasSing = lower.Contains("peva") || lower.Contains("pjeva");
            bool hasDance = lower.Contains("pleše") || lower.Contains("plešeš") || lower.Contains("ples");
            bool hasSmile = lower.Contains("smej") || lower.Contains("smije") || lower.Contains("blistaj");
            bool hasWalk = lower.Contains("šeta") || lower.Contains("šetaj") || lower.Contains("hoda");

            if (hasRun) actionCount++;
            if (hasJump) actionCount++;
            if (hasSing) actionCount++;
            if (hasDance) actionCount++;
            if (hasSmile) actionCount++;

            if (actionCount >= 2)
            {
                var q = new List<string>();
                if (hasRun) q.Add(BuildLiteralSearchQuery("children running fast outdoor happy", "", "", style));
                if (hasJump) q.Add(BuildLiteralSearchQuery("child jumping happy excited outdoor", "", "", style));
                if (hasSing) q.Add(BuildLiteralSearchQuery("child singing joyful music", "", "", style));
                if (hasDance) q.Add(BuildLiteralSearchQuery("children dancing joyful colorful", "", "", style));
                if (hasSmile) q.Add(BuildLiteralSearchQuery("children laughing happy smiling faces", "", "", style));
                if (q.Count >= 2) return q;
            }

            var animalMap = new Dictionary<string, string>
            {
                {"pas ",  "dog playing happy"}, {"psa ", "dog playing happy"}, {"psić", "puppy cute"},
                {"mačka", "cat cute kitten"},   {"mace", "cat cute kitten"},
                {"zec",   "rabbit cute bunny"}, {"kunić", "rabbit cute"},
                {"ptica", "bird flying colorful"}, {"ptice", "birds flying"},
                {"riba",  "fish aquarium colorful"}, {"ribica", "fish colorful water"},
                {"konj",  "horse running field"}, {"medved", "bear forest nature"},
                {"slon",  "elephant nature wild"}, {"leptir", "butterfly flower garden"}
            };
            var foundAnimals = animalMap.Where(kv => lower.Contains(kv.Key)).ToList();
            if (foundAnimals.Count >= 2)
            {
                return foundAnimals.Take(4)
                    .Select(kv => BuildLiteralSearchQuery("child " + kv.Value, "", "", style))
                    .ToList();
            }

            return null;
        }

        private async Task<string> BuildSubSceneVideo(
            List<string> queries, double totalDuration, string tempDir, CancellationToken ct)
        {
            double clipDur = Math.Max(1.5, Math.Round(totalDuration / queries.Count, 1));
            var clipPaths = new List<string>();

            foreach (var query in queries)
            {
                ct.ThrowIfCancellationRequested();
                string path = await SearchAndDownloadMedia(query, 1080, "video", ct, clipDur);
                if (path == null) continue;

                double actualDur = await GetVideoDuration(path);
                if (actualDur > clipDur + 0.3)
                    path = await TrimVideoToDuration(path, clipDur, tempDir);
                clipPaths.Add(path);
            }

            if (clipPaths.Count == 0) return null;
            if (clipPaths.Count == 1) return clipPaths[0];

            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) return clipPaths[0];

            string concatFile = Path.Combine(tempDir, $"subscene_{Guid.NewGuid().ToString().Substring(0, 8)}.txt");
            string outputPath = Path.Combine(tempDir, $"subscene_out_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");

            using (var sw = new StreamWriter(concatFile, false, new System.Text.UTF8Encoding(false)))
                foreach (var p in clipPaths)
                    sw.WriteLine("file '" + p.Replace("\\", "/") + "'");

            string args = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" " +
                          $"-vf \"scale=1920:1080:flags=lanczos,format=yuv420p\" " +
                          $"-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0 -profile:v high -level 4.1 " +
                          $"-an -y \"{outputPath}\"";
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();
            var soTask = proc.StandardOutput.ReadToEndAsync();
            var seTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(soTask, seTask);
            await proc.WaitForExitAsync(ct);
            bool ok = proc.ExitCode == 0;

            if (ok && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000)
                return outputPath;

            return clipPaths[0];
        }

        #endregion

        #region MultiClip — više videa po sceni
        private async Task<string> SearchAndDownloadMultipleMedia(
            string keywords, string fallbackKeywords, string mediaType,
            double targetDuration, string tempDir, CancellationToken ct)
        {
            int clipCount = Math.Max(2, (int)Math.Ceiling(targetDuration / 5.0));
            clipCount = Math.Min(clipCount, 4);

            double clipDuration = targetDuration / clipCount;
            var clipPaths = new List<string>();

            LogToMainWindow($"🎬 MultiClip: {clipCount} klipa × {clipDuration:F1}s = {targetDuration:F1}s ukupno");

            for (int c = 0; c < clipCount; c++)
            {
                ct.ThrowIfCancellationRequested();
                string q = (c % 2 == 0) ? keywords : (fallbackKeywords ?? keywords);
                string path = await SearchAndDownloadMedia(q, 1080, mediaType, ct, clipDuration);
                if (path == null && fallbackKeywords != null)
                    path = await SearchAndDownloadMedia(fallbackKeywords, 1080, mediaType, ct, clipDuration);
                if (path != null)
                    clipPaths.Add(path);
            }

            if (clipPaths.Count == 0) return null;
            if (clipPaths.Count == 1) return clipPaths[0];

            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) return clipPaths[0];

            string concatList = Path.Combine(tempDir, $"multiclip_{Guid.NewGuid().ToString().Substring(0, 8)}.txt");
            string outputPath = Path.Combine(tempDir, $"multi_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");

            var preparedPaths = new List<string>();
            foreach (var cp in clipPaths)
            {
                double dur = await GetVideoDuration(cp);
                string prepared = cp;
                if (dur > clipDuration + 0.5)
                    prepared = await TrimVideoToDuration(cp, clipDuration, tempDir);
                preparedPaths.Add(prepared);
            }

            using (var sw = new System.IO.StreamWriter(concatList, false, new System.Text.UTF8Encoding(false)))
                foreach (var p in preparedPaths)
                    sw.WriteLine("file '" + p.Replace("\\", "/") + "'");

            string args = $"-nostdin -f concat -safe 0 -i \"{concatList}\" " +
                          $"-vf \"scale=1920:1080:flags=lanczos,format=yuv420p\" " +
                          $"-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0 -profile:v high -level 4.1 " +
                          $"-an -y \"{outputPath}\"";

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(ct);
            string ffmpegErr = stderrTask.Result;

            if ((proc.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length < 1000)
                && (ffmpegErr.Contains("nvenc") || ffmpegErr.Contains("No NVENC") || ffmpegErr.Contains("Cannot load")))
            {
                LogToMainWindow("⚠ MultiClip: NVENC nedostupan, koristim libx264...");
                string argsCpu = $"-nostdin -f concat -safe 0 -i \"{concatList}\" " +
                                 $"-vf \"scale=1920:1080:flags=lanczos,format=yuv420p\" " +
                                 $"-c:v libx264 -preset fast -crf 23 -profile:v high -level 4.1 " +
                                 $"-an -y \"{outputPath}\"";
                var proc2 = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = argsCpu,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };
                proc2.Start();
                var so2 = proc2.StandardOutput.ReadToEndAsync();
                var se2 = proc2.StandardError.ReadToEndAsync();
                await Task.WhenAll(so2, se2);
                await proc2.WaitForExitAsync(ct);
                ffmpegErr = se2.Result;
                if (proc2.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000)
                {
                    LogToMainWindow($"✅ MultiClip spojen (libx264): {preparedPaths.Count} klipa → {targetDuration:F1}s");
                    return outputPath;
                }
            }

            if (proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000)
            {
                LogToMainWindow($"✅ MultiClip spojen: {preparedPaths.Count} klipa → {targetDuration:F1}s");
                return outputPath;
            }

            if (!string.IsNullOrEmpty(ffmpegErr))
            {
                var errLines = ffmpegErr.Split('\n');
                var realError = string.Join("\n", errLines
                    .Where(l => l.Contains("Error") || l.Contains("Invalid") || l.Contains("failed") || l.Contains("error") || l.Contains("moov"))
                    .Take(5));
                if (!string.IsNullOrEmpty(realError))
                    LogToMainWindow($"⚠ MultiClip FFmpeg greska: {realError}");
                else
                    LogToMainWindow($"⚠ MultiClip FFmpeg exit={proc.ExitCode}");
            }

            LogToMainWindow("⚠ MultiClip concat neuspjesan — koristim prvi klip");
            return clipPaths[0];
        }
        #endregion
        private string GetCurrentProjectFolder()
        {
            if (WpfApp.Current.MainWindow is MainWindow mainWin) return mainWin.GetCurrentProjectFolder();
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private void LogToMainWindow(string message)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (WpfApp.Current.MainWindow is MainWindow mainWin)
                        mainWin.LogMessage(message, true);
                });
            }
            catch { }
        }

        private void AnnounceToUser(string message, int progressPercent = -1)
        {
            Dispatcher.Invoke(() =>
            {
                txtOllamaStatus.Text = message;
                if (progressPercent >= 0)
                {
                    prgProgress.Value = progressPercent;
                    txtProgress.Text = $"{progressPercent}% - {message}";
                }
                var peer = UIElementAutomationPeer.FromElement(txtOllamaStatus);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            });
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) { _cts?.Cancel(); AnnounceToUser(L("cancelling_msg")); btnCancel.Content = L("cancelling_button"); btnCancel.IsEnabled = false; }
            else { DialogResult = false; Close(); }
        }
    }

    #region Helper Classes

    public class StoryScene
    {
        public int SceneNumber { get; set; }
        public string Description { get; set; }
        public string Emotion { get; set; }
        public int Energy { get; set; }
        public string Characters { get; set; }
        public string Action { get; set; }
        public string Location { get; set; }
        public string Keywords { get; set; }
        public string AmbientSound { get; set; }
        public double StartTime { get; set; }
        public double Duration { get; set; }
        public string AmbientPath { get; set; }
        public double VisionScore { get; set; } = 6.0;
        public bool IsStaticClip { get; set; } = false;
    }

    public class StoryBoard
    {
        public List<StoryScene> Scenes { get; set; }
        public string MainCharacter { get; set; }
        public string OverallTheme { get; set; }
    }

    public class TimelineSegment
    {
        public string Path { get; set; }
        public double Duration { get; set; }
        public double StartTime { get; set; }
        public string Description { get; set; }
        public string LyricText { get; set; }
        public string AmbientSoundPath { get; set; }
        public string VoiceNarrationPath { get; set; }
        public int Energy { get; set; }
        public string Emotion { get; set; }
        public string MoodTag { get; set; }
        public double VisionScore { get; set; } = 6.0;
        public bool IsStaticClip { get; set; } = false;
    }

    public class SongAnalysis
    {
        [Newtonsoft.Json.JsonProperty("context")]
        public string Context { get; set; }
        [Newtonsoft.Json.JsonProperty("mood")]
        public string Mood { get; set; }
        [Newtonsoft.Json.JsonProperty("theme")]
        public string Theme { get; set; }
        [Newtonsoft.Json.JsonProperty("visual_style")]
        public string VisualStyle { get; set; }
        [Newtonsoft.Json.JsonProperty("main_subject")]
        public string MainSubject { get; set; }
        [Newtonsoft.Json.JsonProperty("season")]
        public string Season { get; set; }
        [Newtonsoft.Json.JsonProperty("setting")]
        public string Setting { get; set; }
    }

    public class SceneKeywords
    {
        [Newtonsoft.Json.JsonProperty("line")]
        public int Line { get; set; }
        [Newtonsoft.Json.JsonProperty("keywords")]
        public string Keywords { get; set; }
        [Newtonsoft.Json.JsonProperty("ambient")]
        public string Ambient { get; set; }
    }

    #endregion

}