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
        // Timestamp-ovi iz Whisper transkripcije: indeks stiha → sekunde od pocetka
        private Dictionary<int, double> _lyricTimestamps = new Dictionary<int, double>();
        private List<TimelineSegment> _segments;
        private string _pixabayApiKey;
        private bool _showLyrics = false;

        // Javno polje — MainWindow čita ovo pri render pozivu
        public static bool FastRenderMode = true;
        private bool _enableTransitionSounds = true;
        private bool _enableAmbientSounds = true;
        private bool _enableVoiceNarration = false;
        private string _selectedTheme = "fun";
        private string _tempVideoFolder;
        private string _ambientAudioPath;
        private string _detectedMood = "neutral";
        private string _detectedContext = "";
        private FreesoundClient _freesound;
        private string _freesoundKey;

        // Keš za tranzicione zvukove - preuzimamo jednom, kopiramo za svaku scenu
        private readonly Dictionary<string, string> _transitionSoundCache = new Dictionary<string, string>();

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private CancellationTokenSource _cts;
        private bool _isRunning = false;
        private readonly SemaphoreSlim _apiRateLimiter = new SemaphoreSlim(1, 1);

        // Keš već preuzetih URL-ova — sprečava ponavljanje istog videa/slike
        private readonly HashSet<string> _usedMediaUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Brojač koliko puta je korišćen svaki query — za rotaciju rezultata
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
            ["fun"] = new List<string> { "children playing in park soft light warm colors", "kids running and laughing happy atmosphere", "children on swings warm colors" }
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
            txtOllamaStatus.Text = "🔍 Provjeravam Ollama...";
            txtOllamaStatus.Foreground = System.Windows.Media.Brushes.Orange;

            _ollama = new OllamaClient();
            _ollamaRunning = await _ollama.IsOllamaRunning();

            if (_ollamaRunning)
            {
                txtOllamaStatus.Text = "✅ Ollama je pokrenuta! AI će automatski generisati priču i ključne riječi.";
                txtOllamaStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                btnGenerate.IsEnabled = true;
            }
            else
            {
                txtOllamaStatus.Text = "❌ Ollama NIJE pokrenuta! AI neće raditi. Molimo pokrenite Ollama.";
                txtOllamaStatus.Foreground = System.Windows.Media.Brushes.Red;
                btnGenerate.IsEnabled = false;
            }

            InitFreesound();
            AutoPopulateSongInfo();
            txtLyrics.Focus();
        }

        private void AutoPopulateSongInfo()
        {
            // Auto-popunjava naslov i odjavni tekst iz naziva audio fajla na timeline-u
            var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            var audioItem = mainWindow.timelineItems.FirstOrDefault(i => i.Type == "Audio");
            if (audioItem == null || string.IsNullOrEmpty(audioItem.Path)) return;

            // Uzimamo naziv fajla bez ekstenzije
            // Npr. "Iskra - Pesma o muzici.mp3" → "Iskra - Pesma o muzici"
            string fileName = Path.GetFileNameWithoutExtension(audioItem.Path);

            // Popunjavamo naslov (txtIntroText) samo ako je prazno
            if (txtIntroText != null && string.IsNullOrWhiteSpace(txtIntroText.Text))
                txtIntroText.Text = fileName;

            // Popunjavamo odjavni tekst (txtOutroText) samo ako je prazno
            // Format: "naziv pesme" ili ako ima " - " onda "Izvođač - Naziv"
            if (txtOutroText != null && string.IsNullOrWhiteSpace(txtOutroText.Text))
            {
                // Pokušavamo da izvučemo izvođača i naziv ako postoji " - " separator
                if (fileName.Contains(" - "))
                {
                    var parts = fileName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    // Odjavni tekst: "Naziv - Izvođač" ili samo naziv
                    txtOutroText.Text = fileName;
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
            _freesoundKey = FreesoundClient.ReadKey();
            if (!string.IsNullOrEmpty(_freesoundKey))
            {
                _freesound = new FreesoundClient(_freesoundKey);
                LogToMainWindow("🔊 Freesound inicijalizovan — ambijentalni zvukovi su aktivni.");
            }
            else
            {
                LogToMainWindow("⚠️ Freesound API ključ nije podešen. Ambijentalni zvukovi neće biti dostupni.");
                _enableAmbientSounds = false;
                if (chkAmbientSounds != null) chkAmbientSounds.IsChecked = false;
                if (chkAmbientSounds != null) chkAmbientSounds.IsEnabled = false;
            }
        }

        private void PromptFreesoundKey()
        {
            var result = WpfMessageBox.Show(
                "Freesound API ključ nije podešen.\n\nFreesound.org je besplatan servis koji pruža visokokvalitetne ambijentalne zvukove.\n\nDa li želiš unijeti Freesound API ključ sada?",
                "🔊 Freesound ambijentalni zvukovi",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var dlg = new ApiKeyDialog("freesound", "Freesound API ključ\n\n1. Idi na freesound.org\n2. Registruj se (besplatno)\n3. Account → Edit Profile → API Applications\n4. Kreiraj novu aplikaciju i kopiraj API key");

                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.ApiKey))
                {
                    FreesoundClient.SaveKey(dlg.ApiKey);
                    _freesoundKey = dlg.ApiKey;
                    _freesound = new FreesoundClient(_freesoundKey);
                    LogToMainWindow("✅ Freesound API ključ sačuvan — ambijentalni zvukovi aktivni!");
                }
            }
        }

        private void btnBrowseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WpfOpenFileDialog { Filter = "Slike|*.png;*.jpg;*.jpeg;*.bmp" };
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
                stackPanel.Children.Add(new TextBlock { Text = "→ AI će generisati priču...", Width = 300, Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
                lstKeywords.Items.Add(stackPanel);
            }
            btnGenerate.IsEnabled = _lyricLines.Count > 0;
        }

        private async void btnAutoTranscribe_Click(object sender, RoutedEventArgs e)
        {
            // Pronalazi ucitani audio fajl iz projekta
            string audioPath = null;

            // Pokusaj da uzmes putanju iz glavnog prozora (selektovani audio klip)
            var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                // Trazi audio klip na timeline-u
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
                    Filter = "Audio/Video fajlovi|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac;*.mp4;*.avi;*.mkv;*.mov|Svi fajlovi|*.*"
                };
                if (dlg.ShowDialog() != true) return;
                audioPath = dlg.FileName;
            }

            if (!AITranscription.IsWhisperAvailable())
            {
                var msg = "Whisper nije instaliran na ovom racunaru." + Environment.NewLine + Environment.NewLine +
                          "Instaliraj ga na jedan od ova dva nacina:" + Environment.NewLine + Environment.NewLine +
                          "OPCIJA A - Python (preporuceno):" + Environment.NewLine +
                          "  pip install openai-whisper" + Environment.NewLine + Environment.NewLine +
                          "OPCIJA B - Standalone (bez Python-a):" + Environment.NewLine +
                          "  Preuzmi faster-whisper-xxl.exe" + Environment.NewLine +
                          "  i stavi ga pored UltraVideoEditor.exe" + Environment.NewLine + Environment.NewLine +
                          "Nakon instalacije, ponovo pokreni aplikaciju.";
                WpfMessageBox.Show(msg, "Whisper nije pronadjen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // UI - zakljucaj dugme, pokazi status
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
                    modelSize: "Large-v3",
                    progress: progress,
                    ct: _cts?.Token ?? CancellationToken.None);

                if (!result.Success)
                {
                    WpfMessageBox.Show(result.ErrorMessage, "Greška pri transkripciji",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Popuni txtLyrics sa prepoznatim tekstom
                txtLyrics.Text = AITranscription.FormatLyricsForTextBox(result.Lines);

                // Sacuvaj timestamp-ove za sinhronizaciju kadrova
                _lyricTimestamps.Clear();
                for (int i = 0; i < result.Lines.Count; i++)
                    _lyricTimestamps[i] = result.Lines[i].StartSeconds;

                if (txtTranscribeStatus != null)
                    txtTranscribeStatus.Text = $"✅ {result.Lines.Count} linija prepoznato";

                AnnounceToUser($"Transkripcija gotova. Prepoznato {result.Lines.Count} linija teksta.", 0);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Greška: {ex.Message}", "Greška", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (mainWindow == null) { WpfMessageBox.Show("MainWindow nije dostupan.", "Greška", MessageBoxButton.OK, MessageBoxImage.Error); return; }

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
                var result = WpfMessageBox.Show("Nema audio fajla na timeline-u.\n\nDa li želite da AI automatski generiše pozadinsku muziku?",
                    "🎵 AI Muzika", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    WpfMessageBox.Show("Dodajte audio fajl na timeline (Ctrl+Shift+I) prije kreiranja videa.",
                        "Upozorenje", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AnnounceToUser("AI analizira pjesmu...", 0);
                var tempStoryBoard = await GenerateStoryBoard(_lyricLines, _cts?.Token ?? CancellationToken.None);

                totalDuration = 180;

                AnnounceToUser("AI traži odgovarajuću muziku...", 5);
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
                    WpfMessageBox.Show("Nije moguće preuzeti AI muziku. Dodajte audio fajl ručno.",
                        "Greška", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                totalDuration = audioItem.Duration;
                if (totalDuration <= 0)
                {
                    WpfMessageBox.Show("Audio fajl ima nepoznato trajanje.", "Upozorenje", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _lyricLines = txtLyrics.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            // Ako nema teksta, generisemo automatske stihove na osnovu trajanja muzike
            // Svaki "stih" traje ~6-8 sekundi — kao da je instrumental
            if (_lyricLines.Count == 0)
            {
                int autoSceneCount = Math.Max(4, (int)(totalDuration / 7.0));
                _lyricLines = GenerateInstrumentalSceneDescriptions(autoSceneCount, totalDuration, _detectedContext, _detectedMood);
                AnnounceToUser($"Nema teksta — generisem {autoSceneCount} automatskih scena za instrumental.", 0);
            }

            _showLyrics = chkShowLyrics.IsChecked == true;
            _cts = new CancellationTokenSource();
            _isRunning = true;
            btnGenerate.IsEnabled = false;
            btnCancel.Content = "ZAUSTAVI";
            btnGenerate.Content = "🎬 AI kreira priču...";
            prgProgress.Visibility = Visibility.Visible;
            AnnounceToUser($"Audio trajanje: {FormatTime(totalDuration)} ({totalDuration:F1}s). Analiziram {_lyricLines.Count} stihova...", 0);

            try
            {
                await ProcessVideoCreation(audioItem.Path, totalDuration);
            }
            catch (OperationCanceledException) { AnnounceToUser("Operacija je otkazana."); }
            catch (Exception ex) { WpfMessageBox.Show($"Greska: {ex.Message}", "Greska", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { _isRunning = false; _cts?.Dispose(); _cts = null; btnGenerate.IsEnabled = true; btnGenerate.Content = "KREIRAJ VIDEO"; btnCancel.Content = "OTKAZI"; btnCancel.IsEnabled = true; prgProgress.Visibility = Visibility.Collapsed; txtProgress.Text = ""; }
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
                LogToMainWindow("🧠 AI analizira cjelokupnu pjesmu...");
                string response = await _ollama.GenerateAsync(prompt, ct: ct);
                string jsonStr = ExtractJson(response);
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    var analysis = Newtonsoft.Json.JsonConvert.DeserializeObject<SongAnalysis>(jsonStr);
                    if (analysis != null)
                    {
                        LogToMainWindow($"🎭 AI analiza: kontekst='{analysis.Context}', mood='{analysis.Mood}', tema='{analysis.Theme}'");
                        return analysis;
                    }
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow($"⚠️ AI analiza nije uspjela ({ex.Message}), koristim keyword detekciju...");
            }

            return FallbackSongAnalysis(lyrics);
        }

        private SongAnalysis FallbackSongAnalysis(List<string> lyrics)
        {
            string allText = string.Join(" ", lyrics).ToLower();
            var scores = new Dictionary<string, int> { ["music"] = 0, ["lullaby"] = 0, ["party"] = 0, ["love"] = 0, ["sad"] = 0, ["adventure"] = 0, ["nature"] = 0, ["dance"] = 0, ["school"] = 0, ["animal"] = 0, ["christmas"] = 0, ["outdoor"] = 0, ["seasons"] = 0, ["health"] = 0, ["fun"] = 1 };

            string[] musicW  = { "muzik", "melodij", "pesm", "pjesm", "svira", "instrument", "nota", "ritam", "zvuk", "gitara", "klavir", "bubanj", "violina", "flauta", "pevaj", "pjevaj", "muzičar", "koncert", "slušaj muzik", "blago muzik" };
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

            foreach (var w in musicW)   if (allText.Contains(w)) scores["music"] += 4;
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

            return new SongAnalysis
            {
                Context = ctx,
                Mood = mood,
                Theme = "Dječija pjesma",
                VisualStyle = "warm colors soft light natural outdoor",
                MainSubject = "children",
                Season = season,
                Setting = ctx == "outdoor" || ctx == "seasons" || ctx == "health" ? "outdoors" : "mixed"
            };
        }

        // MODIFICIRANA METODA: Agresivniji prompt za literalnu ilustraciju akcija
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

                // AGRESIVNI PROMPT sa fokusom na bukvalnu ilustraciju akcije, NE pejzaže!
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
                    AnnounceToUser($"AI analizira stihove {i + 1}-{Math.Min(i + batchSize, total)} (literal akcija)...", 5 + (i * 20 / total));
                    string response = await _ollama.GenerateAsync(prompt, ct: ct);
                    string jsonStr = ExtractJson(response, isArray: true);

                    if (!string.IsNullOrEmpty(jsonStr))
                    {
                        var batchResults = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SceneKeywords>>(jsonStr);
                        if (batchResults != null && batchResults.Count > 0)
                        {
                            // Validacija: ignoriši template tekst koji AI vrati bukvalno
                            foreach (var r in batchResults)
                            {
                                // Validacija — AI je vratio template ili previše generički sadržaj
                                bool isTemplate = string.IsNullOrEmpty(r.Keywords) ||
                                    r.Keywords.Contains("konkretna akcija") ||
                                    r.Keywords.Contains("subjekt + detalj") ||
                                    r.Keywords == "..." ||
                                    r.Keywords.Length < 5;

                                // Proveri da li je AI vratio srpski tekst koji je prošao nefiltriran
                                bool hasCyrillic = r.Keywords?.Any(c => c > 0x400 && c < 0x500) ?? false;
                                bool hasLatin    = r.Keywords?.Any(c => "šđčćžŠĐČĆŽ".Contains(c)) ?? false;

                                // Previše generički — isti kao što bi fallback vratio za bilo koji stih
                                var genericPhrases = new[] { "happy child", "child playing", "children playing", "warm colors", "soft light" };
                                bool isTooGeneric = genericPhrases.Any(p => r.Keywords?.ToLower() == p);

                                if (isTemplate || hasCyrillic || hasLatin || isTooGeneric)
                                {
                                    int lyricIdx = r.Line - batchStart - 1;
                                    if (lyricIdx >= 0 && lyricIdx < batch.Count)
                                    {
                                        r.Keywords = GenerateKeywordsFromLyric(batch[lyricIdx], analysis);
                                        r.Ambient  = InferAmbientFromLyric(batch[lyricIdx], analysis.Context);
                                        LogToMainWindow($"   ⚠ Stih {r.Line}: AI keywords neupotrebljivi, koristim lokalni fallback");
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
                    LogToMainWindow($"⚠️ Keywords batch greška: {ex.Message}");
                }

                // Fallback: lokalna detekcija akcije iz teksta
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

        /// <summary>
        /// Gradi čist engleski Pixabay query iz keywords-a scene.
        /// Uklanja sve srpske/bosanske reči, ćirilicu, specijalne karaktere.
        /// Rezultat je uvek kratak (max 5 reči) čist engleski string.
        /// </summary>
        /// <summary>
        /// Kad nema teksta pesme (instrumental), generiše listu opisnih "stihova"
        /// koji vode AI da bira raznovrsne, vizuelno zanimljive kadrove.
        /// Scena nikad ne sme biti prazna.
        /// </summary>
        private List<string> GenerateInstrumentalSceneDescriptions(int count, double totalDuration, string context = "fun", string mood = "happy", string visualStyle = "")
        {
            // Kontekstualni opisi prilagodeni tipu pesme
            // Svaki tip pesme ima svoje specificne kadrove za instrumentalne delove
            string[] templates;

            switch (context)
            {
                case "music":
                    templates = new[]
                    {
                        "music notes floating colorful background",
                        "child playing piano keys joyful",
                        "colorful sound waves music abstract",
                        "children singing together choir joyful",
                        "guitar strings vibrating music close up",
                        "music sheet notes colorful background",
                        "child conducting orchestra imagine",
                        "headphones music listening child happy",
                        "vinyl record spinning music retro",
                        "musical instruments colorful collection",
                        "child dancing to music joyful room",
                        "sound waves colorful abstract music",
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
                        "warm sunset couple family silhouette",
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

                default: // fun, outdoor, health, i sve ostalo
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
            // Rečnik srpski/bosanski → engleski za česte reči u dečijim pesmama
            var srToEn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Prevozna sredstva
                {"automobil","car"}, {"auto","car"}, {"kola","car"}, {"vozilo","car"},
                {"kamion","truck"}, {"bus","bus"}, {"autobus","bus"}, {"voz","train"},
                {"bicikl","bicycle"}, {"motor","motorcycle"}, {"avion","airplane"},
                {"brod","ship"}, {"čamac","boat"}, {"traktor","tractor"},

                // Porodica i ljudi
                {"mama","mother"}, {"tata","father"}, {"dete","child"}, {"djete","child"},
                {"deca","children"}, {"djeca","children"}, {"beba","baby"},
                {"baka","grandmother"}, {"deka","grandfather"}, {"sestra","sister"},
                {"brat","brother"}, {"porodica","family"}, {"prijatelj","friend"},
                {"drugar","friend"}, {"drugarica","friend"}, {"učitelj","teacher"},

                // Životinje
                {"pas","dog"}, {"mačka","cat"}, {"konj","horse"}, {"krava","cow"},
                {"ptica","bird"}, {"ptice","birds"}, {"leptir","butterfly"},
                {"riba","fish"}, {"zec","rabbit"}, {"medved","bear"}, {"medvjed","bear"},
                {"lav","lion"}, {"tigar","tiger"}, {"slon","elephant"}, {"majmun","monkey"},
                {"ovca","sheep"}, {"patka","duck"}, {"pile","chicken"}, {"svinja","pig"},

                // Priroda i mesta
                {"park","park"}, {"parkić","park"}, {"šuma","forest"}, {"suma","forest"},
                {"plaža","beach"}, {"more","sea"}, {"reka","river"}, {"rijeka","river"},
                {"planina","mountain"}, {"livada","meadow"}, {"bašta","garden"},
                {"dvorište","yard"}, {"ulica","street"}, {"grad","city"},
                {"škola","school"}, {"kuća","house"}, {"dom","home"},

                // Hrana i piće
                {"sladoled","ice cream"}, {"torta","cake"}, {"čokolada","chocolate"},
                {"jabuka","apple"}, {"banana","banana"}, {"hleb","bread"},
                {"čaj","tea"}, {"sok","juice"}, {"mleko","milk"},

                // Radnje
                {"trči","running"}, {"trčanje","running"}, {"šeta","walking"},
                {"šetaj","walking"}, {"šetnja","walking"}, {"skače","jumping"},
                {"igra","playing"}, {"pleše","dancing"}, {"peva","singing"},
                {"čita","reading"}, {"crta","drawing"}, {"slika","painting"},
                {"spava","sleeping"}, {"jede","eating"}, {"pije","drinking"},
                {"pliva","swimming"}, {"vozi","riding"}, {"nosi","carrying"},
                {"grli","hugging"}, {"smeje","laughing"}, {"plače","crying"},

                // Godišnja doba i vreme
                {"prolece","spring"}, {"proleće","spring"}, {"proljeće","spring"},
                {"leto","summer"}, {"ljeto","summer"}, {"jesen","autumn"},
                {"zima","winter"}, {"sneg","snow"}, {"snijeg","snow"},
                {"kiša","rain"}, {"sunce","sun"}, {"vetar","wind"}, {"vjetar","wind"},

                // Predmeti
                {"lopta","ball"}, {"igračka","toy"}, {"lutka","doll"},
                {"knjiga","book"}, {"olovka","pencil"}, {"torba","bag"},
                {"kapa","hat"}, {"čizme","boots"}, {"rukavice","gloves"},
                {"šal","scarf"}, {"kaput","coat"}, {"haljina","dress"},

                // Emocije i opisi
                {"sretan","happy"}, {"srečan","happy"}, {"vesel","joyful"},
                {"tužan","sad"}, {"ljut","angry"}, {"uplašen","scared"},
                {"zdravo","healthy"}, {"jako","strong"}, {"malo","little"},
                {"veliko","big"}, {"lepo","beautiful"}, {"lijepo","beautiful"},
            };

            // Spoji sve delove
            var parts = new[] { keywords, action, energyBoost, style }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToList();

            var englishWords = new List<string>();
            foreach (var word in parts)
            {
                // Ukloni specijalne karaktere, ostavi samo slova i crtice
                string clean = System.Text.RegularExpressions.Regex.Replace(word, @"[^a-zA-ZšđčćžŠĐČĆŽ\-]", "");
                if (string.IsNullOrEmpty(clean)) continue;

                // Prevedi ako postoji u rečniku
                if (srToEn.TryGetValue(clean, out string translated))
                {
                    if (!englishWords.Contains(translated))
                        englishWords.Add(translated);
                }
                else
                {
                    // Proveri da li je čisto engleski (samo ASCII slova)
                    bool isEnglish = clean.All(c => c < 128 && char.IsLetter(c));
                    if (isEnglish && !englishWords.Contains(clean.ToLower()))
                        englishWords.Add(clean.ToLower());
                    // Srpske reči bez prevoda se preskače — ne šaljemo ih na API
                }
            }

            // Pixabay query: max 4 konkretne reči (core), bez generičkih stilskih reči
            // Stilske reči (warm, colors, soft, light) razblažuju preciznost — izbacujemo ih iz querija
            var styleNoise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "warm","colors","colour","soft","light","happy","atmosphere","child","friendly",
                "bright","colorful","colourful","beautiful","nice","good","calm","peaceful",
                "cheerful","uplifting","gentle","cozy","cosy"
            };

            // Odvoji core reči od stilskih
            var coreWords  = englishWords.Where(w => !styleNoise.Contains(w)).Take(4).ToList();
            var styleWords = englishWords.Where(w =>  styleNoise.Contains(w)).Take(1).ToList();

            // Ako core prazan — uzmi sve
            if (coreWords.Count == 0)
                coreWords = englishWords.Take(4).ToList();

            // Dodaj jedan stilski hint samo ako ima manje od 3 core reči
            var finalWords = coreWords.Count < 3
                ? coreWords.Concat(styleWords).ToList()
                : coreWords;

            if (finalWords.Count == 0)
                return "child playing outdoor";

            return string.Join(" ", finalWords);
        }

        private string GenerateKeywordsFromLyric(string lyric, SongAnalysis analysis)
        {
            string lower = lyric.ToLower();
            string style = analysis.VisualStyle ?? "warm colors soft light";

            // ── PREVOZNA SREDSTVA ─────────────────────────────────────────
            if (lower.Contains("automobil") || lower.Contains(" auto") || lower.Contains("kola"))
                return $"car driving street";
            if (lower.Contains("bicikl")) return $"child riding bicycle";
            if (lower.Contains("kamion")) return $"truck driving road";
            if (lower.Contains("avion")) return $"airplane flying sky";
            if (lower.Contains("voz") || lower.Contains("vozić")) return $"train station";
            if (lower.Contains("brod") || lower.Contains("čamac")) return $"boat water sailing";
            if (lower.Contains("traktor")) return $"tractor farm field";

            // ── ŽIVOTINJE ─────────────────────────────────────────────────
            if (lower.Contains(" pas") || lower.Contains("psa") || lower.Contains("psić"))
                return $"dog playing happy";
            if (lower.Contains("mačka") || lower.Contains("maca")) return $"cat cute";
            if (lower.Contains("konj")) return $"horse running field";
            if (lower.Contains("krava")) return $"cow farm meadow";
            if (lower.Contains("leptir")) return $"butterfly flower garden";
            if (lower.Contains("riba")) return $"fish swimming water";
            if (lower.Contains("zec")) return $"rabbit cute";
            if (lower.Contains("medved") || lower.Contains("medvjed")) return $"bear forest";
            if (lower.Contains("ptic") || lower.Contains("vrabac") || lower.Contains("lastavic"))
                return $"birds flying sky";
            if (lower.Contains("lav")) return $"lion savanna";
            if (lower.Contains("slon")) return $"elephant nature";
            if (lower.Contains("patka")) return $"duck pond water";

            // ── PORODICA I LJUDI ──────────────────────────────────────────
            if (lower.Contains("mama") && lower.Contains("tata")) return $"family parents child walking";
            if (lower.Contains("mama")) return $"mother child hug";
            if (lower.Contains("tata")) return $"father child playing";
            if (lower.Contains("baka") || lower.Contains("deka")) return $"grandparents child park";
            if (lower.Contains("brat") || lower.Contains("sestra")) return $"siblings playing together";
            if (lower.Contains("drugar") || lower.Contains("prijatelj")) return $"children friends playing";
            if (lower.Contains("porodic")) return $"family outdoor together";
            if (lower.Contains("beba")) return $"baby cute happy";

            // ── HRANA I PIĆE ──────────────────────────────────────────────
            if (lower.Contains("sladoled")) return $"child eating ice cream summer";
            if (lower.Contains("čokolada")) return $"child drinking hot chocolate winter";
            if (lower.Contains("torta") || lower.Contains("kolač")) return $"birthday cake children celebrating";
            if (lower.Contains("jabuka")) return $"child eating apple healthy";
            if (lower.Contains("čaj")) return $"hot tea cup warm winter";
            if (lower.Contains("sok")) return $"child drinking juice";

            // ── AKTIVNOSTI ────────────────────────────────────────────────
            if (lower.Contains("trči") || lower.Contains("trčanje") || lower.Contains("juriš"))
                return $"children running active";
            if (lower.Contains("šetaj") || lower.Contains("šeta") || lower.Contains("šetnja") || lower.Contains("šeće"))
                return $"child walking park";
            if (lower.Contains("skoči") || lower.Contains("skači") || lower.Contains("skakanje"))
                return $"child jumping happy";
            if (lower.Contains("pleši") || lower.Contains("plešeš") || lower.Contains("ples"))
                return $"children dancing joyful";
            if (lower.Contains("peva") || lower.Contains("pjeva") || lower.Contains("pevaj"))
                return $"child singing music";
            if (lower.Contains("pliva") || lower.Contains("kupanje"))
                return $"child swimming pool water";
            if (lower.Contains("crta") || lower.Contains("slika") || lower.Contains("boji"))
                return $"child drawing painting art";
            if (lower.Contains("čita") || lower.Contains("knjig"))
                return $"child reading book";
            if (lower.Contains("igraj") || lower.Contains("igra") || lower.Contains("igraju"))
                return $"children playing joyful";
            if (lower.Contains("smej") || lower.Contains("smije") || lower.Contains("blistaj"))
                return $"children laughing happy faces";
            if (lower.Contains("spava") || lower.Contains("sanja"))
                return $"child sleeping peaceful";
            if (lower.Contains("grli") || lower.Contains("zagrli"))
                return $"children hugging friendship";
            if (lower.Contains("nosi") || lower.Contains("uzmi") || lower.Contains("drži"))
                return $"child carrying";
            if (lower.Contains("upoznaj") || lower.Contains("otkriva"))
                return $"child exploring discovering";

            // ── MESTA ─────────────────────────────────────────────────────
            if (lower.Contains("parkić") || lower.Contains(" park")) return $"child playing park playground";
            if (lower.Contains("plaža") || lower.Contains("more")) return $"child beach sea summer";
            if (lower.Contains("šuma") || lower.Contains("suma")) return $"children forest nature";
            if (lower.Contains("planina")) return $"mountain nature hiking";
            if (lower.Contains("škola")) return $"school children classroom";
            if (lower.Contains("kuća") || lower.Contains(" dom")) return $"home family cozy";
            if (lower.Contains("dvorišt")) return $"children backyard playing";
            if (lower.Contains("grad") || lower.Contains("ulica")) return $"city street children";

            // ── SEZONE ────────────────────────────────────────────────────
            if (lower.Contains("proleć") || lower.Contains("proljeć") || lower.Contains("cvet") || lower.Contains("cvijet"))
                return $"spring flowers blooming child playing";
            if (lower.Contains("jesen") || lower.Contains("opada")) return $"autumn leaves falling child";
            if (lower.Contains("zima") || lower.Contains("sneg") || lower.Contains("snijeg") || lower.Contains("mraz"))
                return $"winter snow child playing outdoor";
            if (lower.Contains("leto") || lower.Contains("ljeto") || lower.Contains("sunce") || lower.Contains("toplo"))
                return $"summer sunny child outdoor";

            // ── ZIMSKA OPREMA ─────────────────────────────────────────────
            if (lower.Contains("čizme") || lower.Contains("rukavice") || lower.Contains("skafander") ||
                lower.Contains("kapu") || lower.Contains("šal") || lower.Contains("kaput"))
                return $"child winter clothes snow outdoor";

            // ── MUZIKA I INSTRUMENTI ──────────────────────────────────────
            // Kombinovane specifičnosti — najpre najkonkretnije
            if (lower.Contains("gitara") || lower.Contains("gitaru"))
                return"child playing guitar music";
            if (lower.Contains("klavir") || lower.Contains("piano"))
                return"child playing piano keyboard";
            if (lower.Contains("bubanj") || lower.Contains("bubnjevi"))
                return"child playing drums percussion";
            if (lower.Contains("violina"))
                return"child playing violin music";
            if (lower.Contains("flaut"))
                return"child playing flute music";
            if (lower.Contains("svira") || lower.Contains("instrument"))
                return"child playing musical instrument";
            if (lower.Contains("pesm") || lower.Contains("pjesm"))
                return"child singing microphone stage";
            if (lower.Contains("slušaj") || lower.Contains("sluša") || lower.Contains("slusaj") || lower.Contains("slušati"))
                return"child listening headphones music";
            if (lower.Contains("bez muzike") || lower.Contains("živeti bez") || lower.Contains("ziveti bez"))
                return"music notes flying colorful";
            if (lower.Contains("muzik"))
                return"music concert stage children";

            // ── POSEBNE FRAZE ─────────────────────────────────────────────
            if (lower.Contains("blago") || lower.Contains("najveć") || lower.Contains("dragocen"))
                return"treasure gift golden child excited";
            if (lower.Contains("počni dan") || lower.Contains("pocni dan") || lower.Contains("jutro") || lower.Contains("zora") || lower.Contains("osvanu"))
                return"child morning waking up sunrise";
            if (lower.Contains("završi dan") || lower.Contains("zavrsi dan") || lower.Contains("veče") || lower.Contains("noć") || lower.Contains("laku noć"))
                return"child evening bedtime stars";
            if (lower.Contains("brza") || lower.Contains("zaigra") || lower.Contains("tancuj"))
                return"children dancing fast energetic";
            if (lower.Contains("lagana") || lower.Contains("zadrema") || lower.Contains("sporo") || lower.Contains("tiho"))
                return"child calm relaxing peaceful";
            if (lower.Contains("rast") || lower.Contains("sazrev") || lower.Contains("učiti") || lower.Contains("uciti") || lower.Contains("nauči"))
                return"child learning growing school";
            if (lower.Contains("osećanj") || lower.Contains("osjećanj") || lower.Contains("srce") || lower.Contains("duša"))
                return"child expressive emotional face";
            if (lower.Contains("uživanj") || lower.Contains("uzivanj") || lower.Contains("simbol") || lower.Contains("sreća") || lower.Contains("sreca"))
                return"child happiness joy celebration";
            if (lower.Contains("zajedno") || lower.Contains("svi") || lower.Contains("svi mi"))
                return"children group together happy";
            if (lower.Contains("priroda") || lower.Contains("sve oko"))
                return"nature outdoor children exploring";

            // ── OPŠTI FALLBACK SA AI KONTEKSTOM ──────────────────────────
            // Kada stih nema konkretnu sliku, koristimo AI analizu pesme
            if (!string.IsNullOrEmpty(analysis.Context) && !string.IsNullOrEmpty(analysis.Mood))
            {
                string moodVisual = analysis.Mood switch
                {
                    "happy"     => "happy joyful bright",
                    "calm"      => "peaceful calm serene",
                    "excited"   => "energetic vibrant colorful",
                    "playful"   => "playful fun children",
                    "joyful"    => "joyful celebration bright",
                    "energetic" => "active dynamic movement",
                    "upbeat"    => "cheerful bright uplifting",
                    _           => "happy bright colorful"
                };
                string contextVisual = analysis.Context switch
                {
                    "dance"   => "children dancing",
                    "lullaby" => "child peaceful quiet",
                    "party"   => "children celebrating",
                    "nature"  => "nature outdoor green",
                    "school"  => "children learning",
                    "health"  => "child active outdoor",
                    "animal"  => "animals cute",
                    _         => "child playing"
                };
                return $"{contextVisual} {moodVisual}";
            }

            // ── KONTEKSTUALNI FALLBACK ────────────────────────────────────
            return analysis.Context switch
            {
                "outdoor" => $"child outdoor activity playing",
                "dance"   => $"children dancing joyful",
                "lullaby" => $"child sleeping peaceful bedroom soft light",
                "party"   => $"child celebrating birthday party",
                "nature"  => $"children nature outdoor exploring",
                "health"  => $"child active healthy running outdoor",
                _         => $"child playing outdoor happy"
            };
        }

        private string InferAmbientFromLyric(string lyric, string context)
        {
            // PRINCIP: Literal sync — čitam stih i vraćam TAČAN zvuk koji odgovara.
            // Npr. "ptice pjevaju" → "birds chirping", "reka teče" → "stream flowing".
            // KONTEKSTUALNA KOREKCIJA: ako je pjesma vesela/dječija, "zimski" stihovi
            // dobijaju VESELE zimske zvukove (djeca u snijegu), ne zastrašujući vjetar.
            string lower = lyric.ToLower();
            bool isJoyfulContext = context is "outdoor" or "health" or "fun" or "party"
                                             or "dance" or "seasons" or "animal" or "school";

            // ══════════════════════════════════════════════════════════════
            // 1. NAJSPECIFIČNIJI ZVUCI — direktno iz teksta
            // ══════════════════════════════════════════════════════════════

            // Ptice — svako pominjanje
            if (lower.Contains("ptic") || lower.Contains("cvrkut") ||
                lower.Contains("pjev") || lower.Contains("lastavic") ||
                lower.Contains("vrabac") || lower.Contains("slavuj"))
                return "birds chirping";

            // Voda — reka, potok, bujica
            if (lower.Contains("reka") || lower.Contains("rijeka") ||
                lower.Contains("potok") || lower.Contains("bujica") ||
                lower.Contains("izvor") || lower.Contains("voda teč"))
                return "stream flowing";

            // More / plaža / talasi
            if (lower.Contains("more") || lower.Contains("plaža") ||
                lower.Contains("talas") || lower.Contains("obala") ||
                lower.Contains("brod") || lower.Contains("ocean"))
                return "ocean waves";

            // Vjetar / povjetarac
            if (lower.Contains("vetar") || lower.Contains("vjetar") ||
                lower.Contains("povjetarac") || lower.Contains("duva"))
                return isJoyfulContext ? "gentle breeze outdoor" : "wind outdoor";

            // Kiša
            if (lower.Contains("kiša") || lower.Contains("pada kiša") ||
                lower.Contains("kaplja") || lower.Contains("mokro"))
                return "gentle rain";

            // Grmljavina / oluja
            if (lower.Contains("grmljavina") || lower.Contains("oluja") ||
                lower.Contains("munja") || lower.Contains("grom"))
                return "thunder storm";

            // Šuma / drveće / lišće
            if (lower.Contains("šuma") || lower.Contains("suma") ||
                lower.Contains("drveć") || lower.Contains("grana") ||
                lower.Contains("lisće") || lower.Contains("lišće") ||
                lower.Contains("šušti"))
                return "forest birds nature";

            // ══════════════════════════════════════════════════════════════
            // 2. GODIŠNJA DOBA — s kontekstualnom korekcijom
            // ══════════════════════════════════════════════════════════════

            // Proljeće
            if (lower.Contains("proljeć") || lower.Contains("proleć") ||
                lower.Contains("cvijet") || lower.Contains("cvece") ||
                lower.Contains("bujanje") || lower.Contains("procvat"))
                return "birds chirping spring";

            // Ljeto / sunce / toplo
            if (lower.Contains("leto") || lower.Contains("ljeto") ||
                lower.Contains("sunce") || lower.Contains("toplo") ||
                lower.Contains("vrućina") || lower.Contains("cvrčci"))
                return "summer nature crickets";

            // Jesen / lišće opada
            if (lower.Contains("jesen") || lower.Contains("lišće pada") ||
                lower.Contains("opada") || lower.Contains("zlatno") ||
                lower.Contains("žuto lišće"))
                return "autumn leaves rustling";

            // Zima / snijeg — KOREKCIJA: vesele pjesme dobijaju zvukove djece u snijegu
            // ne zastrašujući zimski vjetar
            if (lower.Contains("zima") || lower.Contains("sneg") ||
                lower.Contains("snijeg") || lower.Contains("mraz") ||
                lower.Contains("led") || lower.Contains("mećava"))
            {
                return isJoyfulContext
                    ? "children playing snow"   // Djeca se igraju u snijegu
                    : "winter wind snow";        // Samo za tužne/mračne kontekste
            }

            // Specifična zimska odjeća — samo označava zimu, ne treba strašan vjetar
            if (lower.Contains("čizme") || lower.Contains("rukavice") ||
                lower.Contains("skafander") || lower.Contains("kapu") ||
                lower.Contains("šal") || lower.Contains("kaput"))
                return isJoyfulContext
                    ? "children playing snow"
                    : "winter wind snow";

            // ══════════════════════════════════════════════════════════════
            // 3. AKTIVNOSTI — zvukovi koji prate radnju
            // ══════════════════════════════════════════════════════════════

            // Trčanje / skakanje / aktivan sport
            if (lower.Contains("trči") || lower.Contains("trčanje") ||
                lower.Contains("skoči") || lower.Contains("skači") ||
                lower.Contains("blistaj") || lower.Contains("juri"))
                return "children playing outdoor";

            // Smijeh / radost / veselje
            if (lower.Contains("smej") || lower.Contains("smije") ||
                lower.Contains("smeh") || lower.Contains("veselo") ||
                lower.Contains("haha") || lower.Contains("raduj"))
                return "children laughing";

            // Šetnja / hodanje (tiho, opušteno)
            if (lower.Contains("šetaj") || lower.Contains("šetnja") ||
                lower.Contains("hodaj") || lower.Contains("korak") ||
                lower.Contains("prošetaj") || lower.Contains("idi"))
                return "park ambience footsteps";

            // Igranje (opšte)
            if (lower.Contains("igraj") || lower.Contains("igra ") ||
                lower.Contains("igrice") || lower.Contains("zabav"))
                return "children playing outdoor";

            // ══════════════════════════════════════════════════════════════
            // 4. MJESTA — zvuci okoline
            // ══════════════════════════════════════════════════════════════

            // Park / playground
            if (lower.Contains("park") || lower.Contains("parkić") ||
                lower.Contains("klackalica") || lower.Contains("ljuljaška") ||
                lower.Contains("tobogan") || lower.Contains("peskovnik"))
                return "park ambience birds";

            // Grad / ulica
            if (lower.Contains("grad") || lower.Contains("ulica") ||
                lower.Contains("sokak") || lower.Contains("centar"))
                return "city park ambience";

            // Kuća / dom / unutra
            if (lower.Contains("kuća") || lower.Contains("kuca") ||
                lower.Contains("dom") || lower.Contains("soba") ||
                lower.Contains("unutra") || lower.Contains("topla"))
                return "home warmth fireplace";

            // Planina / vis
            if (lower.Contains("planina") || lower.Contains("vrh") ||
                lower.Contains("klisura") || lower.Contains("pećina"))
                return "mountain wind birds";

            // ══════════════════════════════════════════════════════════════
            // 5. HRANA / PIĆE — zvuci koji asociraju na ugodan ambijent
            // ══════════════════════════════════════════════════════════════

            if (lower.Contains("sladoled"))
                return "summer park children";

            if (lower.Contains("čaj") || lower.Contains("kakao") ||
                lower.Contains("čokolada") || lower.Contains("topli napit"))
                return "home warmth fireplace";

            if (lower.Contains("torta") || lower.Contains("kolač") ||
                lower.Contains("slatkiš"))
                return "children birthday party";

            // ══════════════════════════════════════════════════════════════
            // 6. ŽIVOTINJE — direktni zvuci
            // ══════════════════════════════════════════════════════════════

            if (lower.Contains("pas") || lower.Contains("kučić") ||
                lower.Contains("štene"))
                return "dog playing outdoor";

            if (lower.Contains("maca") || lower.Contains("mačka") ||
                lower.Contains("mače"))
                return "cat purring";

            if (lower.Contains("konj") || lower.Contains("konjanik"))
                return "horse hooves";

            if (lower.Contains("pčela") || lower.Contains("leptir") ||
                lower.Contains("buba"))
                return "summer meadow insects";

            if (lower.Contains("zec") || lower.Contains("vjeverica"))
                return "forest animals gentle";

            // ══════════════════════════════════════════════════════════════
            // 7. PORODICA / EMOCIJE — topli ambijentalni zvuci
            // ══════════════════════════════════════════════════════════════

            if (lower.Contains("mama") || lower.Contains("majka") ||
                lower.Contains("tata") || lower.Contains("otac"))
                return "park ambience family";

            if (lower.Contains("baka") || lower.Contains("deka") ||
                lower.Contains("porodic"))
                return "home warmth gentle";

            if (lower.Contains("drugar") || lower.Contains("prijatelj") ||
                lower.Contains("zajedno") || lower.Contains("svi"))
                return "children group playing";

            // ══════════════════════════════════════════════════════════════
            // 8. SPAVANJE / NOĆ / MIROVANJE
            // VAŽNO: Kontekstualna korekcija — u veseloj dječijoj pjesmi "noći"
            // znači "jutro poslije noći" (ustaj, šetaj), ne noćna scena.
            // ══════════════════════════════════════════════════════════════

            if (lower.Contains("spavaj") || lower.Contains("zaspi") ||
                lower.Contains("laku noć") || lower.Contains("usni") ||
                lower.Contains("san ") || lower.Contains("sanjaj"))
                return isJoyfulContext ? "park birds outdoor" : "lullaby music box";

            // "noći" u kontekstu "poslije noći" / "jutro" → park zvuci za vesele pjesme
            if (lower.Contains("noć") || lower.Contains("zvijezd") ||
                lower.Contains("mesec") || lower.Contains("tišina"))
                return isJoyfulContext ? "morning birds outdoor" : "night crickets gentle";

            // ══════════════════════════════════════════════════════════════
            // 9. ZDRAVLJE / SPORT / AKTIVNOST
            // ══════════════════════════════════════════════════════════════

            if (lower.Contains("zdravo") || lower.Contains("zdravlje") ||
                lower.Contains("jako") || lower.Contains("snažno"))
                return "park ambience birds";

            // ══════════════════════════════════════════════════════════════
            // 10. FALLBACK PO KONTEKSTU PJESME
            // ══════════════════════════════════════════════════════════════
            return context switch
            {
                "lullaby"    => "lullaby music box",
                "outdoor"    => "park birds outdoor",
                "health"     => "park birds outdoor",
                "nature"     => "forest stream birds",
                "adventure"  => "forest birds outdoor",
                "sad"        => "gentle rain",
                "party"      => "children laughing party",
                "christmas"  => "home warmth fireplace",
                "animal"     => "forest animals nature",
                "seasons"    => "birds chirping nature",
                "dance"      => "children playing outdoor",
                "school"     => "children playground",
                "love"       => "park birds gentle",
                "fun"        => "park birds outdoor",
                _            => "birds chirping nature"
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

        private void DetectContextFromLyrics(List<string> lyrics)
        {
            string allText = string.Join(" ", lyrics).ToLower();
            var scores = new Dictionary<string, int> { ["lullaby"] = 0, ["party"] = 0, ["love"] = 0, ["sad"] = 0, ["adventure"] = 0, ["nature"] = 0, ["dance"] = 0, ["school"] = 0, ["animal"] = 0, ["christmas"] = 0, ["fun"] = 1 };

            string[] lullabyWords = { "spavaj", "usni", "sni", "laku noć", "uspavanka", "sleep", "lullaby", "goodnight", "moonlight" };
            string[] partyWords = { "sretan", "srećan", "rođendan", "baloni", "torta", "birthday", "party", "balloon", "cake", "celebrate" };
            string[] loveWords = { "volim", "ljubav", "srce", "draga", "dragi", "love", "heart", "kiss", "hug", "romance" };
            string[] sadWords = { "plačem", "suze", "tužan", "tuga", "cry", "tears", "sad", "alone", "lonely", "goodbye" };
            string[] adventureWords = { "istraži", "avantura", "planina", "šuma", "adventure", "explore", "mountain", "forest", "discover" };
            string[] natureWords = { "cvijet", "proljeće", "jesen", "zima", "ljeto", "priroda", "flower", "spring", "autumn", "winter", "summer", "nature" };
            string[] danceWords = { "pleši", "igraj", "ples", "ritam", "dance", "dancing", "rhythm", "music", "sing" };
            string[] schoolWords = { "škola", "učenje", "knjiga", "učitelj", "school", "learn", "book", "teacher", "class" };
            string[] animalWords = { "pas", "maca", "konj", "zec", "ptica", "dog", "cat", "horse", "bunny", "animal" };
            string[] xmasWords = { "božić", "nova godina", "snijeg", "jelka", "christmas", "santa", "snow", "holiday", "gift" };

            foreach (var w in lullabyWords) if (allText.Contains(w)) scores["lullaby"] += 3;
            foreach (var w in partyWords) if (allText.Contains(w)) scores["party"] += 3;
            foreach (var w in loveWords) if (allText.Contains(w)) scores["love"] += 3;
            foreach (var w in sadWords) if (allText.Contains(w)) scores["sad"] += 3;
            foreach (var w in adventureWords) if (allText.Contains(w)) scores["adventure"] += 2;
            foreach (var w in natureWords) if (allText.Contains(w)) scores["nature"] += 1;
            foreach (var w in danceWords) if (allText.Contains(w)) scores["dance"] += 2;
            foreach (var w in schoolWords) if (allText.Contains(w)) scores["school"] += 2;
            foreach (var w in animalWords) if (allText.Contains(w)) scores["animal"] += 2;
            foreach (var w in xmasWords) if (allText.Contains(w)) scores["christmas"] += 4;

            string detected = scores.OrderByDescending(kv => kv.Value).First().Key;
            _detectedContext = detected;

            _detectedMood = detected switch
            {
                "lullaby" => "calm",
                "sad" => "melancholy",
                "love" => "romantic",
                "party" => "excited",
                "adventure" => "energetic",
                "dance" => "upbeat",
                "christmas" => "joyful",
                "animal" => "playful",
                "school" => "curious",
                "nature" => "peaceful",
                _ => "happy"
            };

            if (_contextKeywords.TryGetValue(detected, out var contextList))
                _universalKeywords = new List<string>(contextList);

            LogToMainWindow($"🎭 Detektovan kontekst pjesme: '{detected}', raspoloženje: '{_detectedMood}'");
        }

        private async Task<StoryBoard> GenerateStoryBoard(List<string> lyrics, CancellationToken ct)
        {
            var analysis = await AnalyseSongWithAI(lyrics, ct);
            _detectedContext = analysis.Context ?? "fun";
            _detectedMood = analysis.Mood ?? "happy";

            if (_contextKeywords.TryGetValue(_detectedContext, out var ctxList))
                _universalKeywords = new List<string>(ctxList);

            var perLyricKeywords = await GenerateKeywordsPerLyric(lyrics, analysis, ct);

            var scenes = new List<StoryScene>();
            for (int i = 0; i < lyrics.Count; i++)
            {
                var kw = perLyricKeywords.FirstOrDefault(k => k.Line == i + 1);
                string keywords = kw?.Keywords ?? GenerateKeywordsFromLyric(lyrics[i], analysis);
                string ambient = kw?.Ambient ?? InferAmbientFromLyric(lyrics[i], analysis.Context);
                // Normalizuj AI ambient opis na naše mapiranje
                // AI može vratiti slobodan tekst ("zvuk kućanstva ili restorana") —
                // to mapiramo na konkretne Freesound querije
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

            // Kretanje
            if (lower.Contains("trči") || lower.Contains("trčanje") || lower.Contains("juri")) return "running";
            if (lower.Contains("šetaj") || lower.Contains("šeta") || lower.Contains("šetnja") || lower.Contains("šeće") || lower.Contains("hoda")) return "walking";
            if (lower.Contains("skoči") || lower.Contains("skači") || lower.Contains("skakanje") || lower.Contains("poskakuje")) return "jumping";
            if (lower.Contains("leti") || lower.Contains("leteći") || lower.Contains("lebdi")) return "flying";
            if (lower.Contains("pliva") || lower.Contains("kupanje") || lower.Contains("ronjen")) return "swimming";
            if (lower.Contains("vozi") || lower.Contains("bicikl")) return "riding bicycle";
            if (lower.Contains("pleši") || lower.Contains("plešeš") || lower.Contains("zaigraj") || lower.Contains("zaigra") || lower.Contains("brza") || lower.Contains("tancuj")) return "dancing";
            if (lower.Contains("penje") || lower.Contains("penjanje") || lower.Contains("penj")) return "climbing";

            // Aktivnosti s predmetima
            if (lower.Contains("peva") || lower.Contains("pjeva") || lower.Contains("pevaj") || lower.Contains("pjevaj") || lower.Contains("zapeva")) return "singing";
            if (lower.Contains("svira") || lower.Contains("sviranje")) return "playing instrument";
            if (lower.Contains("crta") || lower.Contains("slika") || lower.Contains("boji") || lower.Contains("pravi")) return "drawing painting";
            if (lower.Contains("čita") || lower.Contains("knjig")) return "reading book";
            if (lower.Contains("igraj") || lower.Contains("igra") || lower.Contains("igraju")) return "playing";
            if (lower.Contains("gradi") || lower.Contains("pravi") || lower.Contains("kocke")) return "building blocks";
            if (lower.Contains("lopta") || lower.Contains("šutiraj") || lower.Contains("baca")) return "playing ball";

            // Emocije/izrazi lica
            if (lower.Contains("smej") || lower.Contains("smije") || lower.Contains("smeh") || lower.Contains("smijeh") || lower.Contains("blistaj")) return "laughing";
            if (lower.Contains("plač") || lower.Contains("suza")) return "crying";
            if (lower.Contains("grli") || lower.Contains("zagrli") || lower.Contains("mazi")) return "hugging";
            if (lower.Contains("ljubi") || lower.Contains("polj")) return "kissing cheek";

            // Odmor / spavanje
            if (lower.Contains("spava") || lower.Contains("sanja") || lower.Contains("zaspi") || lower.Contains("zadrema") || lower.Contains("lagana")) return "sleeping";
            if (lower.Contains("odmara") || lower.Contains("sedi") || lower.Contains("sjedi") || lower.Contains("leži")) return "relaxing";
            if (lower.Contains("slušaj") || lower.Contains("sluša") || lower.Contains("slusaj")) return "listening";

            // Jelo / piće
            if (lower.Contains("jede") || lower.Contains("jedi") || lower.Contains("sladoled") || lower.Contains("torta")) return "eating";
            if (lower.Contains("pije") || lower.Contains("pij") || lower.Contains("sok") || lower.Contains("čaj")) return "drinking";

            // Istraživanje
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

                                byte[] data = await _httpClient.GetByteArrayAsync(audioUrl, ct);
                                await File.WriteAllBytesAsync(outputPath, data, ct);

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

            string args = $"-stream_loop -1 -i \"{audioPath}\" -t {targetDuration.ToString(CultureInfo.InvariantCulture)} -c copy -y \"{outputPath}\"";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo { FileName = ffmpegPath, Arguments = args, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true }
            };
            process.Start();
            await process.WaitForExitAsync();

            return File.Exists(outputPath) ? outputPath : audioPath;
        }

        private async Task<double> GetAudioDuration(string audioPath)
        {
            try
            {
                string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath)) return 180.0;

                string args = $"-i \"{audioPath}\" -f null - 2>&1";
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo { FileName = ffmpegPath, Arguments = args, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true }
                };
                process.Start();
                string output = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

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

        #region Audio Processing (SAMO Freesound, NEMA PIXABAY)

        private async Task<string> GetAmbientSoundPath(string soundType, string tempDir, CancellationToken ct)
        {
            LogToMainWindow($"🔊 GetAmbientSoundPath: soundType='{soundType}', context='{_detectedContext}', AmbientSoundsEnabled={_enableAmbientSounds}");

            if (!_enableAmbientSounds || string.IsNullOrEmpty(soundType) || soundType == "none")
            {
                LogToMainWindow($"🔊 Ambijentalni zvukovi isključeni ili soundType prazan");
                return null;
            }

            // 1. Provjeri lokalne fajlove
            string localPath = await GetLocalAmbientSound(soundType, tempDir);
            if (localPath != null)
            {
                LogToMainWindow($"✅ Lokalni ambijentalni zvuk: {localPath}");
                return localPath;
            }

            // 2. SAMO Freesound - NEMA PIXABAY FALLBACKA!
            if (_freesound != null)
            {
                try
                {
                    string freesoundQuery = BuildFreesoundQuery(soundType, _detectedContext);
                    LogToMainWindow($"🔊 Freesound pretraga: '{freesoundQuery}'");
                    string freesoundPath = await _freesound.GetAmbientSound(freesoundQuery, tempDir);
                    if (!string.IsNullOrEmpty(freesoundPath))
                    {
                        LogToMainWindow($"✅ Freesound zvuk preuzet: {freesoundQuery}");
                        return freesoundPath;
                    }
                    else
                    {
                        LogToMainWindow($"⚠️ Freesound: nema rezultata za '{freesoundQuery}' — {_freesound.LastError ?? "nepoznata greška"}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogToMainWindow($"⚠️ Freesound greška: {ex.Message}");
                }
            }
            else
            {
                LogToMainWindow($"⚠️ Freesound nije inicijalizovan. Ambijentalni zvukovi nedostupni.");
            }

            LogToMainWindow($"⚠️ Nema ambijentalnog zvuka za '{soundType}'");
            return null;
        }

        /// <summary>
        /// Normalizuje slobodni AI tekst za ambient zvuk na naše definirane tipove.
        /// AI može vratiti "zvuk kućanstva" ili "zvuk pješčane staze" —
        /// ovo mapira te opise na Freesound-ready tipove.
        /// Ako ne može mapirati, poziva InferAmbientFromLyric kao fallback.
        /// </summary>
        private string NormalizeAmbientFromAI(string aiAmbient, string lyric, string context)
        {
            if (string.IsNullOrWhiteSpace(aiAmbient))
                return InferAmbientFromLyric(lyric, context);

            string lower = aiAmbient.ToLower();

            // Ako je već naš mapirani tip (iz InferAmbientFromLyric), vrati direktno
            var knownTypes = new[] {
                "birds chirping", "stream flowing", "ocean waves", "gentle rain",
                "park ambience", "children playing", "home warmth", "lullaby music box",
                "morning birds", "night crickets", "forest birds", "summer nature",
                "children playing snow", "footsteps gravel", "park outdoor birds",
                "fireplace crackling", "indoor quiet birds", "children laughing",
                "children snow playing", "park birds"
            };
            if (knownTypes.Any(k => lower.Contains(k.Split(' ')[0])))
                return aiAmbient;

            // Mapiraj slobodni AI tekst na naše tipove
            // Zvuci prirode / ptica
            if (lower.Contains("ptic") || lower.Contains("šume") || lower.Contains("prirode") ||
                lower.Contains("drveć") || lower.Contains("šuma") || lower.Contains("grana"))
                return InferAmbientFromLyric(lyric, context);

            // Park / šetnja
            if (lower.Contains("park") || lower.Contains("šetanj") || lower.Contains("staza") ||
                lower.Contains("pješčan") || lower.Contains("koraka") || lower.Contains("hodanj"))
                return "park ambience footsteps";

            // Djeca / igranje
            if (lower.Contains("djec") || lower.Contains("djet") || lower.Contains("igre") ||
                lower.Contains("kretanj") || lower.Contains("trčanj"))
                return "children playing outdoor";

            // Zima / snijeg
            if (lower.Contains("zim") || lower.Contains("snijeg") || lower.Contains("sneg") ||
                lower.Contains("hlad") || lower.Contains("mraz"))
                return "children playing snow";

            // Kućanstvo / unutra / restoran
            if (lower.Contains("kuć") || lower.Contains("restoran") || lower.Contains("unutra") ||
                lower.Contains("topli") || lower.Contains("dom") || lower.Contains("soba"))
                return "home warmth gentle";

            // Ljeto / sladoled / sunce
            if (lower.Contains("ljeto") || lower.Contains("leto") || lower.Contains("sunce") ||
                lower.Contains("toplo") || lower.Contains("sladoled"))
                return "summer park children";

            // Porodica / mama / tata
            if (lower.Contains("mama") || lower.Contains("tata") || lower.Contains("baka") ||
                lower.Contains("deka") || lower.Contains("porodic") || lower.Contains("zajedno"))
                return "park ambience family";

            // Fallback: pozovi InferAmbientFromLyric koji čita stih direktno
            return InferAmbientFromLyric(lyric, context);
        }

        private string BuildFreesoundQuery(string soundType, string context)
        {
            if (string.IsNullOrEmpty(soundType)) soundType = "nature";
            string lower = soundType.ToLower();

            // ══════════════════════════════════════════════════════════════
            // DIREKTNO MAPIRANJE — svaki soundType iz InferAmbientFromLyric
            // PRAVILO: Queriji moraju vraćati FIELD RECORDINGS, ne muziku.
            // Kratki, precizni queriji (2-3 rijeci) bolje rade na Freesondu.
            // Izbjegavati: "cozy", "gentle", "ambient" — vraćaju muziku.
            // Koristiti: konkretne zvukove — "birds", "crickets", "footsteps".
            // ══════════════════════════════════════════════════════════════

            // Ptice
            if (lower.Contains("birds chirping spring"))   return "birds chirping spring";
            if (lower.Contains("morning birds"))           return "birds morning chirping";
            if (lower.Contains("birds chirping"))          return "birds chirping";
            if (lower.Contains("birds outdoor"))           return "birds outdoor";
            if (lower.Contains("birds"))                   return "birds nature";

            // Voda / potok
            if (lower.Contains("stream flowing"))          return "stream water flowing";
            if (lower.Contains("forest stream"))           return "forest stream water";
            if (lower.Contains("ocean waves"))             return "ocean waves";
            if (lower.Contains("rain"))                    return "rain outdoor";
            if (lower.Contains("thunder"))                 return "thunder rain storm";
            if (lower.Contains("stream") || lower.Contains("water")) return "stream water";

            // Vjetar
            if (lower.Contains("gentle breeze"))           return "breeze wind leaves";
            if (lower.Contains("mountain wind"))           return "mountain wind outdoor";
            if (lower.Contains("winter wind"))             return "wind snow winter";
            if (lower.Contains("wind"))                    return "wind outdoor";

            // Djeca — konkretni zvuci, ne "party ambient"
            if (lower.Contains("children playing snow"))   return "children snow playing";
            if (lower.Contains("children laughing"))       return "children laughing outdoor";
            if (lower.Contains("children group"))          return "children outdoor group";
            if (lower.Contains("children birthday"))       return "children birthday singing";
            if (lower.Contains("children playground"))     return "children playground";
            if (lower.Contains("children playing"))        return "children playing outdoor";

            // Park / outdoor — konkretni zvuci
            if (lower.Contains("park ambience footsteps")) return "footsteps gravel park";
            if (lower.Contains("park ambience family"))    return "park outdoor birds";
            if (lower.Contains("park ambience birds"))     return "park birds";
            if (lower.Contains("park ambience"))           return "park birds outdoor";
            if (lower.Contains("summer park children"))    return "summer park outdoor";
            if (lower.Contains("city park"))               return "city park birds";

            // Šuma / priroda
            if (lower.Contains("forest birds nature"))     return "forest birds";
            if (lower.Contains("forest animals"))          return "forest animals";
            if (lower.Contains("forest"))                  return "forest nature";

            // Godišnja doba
            if (lower.Contains("summer nature crickets"))  return "summer crickets";
            if (lower.Contains("summer meadow"))           return "meadow insects summer";
            if (lower.Contains("autumn leaves"))           return "leaves rustling wind";

            // Dom / toplina — OPASNO: "cozy indoor" vraća muziku na Freesondu!
            // Koristimo konkretne zvukove umjesto opisa raspoloženja.
            if (lower.Contains("home warmth fireplace"))   return "fireplace crackling";
            if (lower.Contains("home warmth gentle"))      return "indoor quiet birds";
            if (lower.Contains("home warmth"))             return "fireplace wood crackling";

            // Noć / tišina — OPASNO: "night ambient" vraća muziku!
            if (lower.Contains("lullaby music box"))       return "music box";
            if (lower.Contains("lullaby"))                 return "music box gentle";
            if (lower.Contains("night crickets"))          return "crickets night";
            if (lower.Contains("morning birds"))           return "birds morning";

            // Životinje — konkretni zvuci
            if (lower.Contains("dog playing"))             return "dog outdoor";
            if (lower.Contains("cat purring"))             return "cat purring";
            if (lower.Contains("horse hooves"))            return "horse hooves";
            if (lower.Contains("forest animals"))          return "forest animals";
            if (lower.Contains("animals"))                 return "animals outdoor";

            // ══════════════════════════════════════════════════════════════
            // KONTEKST FALLBACK — uvijek sigurni zvuci koji NE vraćaju muziku
            // ══════════════════════════════════════════════════════════════
            if (context == "lullaby")    return "music box";
            if (context == "party")      return "children laughing";
            if (context == "sad")        return "rain outdoor";
            if (context == "adventure")  return "forest birds";
            if (context == "christmas")  return "fireplace crackling";
            if (context == "outdoor")    return "park birds";
            if (context == "health")     return "birds outdoor";
            if (context == "nature")     return "forest stream";

            return "birds outdoor";
        }

        private async Task<string> GetLocalAmbientSound(string soundType, string tempDir)
        {
            // ── AUTO-SKENIRANJE Assets/Sounds/ ──────────────────────────────────────────
            // Biblioteka se automatski ažurira — dodaš fajl u folder, odmah se koristi.
            // Nema potrebe za izmjenama koda. Podržani formati: .mp3 .wav .flac .ogg .aiff
            //
            // Kako matching radi:
            //   soundType = "birds chirping spring"
            //   Traži fajl čije ime sadrži "bird" ILI "morning" ILI "chirp" ILI "spring"
            //   Uzima prvi match. Specifičniji soundType = bolji match.
            //
            // Semantička mapa: soundType ključne riječi → search tagovi po kojima skeniramo fajlove
            var semanticMap = new List<(string keyword, string[] fileTags)>
            {
                // soundType keyword          → tagovi koji se traže u nazivu fajla (OR logika)
                ("children playing snow",    new[] { "children-snow", "snow-children", "kids-snow", "children_snow" }),
                ("children snow",            new[] { "children-snow", "snow-children", "kids-snow", "children_snow" }),
                ("children playing",         new[] { "children-play", "kids-play", "playground", "children_play", "children-laugh" }),
                ("children laughing",        new[] { "children-laugh", "kids-laugh", "children_laugh" }),
                ("kids playing",             new[] { "children-play", "kids-play", "playground" }),
                ("children",                 new[] { "children", "kids", "child" }),

                ("birds chirping spring",    new[] { "morning-bird", "birds-morning", "birdsong", "morning-birdsong", "spring-bird", "morning-birds" }),
                ("morning birds",            new[] { "morning-bird", "birds-morning", "birdsong", "morning-birdsong" }),
                ("birds morning",            new[] { "morning-bird", "birds-morning", "birdsong" }),
                ("birds forest",             new[] { "forest-bird", "birds-forest", "forest-ambient", "spring-forest-bird", "sunny-forest" }),
                ("forest birds",             new[] { "forest-bird", "birds-forest", "forest-ambient", "sunny-forest" }),
                ("birds chirping",           new[] { "bird", "chirp", "birdsong", "tweeting", "sparrow", "swallow" }),
                ("birds outdoor",            new[] { "bird", "park-bird", "outdoor-bird", "urban-bird" }),
                ("birds nature",             new[] { "bird", "forest-bird", "nature-bird" }),
                ("birds",                    new[] { "bird", "chirp", "sparrow", "birdsong", "hawk", "pigeon", "duck", "swallow", "tweeting" }),

                ("park ambience",            new[] { "city-park", "park-ambience", "park-ambient", "citypark", "park-pond", "downtown-park" }),
                ("park birds",               new[] { "city-park", "park-bird", "park-ambience" }),
                ("park outdoor",             new[] { "city-park", "park-ambience", "outdoor" }),
                ("park ambience family",     new[] { "city-park", "park-ambience", "park-soccer", "public-park" }),
                ("park ambience footsteps",  new[] { "footstep", "gravel", "city-park", "park-ambience" }),
                ("outdoor",                  new[] { "city-park", "park-ambience", "garden", "outdoor", "pasture" }),

                ("fireplace",                new[] { "fireplace", "fire-crackl", "campfire", "hearth" }),
                ("fire crackling",           new[] { "fireplace", "fire-crackl", "campfire" }),
                ("home warmth",              new[] { "fireplace", "indoor", "home", "cozy" }),
                ("home warmth gentle",       new[] { "fireplace", "indoor", "garden", "home" }),
                ("indoor warmth",            new[] { "fireplace", "indoor" }),
                ("christmas",                new[] { "fireplace", "christmas", "winter-indoor" }),

                ("stream flowing",           new[] { "stream", "creek", "brook", "river", "water-flow" }),
                ("forest stream",            new[] { "stream", "creek", "forest-water", "brook" }),
                ("stream water",             new[] { "stream", "creek", "brook", "pond", "river" }),
                ("water stream",             new[] { "stream", "creek", "pond", "water" }),
                ("stream",                   new[] { "stream", "creek", "brook", "river", "pond" }),
                ("water",                    new[] { "stream", "creek", "pond", "ocean", "lake", "water" }),

                ("gentle rain",              new[] { "rain", "rainfall", "rainandrumble", "rain-rumble" }),
                ("soft rain",                new[] { "rain", "rainfall", "drizzle" }),
                ("rain outdoor",             new[] { "rain", "rainfall" }),
                ("rain",                     new[] { "rain", "rainfall", "rainandrumble" }),
                ("sad",                      new[] { "rain", "rainfall", "wind" }),

                ("summer nature crickets",   new[] { "cricket", "insect", "cicada", "summer-insect" }),
                ("night crickets",           new[] { "cricket", "insect", "night-insect", "cicada" }),
                ("summer crickets",          new[] { "cricket", "insect", "cicada" }),
                ("crickets",                 new[] { "cricket", "insect", "cicada" }),
                ("summer nature",            new[] { "cricket", "summer", "insect", "sunny-forest", "garden" }),

                ("footsteps gravel",         new[] { "footstep", "gravel", "walking-gravel", "steps-gravel" }),
                ("gravel footsteps",         new[] { "footstep", "gravel", "steps" }),
                ("footsteps",                new[] { "footstep", "step", "walking", "gravel" }),

                ("ocean waves",              new[] { "ocean", "wave", "sea", "oceanwave" }),
                ("ocean",                    new[] { "ocean", "wave", "sea" }),
                ("wind",                     new[] { "wind", "windy", "breeze" }),
                ("gentle wind",              new[] { "wind", "windy", "breeze", "gentle-wind" }),

                ("forest ambience",          new[] { "forest", "forestsurround", "forest-ambient", "sunny-forest", "forest-ambient" }),
                ("forest",                   new[] { "forest", "forestsurround", "woodland", "sunny-forest" }),
                ("nature",                   new[] { "forest", "garden", "nature", "outdoor", "bird", "park" }),
                ("mountain",                 new[] { "forest", "wind", "mountain", "outdoor" }),

                ("lullaby",                  new[] { "rain", "gentle", "soft", "calm" }),
                ("sleep",                    new[] { "rain", "gentle", "calm", "night" }),
                ("night",                    new[] { "cricket", "insect", "night", "evening" }),
                ("city",                     new[] { "city-park", "citypark", "urban", "cityhum" }),
            };

            string lower = soundType?.ToLower() ?? string.Empty;
            string soundsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds");

            if (!Directory.Exists(soundsDir))
            {
                LogToMainWindow($"⚠️ Assets/Sounds/ folder nije pronađen");
                return null;
            }

            // Učitaj sve fajlove jednom (cache unutar poziva)
            var supportedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac", ".ogg", ".aiff" };
            var allFiles = Directory.GetFiles(soundsDir)
                .Where(f => supportedExt.Contains(Path.GetExtension(f)))
                .ToList();

            foreach (var (keyword, fileTags) in semanticMap)
            {
                if (!lower.Contains(keyword)) continue;

                // Traži fajl čije ime sadrži bilo koji od tagova (case-insensitive)
                foreach (string tag in fileTags)
                {
                    string found = allFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f)
                            .ToLower()
                            .Replace("_", "-")
                            .Contains(tag.ToLower()));

                    if (found != null)
                    {
                        string ext = Path.GetExtension(found);
                        string outputPath = Path.Combine(tempDir, $"ambient_{Guid.NewGuid().ToString().Substring(0, 8)}{ext}");
                        await Task.Run(() => File.Copy(found, outputPath, true));
                        LogToMainWindow($"🔊 Lokalni zvuk: '{Path.GetFileName(found)}' za '{soundType}'");
                        return outputPath;
                    }
                }
            }

            // Nijedan lokalni fajl nije pronađen → Freesound fallback
            LogToMainWindow($"🔊 Nema lokalnog zvuka za '{soundType}', koristim Freesound fallback");
            return null;
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
                "lullaby" => 0.08,
                "sad" => 0.10,
                "party" => 0.20,
                "dance" => 0.20,
                "adventure" => 0.18,
                "christmas" => 0.16,
                "love" => 0.12,
                "nature" => 0.14,
                _ => 0.15
            };

            LogToMainWindow($"🔊 Miksiram ambijentalne zvukove sa muzikom ({scenes.Count(s => !string.IsNullOrEmpty(s.AmbientPath))} zvukova, volumen={ambientVolume:F2})");

            var filterParts = new List<string>();
            var inputs = new List<string>();

            inputs.Add($"-i \"{musicPath}\"");
            filterParts.Add($"[0:a]volume=1.0[a0]");

            int ambientIndex = 1;
            for (int i = 0; i < scenes.Count; i++)
            {
                if (!string.IsNullOrEmpty(scenes[i].AmbientPath) && File.Exists(scenes[i].AmbientPath))
                {
                    inputs.Add($"-i \"{scenes[i].AmbientPath}\"");
                    double startTime = scenes[i].StartTime;
                    double duration  = scenes[i].Duration;
                    string startMs   = ((long)(startTime * 1000)).ToString();
                    double endTime   = startTime + duration;

                    // ISPRAVAN REDOSLED:
                    // 1. aloop — loopuj kratki zvuk da popuni cijelu scenu
                    // 2. adelay — pomjeri na tačnu poziciju u timeline-u
                    // 3. atrim=end — odsijeci na kraj scene (apsolutno vrijeme)
                    // 4. volume — primijeni glasnoću
                    filterParts.Add(
                        $"[{ambientIndex}:a]" +
                        $"aloop=loop=-1:size=2e+09," +
                        $"adelay={startMs}|{startMs}," +
                        $"atrim=end={endTime.ToString("F3", CultureInfo.InvariantCulture)}," +
                        $"volume={ambientVolume.ToString("F2", CultureInfo.InvariantCulture)}" +
                        $"[a{ambientIndex}]");
                    ambientIndex++;
                    LogToMainWindow($"🔊 Dodajem zvuk za scenu {i + 1}: start={startTime:F1}s, duration={duration:F1}s");
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

            // normalize=0 je KRITIČNO — bez toga amix dijeli svaki input s brojem inputa
            // (20 inputa = svaki na 5% volumena, muzika postaje nečujna)
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
            // OBAVEZNO - bez ovoga FFmpeg deadlockuje na 90% kada stderr buffer postane pun
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // Timeout 120s - ako ne završi, odustajemo (ne visi zauvijek)
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
                LogToMainWindow($"✅ Ambijentalni zvukovi uspješno miksnani");
                return outputPath;
            }

            return musicPath;
        }

        #endregion

        #region Transition Sounds

        private async Task<string> GetTransitionSound(string tempDir, string type = "pop")
        {
            if (!_enableTransitionSounds) return null;

            // Ako već imamo taj tip u kešu, samo kopiraj
            if (_transitionSoundCache.TryGetValue(type, out string cachedSource) && File.Exists(cachedSource))
            {
                string copyPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");
                File.Copy(cachedSource, copyPath, true);
                return copyPath;
            }

            // Pokušaj lokalni fajl prvo (Assets/Sounds/)
            string soundFile = type == "pop" ? "transition-pop.mp3" : "transition-whoosh.mp3";
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds", soundFile);
            if (File.Exists(localPath))
            {
                string outputPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");
                File.Copy(localPath, outputPath, true);
                _transitionSoundCache[type] = outputPath;
                return outputPath;
            }

            // Generiši zvuk programski pomoću NAudio — bez FFmpeg, bez interneta
            // Rezultat: profesionalan UI zvuk s pravim ADSR envelope-om
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

        /// <summary>
        /// Generiše suptilan UI "pop" zvuk pomoću NAudio.
        /// Zvuk: kratki niskofrekventni klik s mekim ADSR envelope-om.
        /// Namijenjen da bude jedva čujan ispod muzike — kao elegantna tranzicija.
        /// </summary>
        private bool GeneratePopSound(string outputPath)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            // Ukupno trajanje: 180ms — dovoljno kratko da ne ometa muziku
            double totalSeconds = 0.18;
            int totalSamples = (int)(sampleRate * totalSeconds);

            var samples = new float[totalSamples * channels];
            var rng = new Random(42);

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double tNorm = t / totalSeconds; // 0..1

                // Envelope: brzi attack (0-5ms), eksponencijalni decay (5-180ms)
                // Rezultat: kratki klik koji se odmah gasi, ne "bip" koji zvoni
                double envelope;
                if (t < 0.005)
                    envelope = t / 0.005; // Attack: 0→1 za 5ms
                else
                    envelope = Math.Exp(-18.0 * (t - 0.005)); // Decay: eksponencijalni pad

                // Zvuk: niskofrekventni bump (80Hz osnova) + kratki šum za "klik" teksturu
                // 80Hz daje "mekoću", šum daje "realnost" zvuka
                double fundamental = Math.Sin(2 * Math.PI * 80 * t) * 0.6;
                double harmonic    = Math.Sin(2 * Math.PI * 160 * t) * 0.25;
                double click       = (rng.NextDouble() * 2 - 1) * Math.Exp(-120 * t) * 0.3;

                // Ukupan signal s envelope-om, skaliran na tiho (0.15 amplitude)
                // Tiho je namjerno — pop treba biti suptilan, ne agresivan
                double signal = (fundamental + harmonic + click) * envelope * 0.15;

                float sample = (float)Math.Clamp(signal, -1.0, 1.0);
                samples[i * channels]     = sample; // L
                samples[i * channels + 1] = sample; // R
            }

            return WriteWav(outputPath, samples, sampleRate, channels);
        }

        /// <summary>
        /// Generiše suptilan "whoosh" zvuk (šuštanje zraka) pomoću NAudio.
        /// Zvuk: filtrirani bijeli šum s fade-in/fade-out, 450ms.
        /// </summary>
        private bool GenerateWhooshSound(string outputPath)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            double totalSeconds = 0.45;
            int totalSamples = (int)(sampleRate * totalSeconds);

            var samples = new float[totalSamples * channels];
            var rng = new Random(42);

            // Jednostavan IIR lowpass filter za "šuštanje" (ne oštar šum)
            double filterState = 0;
            const double filterCoeff = 0.12; // Propušta ~500Hz i ispod

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double tNorm = t / totalSeconds;

                // Envelope: blagi fade-in (0-80ms), pik na 40%, blagi fade-out (60-100%)
                double envelope;
                if (tNorm < 0.18)
                    envelope = tNorm / 0.18;
                else if (tNorm < 0.60)
                    envelope = 1.0;
                else
                    envelope = 1.0 - (tNorm - 0.60) / 0.40;

                // Filtrirani bijeli šum → mekši whoosh
                double noise = rng.NextDouble() * 2 - 1;
                filterState = filterState * (1 - filterCoeff) + noise * filterCoeff;

                // Suptilno: 0.12 amplitude — jedva čujno ispod muzike
                double signal = filterState * envelope * 0.12;

                float sample = (float)Math.Clamp(signal, -1.0, 1.0);
                samples[i * channels]     = sample;
                samples[i * channels + 1] = sample;
            }

            return WriteWav(outputPath, samples, sampleRate, channels);
        }

        /// <summary>Piše float[] samples u WAV fajl bez eksternih zavisnosti.</summary>
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
            btnGenerate.Content = "🎬 AI kreira priču...";
            AnnounceToUser("AI analizira pjesmu i kreira priču...", 5);

            var storyBoard = await GenerateStoryBoard(_lyricLines, _cts?.Token ?? CancellationToken.None);

            if (storyBoard?.Scenes == null || storyBoard.Scenes.Count == 0)
            {
                LogToMainWindow("❌ Story board nije generisan, koristim fallback");
                storyBoard = CreateFallbackStoryBoard();
            }

            LogToMainWindow($"📖 Priča kreirana: {storyBoard.Scenes.Count} scena");
            LogToMainWindow($"👤 Glavni lik: {storyBoard.MainCharacter}");
            LogToMainWindow($"🎬 Tema: {storyBoard.OverallTheme}");

            _tempVideoFolder = Path.Combine(Path.GetTempPath(), $"UVE_Story_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempVideoFolder);

            _segments = new List<TimelineSegment>();

            // Resetuj deduplication keševe — svaka nova sesija generisanja počinje čisto
            _usedMediaUrls.Clear();
            _queryUseCount.Clear();

            // ========== LOGIKA ZA INSTRUMENTALNE DIJELOVE (B-roll) ==========
            int sceneCount = storyBoard.Scenes.Count;
            double estimatedSegDur = sceneCount > 0
                ? Math.Round(totalDuration / sceneCount, 2)
                : 0;
            double lyricsTotalDuration = sceneCount > 0
                ? Math.Round(estimatedSegDur * sceneCount, 2)
                : 0;
            double remainingDuration = totalDuration - lyricsTotalDuration;
            double originalTotalDuration = totalDuration;

            // Inicijalizuj StartTime i Duration za sve scene OVDE —
            // pre B-roll logike koja pomera StartTime sa += shift.
            // Whisper timestamp-ovi imaju prioritet ako postoje.
            for (int si = 0; si < storyBoard.Scenes.Count; si++)
            {
                var sc = storyBoard.Scenes[si];
                if (_lyricTimestamps.Count > 0 && _lyricTimestamps.ContainsKey(si))
                {
                    sc.StartTime = _lyricTimestamps[si];
                    double nxt = _lyricTimestamps.ContainsKey(si + 1)
                        ? _lyricTimestamps[si + 1]
                        : totalDuration;
                    sc.Duration = Math.Max(1.0, Math.Round(nxt - sc.StartTime, 2));
                }
                else
                {
                    sc.StartTime = Math.Round(si * estimatedSegDur, 2);
                    sc.Duration = (si == storyBoard.Scenes.Count - 1)
                        ? Math.Max(1.0, Math.Round(totalDuration - sc.StartTime, 2))
                        : Math.Round(estimatedSegDur, 2);
                }
            }

            // Instrumentalni dio postoji samo ako je značajan (>5% trajanja)
            if (remainingDuration > totalDuration * 0.05 && remainingDuration > 2.0)
            {
                LogToMainWindow($"🎵 Detektovan instrumentalni dio: {FormatTime(remainingDuration)} (ukupno trajanje: {FormatTime(totalDuration)}, stihovi: {FormatTime(lyricsTotalDuration)})");

                // B-roll keywords za instrumentalne dijelove, prilagođeni kontekstu pjesme
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
                            "children moving joyful silhouette dancing",
                            "party lights animation colorful rhythm",
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
                            "sunset children silhouette park golden hour",
                            "morning dew nature close-up cinematic beautiful",
                            "rainbow sky colorful nature children wonder",
                        });
                        break;
                    case "music":
                        // Pesma o muzici — B-roll pokazuje instrumente, note, koncert
                        bRollKeywords.AddRange(new[] {
                            "music notes colorful flying animation",
                            "children playing instruments school music class",
                            "concert stage lights music performance",
                            "piano keyboard hands playing music close-up",
                            "headphones music listening child happy",
                            "musical notes staff sheet music close-up",
                            "guitar strings strumming music close-up"
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
                    default: // fun / general — koristimo temu pesme ako postoji
                        // Ako AI detektuje temu, koristimo je za B-roll
                        if (!string.IsNullOrEmpty(storyBoard?.OverallTheme))
                        {
                            string themeLower = storyBoard.OverallTheme.ToLower();
                            // Izvuci engleske ključne reči iz srpske teme
                            if (themeLower.Contains("muzik") || themeLower.Contains("pesm"))
                                bRollKeywords.AddRange(new[] { "music concert children stage", "musical notes colorful animation", "children singing together group" });
                            else if (themeLower.Contains("prirod") || themeLower.Contains("šum"))
                                bRollKeywords.AddRange(new[] { "nature outdoor children exploring", "forest path sunlight children", "meadow flowers wind children" });
                            else if (themeLower.Contains("porodic") || themeLower.Contains("ljubav"))
                                bRollKeywords.AddRange(new[] { "family together outdoor happy warm", "parents children hugging love", "home family cozy warm happy" });
                        }
                        // Dodaj generičke ako lista ostane prazna
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

                // Izračunaj koliko B-roll kadrova treba (svaki ~3-5 sekundi)
                double bRollDurationPerClip = 4.0;
                int bRollCount = Math.Max(1, (int)Math.Ceiling(remainingDuration / bRollDurationPerClip));
                double actualDurationPerClip = remainingDuration / bRollCount;

                LogToMainWindow($"🎬 Kreiranje {bRollCount} B-roll kadrova za instrumentalni dio (po {actualDurationPerClip:F1}s)");

                // B-roll za UVOD (prije prve scene) - do 40% instrumentalnog dijela, max 10 sekundi
                double introPortion = Math.Min(remainingDuration * 0.4, 10.0);
                int introClips = Math.Max(1, (int)Math.Ceiling(introPortion / actualDurationPerClip));
                double introClipDuration = introPortion / introClips;

                double bRollStartTime = 0;
                string bRollMediaType = (cmbMediaType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "video";

                // ---- Uvodni B-roll ----
                // Lista privremenih segmenata za uvod — ubacujemo ih u _segments na kraju (Insert na poziciju 0 u obrnutom redosledu)
                var introSegments = new List<TimelineSegment>();
                for (int i = 0; i < introClips; i++)
                {
                    string kw = bRollKeywords[i % bRollKeywords.Count];
                    string styleEnhance = _detectedContext switch
                    {
                        "lullaby"   => "soft light peaceful calm",
                        "party"     => "colorful joyful energetic",
                        "sad"       => "gentle melancholy quiet",
                        "adventure" => "exciting dynamic outdoor",
                        "dance"     => "rhythmic colorful movement",
                        "christmas" => "warm cozy magical",
                        "nature"    => "peaceful natural soft",
                        _           => "warm colors happy cheerful"
                    };
                    string enhancedQuery = $"{kw} {styleEnhance}";

                    AnnounceToUser($"B-roll uvod {i + 1}/{introClips}...", 5);
                    string bMediaPath = await SearchAndDownloadMedia(enhancedQuery, 1080, bRollMediaType, _cts?.Token ?? CancellationToken.None);

                    // Ako nije pronađen — probaj sledeći keyword iz liste
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

                // Ubaci uvod na početak _segments (u ispravnom redosledu)
                for (int i = introSegments.Count - 1; i >= 0; i--)
                    _segments.Insert(0, introSegments[i]);

                // Pomjeri sve scene storyboard-a za trajanje uvoda
                double shift = introPortion;
                foreach (var scene in storyBoard.Scenes)
                    scene.StartTime += shift;

                // ---- Outro B-roll ----
                double outroPortion = remainingDuration - introPortion;
                if (outroPortion > 0.5)
                {
                    int outroClips = Math.Max(1, (int)Math.Ceiling(outroPortion / actualDurationPerClip));
                    double outroClipDuration = outroPortion / outroClips;
                    double outroStartTime = lyricsTotalDuration + introPortion;

                    for (int i = 0; i < outroClips; i++)
                    {
                        string kw = bRollKeywords[(introClips + i) % bRollKeywords.Count];
                        string styleEnhance = _detectedContext switch
                        {
                            "lullaby"   => "soft light peaceful calm ending",
                            "party"     => "colorful joyful fading out",
                            "sad"       => "gentle melancholy quiet fade",
                            "adventure" => "exciting dynamic conclusion",
                            "dance"     => "rhythmic colorful fade",
                            "christmas" => "warm cozy magical ending",
                            "nature"    => "peaceful natural soft conclusion",
                            _           => "warm colors happy cheerful ending"
                        };
                        string enhancedQuery = $"{kw} {styleEnhance}";

                        AnnounceToUser($"B-roll outro {i + 1}/{outroClips}...", 92);
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

                // Resetuj ukupno trajanje za prikaz
                totalDuration = introPortion + lyricsTotalDuration + outroPortion;
                LogToMainWindow($"📊 Nakon dodavanja B-roll: uvod={introPortion:F1}s, stihovi={lyricsTotalDuration:F1}s, outro={outroPortion:F1}s, UKUPNO={totalDuration:F1}s");
            }
            // ========== KRAJ B-roll LOGIKE ==========

            try
            {
                string mediaType = (cmbMediaType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "video";
                int count = storyBoard.Scenes.Count;
                // segDuration je sada izračunat u bloku pre B-roll logike (estimatedSegDur)
                for (int i = 0; i < count; i++)
                {
                    _cts?.Token.ThrowIfCancellationRequested();
                    var scene = storyBoard.Scenes[i];

                    // StartTime i Duration su inicijalizovani pre B-roll logike (i pomak je primenjen tamo).
                    // Ovde NEMA prepisivanja — koristimo vrednosti kakve jesu.

                    string styleConsistency = _detectedContext switch
                    {
                        "lullaby" => "soft light cozy warm peaceful night",
                        "party" => "colorful bright happy festive vibrant",
                        "love" => "warm golden romantic soft glow",
                        "sad" => "grey moody soft desaturated melancholy",
                        "adventure" => "outdoor bright natural energetic",
                        "dance" => "colorful joyful movement bright",
                        "christmas" => "warm holiday lights cozy festive",
                        "animal" => "cute nature soft light warm",
                        "school" => "bright colorful educational warm",
                        "nature" => "natural sunlight green peaceful",
                        _ => "warm colors soft light happy atmosphere child friendly"
                    };
                    string energyBoost = scene.Energy >= 4 ? " action fast dynamic exciting" : scene.Energy <= 2 ? " calm peaceful slow gentle" : "";
                    string emotionBoost = scene.Emotion;

                    // Gradimo čist engleski query — nikakav srpski/bosanski tekst ne sme da prođe
                    string primaryQuery   = BuildLiteralSearchQuery(scene.Keywords, scene.Action, energyBoost, styleConsistency);
                    string fallbackQuery  = BuildLiteralSearchQuery(scene.Keywords, "", "", "");
                    string fallback2Query = BuildLiteralSearchQuery("", scene.Action, "", styleConsistency);

                    LogToMainWindow($"🎬 Scena {i + 1}: Energy={scene.Energy}, Action={scene.Action}, Duration={scene.Duration:F1}s");
                    LogToMainWindow($"   📝 Stih: '{(scene.Description.Length > 50 ? scene.Description.Substring(0, 50) + "..." : scene.Description)}'");
                    LogToMainWindow($"   🔑 Keywords: '{scene.Keywords}'");
                    LogToMainWindow($"   🔍 Pixabay query: '{primaryQuery}'");

                    AnnounceToUser($"Scena {i + 1}/{count}: {scene.Description}...", 10 + (i * 70 / count));

                    string mediaPath = null;

                    if (mediaType == "video" && scene.Duration > 8.0)
                    {
                        // Scena je duga — preuzmi više kratkih klipova i spoji ih
                        // Vizuelna raznolikost: 3×7s umesto 1×21s u loopu
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

                    // Poslednji fallback — color gradient slika u boji prema mood-u (nikad prazna scena)
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
                                // Video je duži — odseci višak (brzo, bez re-encode)
                                finalPath = await TrimVideoToDuration(mediaPath, scene.Duration, _tempVideoFolder);
                            }
                            else if (videoDuration < scene.Duration - 0.5)
                            {
                                // Video je kraći — NE loopujemo ovde.
                                // RenderEngine koristi -stream_loop -1 i sam loopuje pri renderu.
                                // Samo logujemo razliku.
                                LogToMainWindow($"   🔁 Kratak video ({videoDuration:F1}s < {scene.Duration:F1}s) — RenderEngine će loopovati");
                                finalPath = mediaPath; // original, bez promene
                            }
                        }

                        // Ambijentalni zvukovi - SAMO Freesound (Pixabay uklonjen)
                        string ambientPath = await GetAmbientSoundPath(scene.AmbientSound, _tempVideoFolder, _cts?.Token ?? CancellationToken.None);
                        if (ambientPath != null)
                        {
                            scene.AmbientPath = ambientPath;
                            LogToMainWindow($"🔊 Ambijentalni zvuk za scenu {i + 1}: {scene.AmbientSound}");
                        }

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
                            // mood:X|context:Y — RenderEngine koristi za color grading
                            MoodTag = $"mood:{scene.Emotion ?? _detectedMood}|context:{_detectedContext}"
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
                    AnnounceToUser("Miksiram ambijentalne zvukove sa muzikom...", 90);

                    for (int idx = 0; idx < storyBoard.Scenes.Count && idx < _segments.Count; idx++)
                    {
                        if (!string.IsNullOrEmpty(_segments[idx].AmbientSoundPath))
                            storyBoard.Scenes[idx].AmbientPath = _segments[idx].AmbientSoundPath;
                    }

                    _ambientAudioPath = await MixAmbientWithMusic(audioPath, storyBoard.Scenes, totalDuration, _tempVideoFolder);
                }

                // Funkcija 7: Preview lista pre rendera — JAWS accessible
                // Loguje svaku scenu sa stihom i query-jem da korisnik može da provjeri
                AnnounceToUser("Generisanje završeno. Pregledam scene...", 95);
                LogToMainWindow("═══════════════════════════════════════");
                LogToMainWindow($"📋 PREGLED SCENE — {_segments.Count} scena spremno");
                LogToMainWindow("═══════════════════════════════════════");
                for (int si = 0; si < _segments.Count; si++)
                {
                    var seg = _segments[si];
                    string hasMedia = string.IsNullOrEmpty(seg.Path) || !File.Exists(seg.Path)
                        ? "❌ NEMA MEDIJA" : "✅";
                    string lyric = !string.IsNullOrEmpty(seg.LyricText) ? $" | Stih: \"{seg.LyricText}\"" : "";
                    LogToMainWindow($"  [{si + 1:D2}] {hasMedia} {FormatTime(seg.StartTime)}-{FormatTime(seg.StartTime + seg.Duration)} " +
                                   $"({seg.Duration:F1}s) — {seg.Description}{lyric}");
                }
                LogToMainWindow("═══════════════════════════════════════");

                int missingCount = _segments.Count(s => string.IsNullOrEmpty(s.Path) || !File.Exists(s.Path));
                if (missingCount > 0)
                    LogToMainWindow($"⚠ {missingCount} scena nema medija — biće preskočene u renderu");
                else
                    LogToMainWindow($"✅ Sve scene imaju medij. Pokrećem render...");

                AnnounceToUser($"Pregled gotov. {_segments.Count} scena. Kreiram video.", 97);

                await CreateVideo(storyBoard);
            }
            catch (Exception ex) { WpfMessageBox.Show($"Greška: {ex.Message}", "Greška", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { btnGenerate.IsEnabled = true; btnGenerate.Content = "🎬 KREIRAJ VIDEO"; }
        }

        #endregion

        #region TrimVideoToDuration

        private async Task<string> TrimVideoToDuration(string inputPath, double targetDuration, string tempDir)
        {
            var sw = Stopwatch.StartNew();
            LogToMainWindow($"✂️ Trim/Loop: {Path.GetFileName(inputPath)} → {targetDuration:F1}s");

            try
            {
                string outputPath = Path.Combine(tempDir, $"trimmed_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");
                string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath)) return inputPath;

                // Izmeri stvarno trajanje ulaznog videa
                double actualDuration = await GetVideoDuration(inputPath);

                string args;
                if (actualDuration >= targetDuration - 0.5)
                {
                    // Video je dovoljno dug — obični trim (brz, bez re-encode)
                    args = $"-ss 0 -i \"{inputPath}\" -t {targetDuration.ToString(CultureInfo.InvariantCulture)} -c copy -avoid_negative_ts make_zero -y \"{outputPath}\"";
                    LogToMainWindow($"   ✂ Trim: {actualDuration:F1}s → {targetDuration:F1}s");
                }
                else
                {
                    // Video je KRAĆI od potrebnog — NE loopujemo ovde, vraćamo original.
                    // RenderEngine koristi -stream_loop -1 i sam loopuje brzo pri finalnom renderu,
                    // bez re-encode koji bi trajao 30+ minuta za kratke klipove.
                    LogToMainWindow($"   🔁 Kratak video ({actualDuration:F1}s < {targetDuration:F1}s) — RenderEngine će loopovati");
                    return inputPath;
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath, Arguments = args,
                        CreateNoWindow = true, UseShellExecute = false,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                {
                    LogToMainWindow($"✅ Trim/Loop uspješan za {sw.ElapsedMilliseconds}ms");
                    return outputPath;
                }

                // Fallback — vrati original, RenderEngine će ga sam skratiti/loopovati
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

                string args = $"-i \"{videoPath}\" -f null - 2>&1";
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo { FileName = ffmpegPath, Arguments = args, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true }
                };
                process.Start();
                string output = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

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
                        "Pixabay API kljuc nije pronadjen.\n\nRegistruj se besplatno na pixabay.com/api,\npa unesi kljuc ovdje.");
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
                    AnnounceToUser("Pixabay API kljuc nije unesen. Preuzimanje nije moguce.");
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
                            // Pixabay duration kategorije: short(≤4s), medium(4-20s), long(20-60s)
                            // Biramo kategoriju na osnovu potrebnog trajanja scene
                            ? (minDurationSeconds > 20
                                ? $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=20&video_type=all&min_duration=20&max_duration=60"
                                : minDurationSeconds > 4
                                    ? $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=20&video_type=all&min_duration=4&max_duration=60"
                                    : $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=20&video_type=all")
                            : $"https://pixabay.com/api/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&image_type=photo&safesearch=true&per_page=20&min_width={minWidth}";

                        string response = await _httpClient.GetStringAsync(url, ct);
                        var json = JObject.Parse(response);
                        var hits = json["hits"] as JArray;

                        if (hits == null || hits.Count == 0) continue;

                        if (mediaType == "video")
                        {
                            // Određujemo početni indeks na osnovu broja prethodnih upotreba ovog query-ja
                            // — svaki sledeći poziv sa istim query-jem dobija sledeći hit iz liste
                            string queryKey = searchQ.ToLowerInvariant().Trim();
                            if (!_queryUseCount.ContainsKey(queryKey))
                                _queryUseCount[queryKey] = 0;

                            int startIdx = _queryUseCount[queryKey] % hits.Count;
                            _queryUseCount[queryKey]++;

                            // Prolazimo kroz hits počev od startIdx (kružno), preskačemo već korišćene URL-ove
                            for (int hitOffset = 0; hitOffset < hits.Count; hitOffset++)
                            {
                                int hitIdx = (startIdx + hitOffset) % hits.Count;
                                var hit = hits[hitIdx];
                                var videos = hit["videos"] as JObject;
                                if (videos == null) continue;

                                // Pokušaj large, pa medium, pa small format
                                string[] formats = { "large", "medium", "small" };
                                foreach (var fmt in formats)
                                {
                                    var videoObj = videos[fmt];
                                    if (videoObj == null) continue;

                                    string dlUrl = videoObj["url"]?.ToString();
                                    if (string.IsNullOrEmpty(dlUrl)) continue;

                                    // Preskočimo ako smo već koristili ovaj URL u ovoj sesiji generisanja
                                    if (_usedMediaUrls.Contains(dlUrl))
                                    {
                                        LogToMainWindow($"   ⏭ Preskačem već korišćeni video URL (hit {hitIdx + 1})");
                                        continue;
                                    }

                                    // Označimo kao korišćen
                                    _usedMediaUrls.Add(dlUrl);

                                    string fileName = $"AI_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4";
                                    string fullPath = Path.Combine(GetCurrentProjectFolder(), fileName);

                                    LogToMainWindow($"   ⬇ Preuzimam video hit {hitIdx + 1}/{hits.Count} (format: {fmt}): {dlUrl.Substring(dlUrl.LastIndexOf('/') + 1)}");
                                    byte[] data = await _httpClient.GetByteArrayAsync(dlUrl, ct);
                                    await File.WriteAllBytesAsync(fullPath, data, ct);
                                    return fullPath;
                                }
                            }

                            // Svi hitovi su već korišćeni — ako smo na attempt 0, nastavi na fallback
                            LogToMainWindow($"   ⚠ Svi {hits.Count} video rezultati za '{searchQ.Substring(0, Math.Min(40, searchQ.Length))}' su već korišćeni");
                        }
                        else
                        {
                            // Isti princip za slike — rotacija + deduplication
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

                                byte[] data = await _httpClient.GetByteArrayAsync(dlUrl, ct);
                                await File.WriteAllBytesAsync(fullPath, data, ct);
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

            // Ukloni stare video/slika klipove I stare tranzicione audio zvukove (🔊)
            // Bez ovoga, svako novo generisanje akumulira stare zvukove
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

            var sortedSegments = _segments.OrderBy(s => s.StartTime).ToList();
            double maxEnd = cursor;

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
                    // AudioDescription nosi MoodTag za color grading u RenderEngine-u
                    AudioDescription = !string.IsNullOrEmpty(segment.MoodTag)
                        ? segment.MoodTag
                        : segment.Description
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
            double outroStart = totalDuration - outroDuration;
            if (outroStart < 0) outroStart = totalDuration;
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

            // VAŽNO: Iteriramo SAMO kroz video/slika klipove (snimak liste prije dodavanja tranzicija)
            // Ako bismo iterirali kroz addedItems i u petlji dodavali u addedItems,
            // dobijamo beskonačan loop jer svaka tranzicija generise novu tranziciju.
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
                // Koristimo prosječnu energiju tranzicije
                int transEnergy = (currentEnergy + nextEnergy) / 2;
                double fadeDuration = transEnergy >= 4 ? 0.3 : 0.4;

                // Tranzicioni zvuk: dodaj samo na energičnijim tranzicijama (>=3)
                // i variramo tip da ne bude monotono
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

            await Dispatcher.InvokeAsync(() => mainWindow.UpdateTimelineDisplay());

            int segmentsCount = _segments?.Count ?? 0;
            double segmentsTotal = _segments?.Sum(s => s.Duration) ?? 0;

            WpfMessageBox.Show($"AI Video Creator završio!\n\nDodato: {segmentsCount} klipova\nTrajanje klipova: {FormatTime(segmentsTotal)} ({segmentsTotal:F1}s)\nTrajanje audio: {FormatTime(totalDuration)} ({totalDuration:F1}s)\n\nSada možete renderovati video (Ctrl+R).", "Završeno", MessageBoxButton.OK, MessageBoxImage.Information);
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
                AnnounceToUser("Greska pri citanju Pixabay kljuca: " + ex.Message);
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
                AnnounceToUser("Greska pri cuvanju Pixabay kljuca: " + ex.Message);
            }
        }


        #region Mood Color Grading i Gradient Fallback

        /// <summary>
        /// Funkcija 6: Generiše color gradient video kao fallback
        /// kada Pixabay ne vrati nijedan rezultat.
        /// Koristi FFmpeg lavfi color source — ne treba internet.
        /// </summary>
        private async Task<string> GenerateMoodGradient(string mood, double duration, string tempDir)
        {
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) return null;

            // Boje po mood-u
            string color = mood?.ToLower() switch
            {
                "sad" or "melancholy"  => "0x3a5f8a",   // plava
                "happy" or "joyful"    => "0xf4a020",   // narandžasta
                "calm" or "peaceful"   => "0x4a9e6b",   // zelena
                "excited" or "upbeat"  => "0xe03060",   // crvena
                "romantic" or "love"   => "0xc04070",   // roze
                "christmas"            => "0xb01010",   // tamnocrvena
                "lullaby"              => "0x6a5acd",   // ljubičasta
                _                      => "0x2060a0"    // default plava
            };

            string outputPath = Path.Combine(tempDir, $"gradient_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");
            string dStr = duration.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Gradient: boja se polako menja od tamnije do svetlije verzije
            double fadeOutStart = Math.Max(0, duration - 1);
            string fadeOutStr = fadeOutStart.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string args = $"-f lavfi -i \"color=c={color}:s=1280x720:d={dStr},format=yuv420p\" " +
                          $"-vf \"fade=in:0:25,fade=out:st={fadeOutStr}:d=1\" " +
                          $"-c:v libx264 -preset veryfast -crf 23 -an -y \"{outputPath}\"";

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath, Arguments = args,
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            await proc.WaitForExitAsync();

            return (proc.ExitCode == 0 && File.Exists(outputPath)) ? outputPath : null;
        }

        /// <summary>
        /// Funkcija 4: Vraća FFmpeg vf filter string za color grading prema mood-u.
        /// Koristi se u RenderEngine pri obradi svakog video klipa.
        /// </summary>
        public static string GetMoodColorFilter(string mood, string context)
        {
            string m = (mood ?? "").ToLower();
            string c = (context ?? "").ToLower();

            // Kontekst ima prioritet nad mood-om
            if (c == "lullaby")   return "eq=brightness=0.05:saturation=0.7:contrast=0.95,vignette=PI/4";
            if (c == "sad")       return "eq=saturation=0.5:contrast=0.9,colorbalance=bs=0.1";
            if (c == "christmas") return "eq=saturation=1.3:contrast=1.1,colorbalance=rs=0.15:gs=-0.05";
            if (c == "party")     return "eq=saturation=1.4:brightness=0.05:contrast=1.1";
            if (c == "nature")    return "eq=saturation=1.1:contrast=1.05,colorbalance=gs=0.05";

            // Fallback po mood-u
            return m switch
            {
                "sad" or "melancholy" => "eq=saturation=0.5:contrast=0.9,colorbalance=bs=0.1",
                "happy" or "joyful"   => "eq=saturation=1.2:brightness=0.03:contrast=1.05",
                "calm" or "peaceful"  => "eq=saturation=0.9:contrast=0.95,vignette=PI/6",
                "excited" or "upbeat" => "eq=saturation=1.3:contrast=1.1:brightness=0.05",
                "romantic" or "love"  => "eq=saturation=1.1,colorbalance=rs=0.1:bs=-0.05",
                _                     => "eq=saturation=1.05:contrast=1.02"  // blagi boost
            };
        }

        #endregion

        #region MultiClip — više videa po sceni
        /// <summary>
        /// Preuzima više kratkih videa za istu scenu i spaja ih u jedan klip.
        /// Koristi se kada je targetDuration dugo (npr. 21s) a Pixabay vraća kratke videe.
        /// Rezultat: vizuelna raznolikost unutar jedne scene.
        /// </summary>
        private async Task<string> SearchAndDownloadMultipleMedia(
            string keywords, string fallbackKeywords, string mediaType,
            double targetDuration, string tempDir, CancellationToken ct)
        {
            int clipCount = Math.Max(2, (int)Math.Ceiling(targetDuration / 8.0));
            clipCount = Math.Min(clipCount, 4); // max 4 klipa po sceni

            double clipDuration = targetDuration / clipCount;
            var clipPaths = new List<string>();

            LogToMainWindow($"🎬 MultiClip: {clipCount} klipa × {clipDuration:F1}s = {targetDuration:F1}s ukupno");

            for (int c = 0; c < clipCount; c++)
            {
                ct.ThrowIfCancellationRequested();
                // Naizmenično koristimo primary i fallback query za vizuelnu raznolikost
                string q = (c % 2 == 0) ? keywords : (fallbackKeywords ?? keywords);
                string path = await SearchAndDownloadMedia(q, 1080, mediaType, ct, clipDuration);
                if (path == null && fallbackKeywords != null)
                    path = await SearchAndDownloadMedia(fallbackKeywords, 1080, mediaType, ct, clipDuration);
                if (path != null)
                    clipPaths.Add(path);
            }

            if (clipPaths.Count == 0) return null;
            if (clipPaths.Count == 1) return clipPaths[0]; // jedan klip — vrati direktno

            // Spoji klipove concat-om
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) return clipPaths[0];

            string concatList = Path.Combine(tempDir, $"multiclip_{Guid.NewGuid().ToString().Substring(0, 8)}.txt");
            string outputPath  = Path.Combine(tempDir, $"multi_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");

            // Pripremi klipove — svaki trimuj/loopuj na clipDuration
            var preparedPaths = new List<string>();
            foreach (var cp in clipPaths)
            {
                double dur = await GetVideoDuration(cp);
                string prepared = cp;
                if (dur > clipDuration + 0.5)
                    prepared = await TrimVideoToDuration(cp, clipDuration, tempDir);
                preparedPaths.Add(prepared);
            }

            // Napiši concat listu
            using (var sw = new System.IO.StreamWriter(concatList, false, System.Text.Encoding.UTF8))
                foreach (var p in preparedPaths)
                    sw.WriteLine("file '" + p.Replace("\\", "/") + "' ");

            // -c copy: bez re-encode — svi klipovi su već isti format/codec (Pixabay MP4/H264)
            // Dramatično brže od libx264 re-encode koji je trajao minute po spajanju
            string args = $"-f concat -safe 0 -i \"{concatList}\" -c copy -an -y \"{outputPath}\"";

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath, Arguments = args,
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000)
            {
                LogToMainWindow($"✅ MultiClip spojen: {clipPaths.Count} klipa → {targetDuration:F1}s");
                return outputPath;
            }

            LogToMainWindow("⚠ MultiClip concat neuspješan — koristim prvi klip");
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
            if (_isRunning) { _cts?.Cancel(); AnnounceToUser("Otkazivanje..."); btnCancel.Content = "Otkazivanje..."; btnCancel.IsEnabled = false; }
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
        public string MoodTag { get; set; }  // "mood:X|context:Y" za color grading
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

} // end class AIVideoCreator