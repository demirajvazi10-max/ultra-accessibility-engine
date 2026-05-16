#nullable disable
using SkiaSharp;
using Timer = System.Windows.Forms.Timer;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Microsoft.Win32;
using System.Windows.Forms.Integration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Input;
using System.Drawing;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
// === AMBIGUITY RJEŠENJA ===
using WinForms = System.Windows.Forms;
using WpfDragDrop = System.Windows.DragDrop;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDataFormats = System.Windows.DataFormats;
using WinFormsKeyEventArgs = System.Windows.Forms.KeyEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using WinFormsApplication = System.Windows.Forms.Application;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WinFormsSaveFileDialog = System.Windows.Forms.SaveFileDialog;
using WinFormsOpenFileDialog = System.Windows.Forms.OpenFileDialog;
using WpfPoint = System.Windows.Point;
using DrawingPoint = System.Drawing.Point;

namespace UltraVideoEditor
{
    public partial class MainWindow : Window
    {
        // Language helper
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        private string _introText = "🎵 Nova dječija pjesmica";
        private string _outroText = "Autor: Iskra Ajvazi. Muzika i tekst: Iskra Ajvazi. Za još predivnih pesmica, zapratite naš YouTube kanal: @Rastimo uz Iskru";

        public string IntroText
        {
            get => _introText;
            set => _introText = value;
        }

        public string OutroText
        {
            get => _outroText;
            set => _outroText = value;
        }
        public List<TimelineItem> timelineItems = new List<TimelineItem>();
        private List<SubtitleItem> subtitles = new List<SubtitleItem>();
        private string _tempVideoFolder = Path.GetTempPath();
        private List<double> markers = new List<double>();
        private List<TransitionEffect> transitions = new List<TransitionEffect>();
        private bool accessibilityMode = true;
        private System.Windows.Forms.Timer _selectionDebounceTimer;
        private bool _selectionProcessing = false;
        private TimelineItem _pendingPlaybackItem = null;
        public string currentProjectFolder = string.Empty;  // DODATO POLJE
        public string GetCurrentProjectFolder() => currentProjectFolder;

        /// <summary>
        /// Poslednja rezolucija koju je korisnik odabrao u ResolutionDialog-u.
        /// Čuva se ovde da bi AIVideoCreator mogao da je pročita pre rendera.
        /// </summary>
        public string _selectedResolution = "1920x1080";

        /// <summary>
        /// Delegat koji AIVideoCreator čita putem _targetResolution propertija.
        /// </summary>
        public Func<string> GetExportResolution => () => _selectedResolution;
        private bool isPlaying = false;
        private double currentPlaybackPosition = 0;
        private double currentVideoDuration = 0;
        private double zoomLevel = 1.0;
        private int previewDurationSeconds = 10;
        private string selectedTranscribeAudioPath = "";
        private double transitionDuration = 1.0;
        private int currentTrackFilter = -1;
        private AnimationKeyframe currentEditingKeyframe = null;
        private bool useGPUAcceleration = true;
        private LibVLC _libVLC;
        private global::LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private Media _currentMedia;
        private Equalizer _equalizer;

        private List<AISubtitle> syncedSubtitles = new List<AISubtitle>();
        private string selectedAudioPath = "";
        private WinForms.ListView nativeListView;
        private ExportSettingsData currentExportSettings = new ExportSettingsData();

        private Stack<List<TimelineItem>> undoStack = new Stack<List<TimelineItem>>();
        private Stack<List<TimelineItem>> redoStack = new Stack<List<TimelineItem>>();

        private System.Windows.Threading.DispatcherTimer positionAnnounceTimer;
        private CancellationTokenSource _renderCancellation;
        private LogWindow _logWindow;
        private TimelineItem _copiedClip = null;
        private RenderEngine _renderEngine;
        internal string _currentLanguage = "sr";
        private string _currentTheme = "dark"; // dark | contrast | light

        private List<AnimationScene> _animationScenes = new List<AnimationScene>();
        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);
        private const uint MB_ICONASTERISK = 0x00000040;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                _logWindow = new LogWindow();
                _renderEngine = new RenderEngine(useGPUAcceleration);

                sldPreviewDuration.ValueChanged += (s, e) =>
                {
                    previewDurationSeconds = (int)sldPreviewDuration.Value;
                    txtPreviewDurationValue.Text = $"{previewDurationSeconds} s";
                    if (accessibilityMode) LogMessage(string.Format(L("preview_duration_sec"), previewDurationSeconds), true);
                };
                txtPreviewDurationValue.Text = "10 s";

                SetupSliderAnnouncements();

                sldZoom.ValueChanged += AnimValue_Changed;
                sldRotation.ValueChanged += AnimValue_Changed;
                sldX.ValueChanged += AnimValue_Changed;
                sldY.ValueChanged += AnimValue_Changed;
                sldOpacity.ValueChanged += AnimValue_Changed;

                LibVLCSharp.Shared.Core.Initialize();
                _libVLC = new LibVLC();
                _mediaPlayer = new global::LibVLCSharp.Shared.MediaPlayer(_libVLC);

                try { vlcVideoView.MediaPlayer = _mediaPlayer; } catch { }

                _equalizer = new Equalizer();
                _mediaPlayer.SetEqualizer(_equalizer);

                _mediaPlayer.LengthChanged += OnMediaLengthChanged;
                _mediaPlayer.Playing += (s, e) =>
                {
                    isPlaying = true;
                    Dispatcher.Invoke(() => { if (btnPlay != null) btnPlay.Content = "⏸ PAUSE"; });
                    LogMessage("Reprodukcija pokrenuta", false);
                };
                _mediaPlayer.Stopped += (s, e) =>
                {
                    isPlaying = false;
                    Dispatcher.Invoke(() => { if (btnPlay != null) btnPlay.Content = "▶ PLAY"; });
                    LogMessage("Reprodukcija zaustavljena", false);
                };
                _mediaPlayer.EndReached += (s, e) =>
                {
                    isPlaying = false;
                    Dispatcher.Invoke(() => { if (btnPlay != null) btnPlay.Content = "▶ PLAY"; });
                    LogMessage(L("playback_done"), false);
                };
                _mediaPlayer.TimeChanged += OnTimeChanged;

                SetupKeyboardCommands();
                SetAccessibilityNames();
                InitPositionAnnouncer();
                LoadTransitions();
                SetupDragDrop();
                SetupDragDropFromExplorer();
                Loaded += (s, ev) => SetupAIPromptPaste();
                SetupTransitionContextMenus();

                InitNativeListView();
                _selectionDebounceTimer = new System.Windows.Forms.Timer();
                _selectionDebounceTimer.Interval = 400;
                _selectionDebounceTimer.Tick += (s, e) =>
                {
                    _selectionDebounceTimer.Stop();
                    _selectionProcessing = false;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_pendingPlaybackItem != null)
                        {
                            PlaySelectedItem(_pendingPlaybackItem);
                            _pendingPlaybackItem = null;
                        }
                    }));
                };
                // ApplyLanguage MORA biti u Loaded eventu
                // jer u konstruktoru WPF elementi jos nisu sigurno kreirani
                Loaded += (s, ev) =>
                {
                    ApplyLanguage();
                    LoadSavedTheme();
                    LogMessage("Sistem spreman...", true);
                };
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(string.Format(L("startup_error"), ex.Message) + "\n\n" + ex.StackTrace);
                throw;
            }
        }

        private void ApplyLanguage()
        {
            string L(string key) => LanguageManager.GetText(key, _currentLanguage);

            this.Title = L("app_title");
            if (txtJawsLog != null) txtJawsLog.Text = L("system_ready");
            if (txtStatus != null) txtStatus.Text = L("status_idle");

            // ── AUTO-SCAN: prevodi sve elemente koji imaju Tag="key" ili Tag="emoji|key" ──
            ApplyLanguageToTree(this);

            // ── TABOVI (Header nije Content/Text/ToolTip, poseban slučaj) ──────────
            if (tabTimeline != null) tabTimeline.Header = L("timeline_tab");
            if (tabAI != null) tabAI.Header = L("ai_tab");
            if (tabEffects != null) tabEffects.Header = L("effects_tab");
            if (tabSubtitles != null) tabSubtitles.Header = L("subtitles_tab");
            if (tabTransitions != null) tabTransitions.Header = L("transitions_tab");
            if (tabAnimation != null) tabAnimation.Header = L("animation_tab");

            // ── MENI HEADERS (Tag sistem ne pokriva MenuItem.Header direktno jer
            //    WPF MenuItem.Header može biti bilo koji objekt; rešavamo posebno) ──
            ApplyLanguageToMenuItems(this);

            // ── JAWS AutomationProperties ─────────────────────────────────────────
            if (txtAIPrompt != null)
                System.Windows.Automation.AutomationProperties.SetName(txtAIPrompt, L("ai_prompt"));
            if (btnGenerate != null)
                System.Windows.Automation.AutomationProperties.SetName(btnGenerate, L("generate_frames"));

            _introText = L("default_intro_text");
            _outroText = L("default_outro_text");

            // ── LISTVIEW KOLONE (WinForms, ne reaguju na Tag sistem) ──────────
            if (nativeListView != null && nativeListView.Columns.Count >= 7)
            {
                nativeListView.Columns[0].Text = L("listview_col_num");
                nativeListView.Columns[1].Text = L("listview_col_name");
                nativeListView.Columns[2].Text = L("listview_col_type");
                nativeListView.Columns[3].Text = L("listview_col_duration");
                nativeListView.Columns[4].Text = L("listview_col_start");
                nativeListView.Columns[5].Text = L("listview_col_end");
                nativeListView.Columns[6].Text = L("listview_col_audio_desc");
            }

            UpdateTimelineDisplay();
        }

        /// <summary>
        /// Rekurzivno prolazi kroz vizuelno stablo i prevodi sve elemente
        /// koji imaju Tag postavljen kao string ključa prevoda.
        /// 
        /// Tag format:
        ///   "key"           → samo tekst (npr. "cut")
        ///   "emoji|key"     → emoji + razmak + tekst (npr. "✂|cut")
        ///   "emoji|key|shortcut" → emoji + tekst + shortcut u zagradi (npr. "✂|cut|Ctrl+X")
        ///   "tooltip:key"   → samo ToolTip (ne Content/Text)
        /// </summary>
        private void ApplyLanguageToTree(DependencyObject root)
        {
            string L(string key) => LanguageManager.GetText(key, _currentLanguage);

            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Tag is string tag && !string.IsNullOrEmpty(tag))
                {
                    ApplyTagToElement(fe, tag, L);
                }
                ApplyLanguageToTree(child);
            }
        }

        private void ApplyTagToElement(FrameworkElement fe, string tag, Func<string, string> L)
        {
            // Format: "tooltip:key" → samo tooltip
            if (tag.StartsWith("tooltip:"))
            {
                string key = tag.Substring(8);
                fe.ToolTip = L(key);
                return;
            }

            // Format: "emoji|key" ili "emoji|key|Ctrl+X"
            string[] parts = tag.Split('|');
            string emoji = parts.Length >= 2 ? parts[0] : "";
            string langKey = parts.Length >= 2 ? parts[1] : parts[0];
            string shortcut = parts.Length >= 3 ? " (" + parts[2] + ")" : "";

            string translated = L(langKey);
            string content = string.IsNullOrEmpty(emoji)
                ? translated + shortcut
                : emoji + " " + translated + shortcut;

            if (fe is System.Windows.Controls.Button btn)
            {
                btn.Content = content;
                // ToolTip = samo prevedeni tekst bez shortcuta, da JAWS jasno čita
                if (btn.ToolTip is string || btn.ToolTip == null)
                    btn.ToolTip = translated + shortcut;
            }
            else if (fe is System.Windows.Controls.TextBlock tb)
                tb.Text = content;
            else if (fe is System.Windows.Controls.Label lbl)
                lbl.Content = content;
        }

        /// <summary>
        /// Prolazi kroz MenuItem hijerarhiju i prevodi Header-e.
        /// MenuItem.Header je object, pa koristimo Tag konvenciju:
        ///   Tag="key"          → Header = prevod
        ///   Tag="emoji|key"    → Header = "emoji prevod"  (bez _ prefiksa — JAWS ga čita!)
        /// </summary>
        private void ApplyLanguageToMenuItems(DependencyObject root)
        {
            string L(string key) => LanguageManager.GetText(key, _currentLanguage);

            // Tražimo MenuItem-e kroz logičko (ne vizuelno) stablo
            foreach (var mi in FindLogicalChildren<MenuItem>(root))
            {
                if (mi.Tag is string tag && !string.IsNullOrEmpty(tag))
                {
                    string[] parts = tag.Split('|');
                    string emoji = parts.Length >= 2 ? parts[0] : "";
                    string langKey = parts.Length >= 2 ? parts[1] : parts[0];
                    string translated = L(langKey);
                    mi.Header = string.IsNullOrEmpty(emoji) ? translated : emoji + " " + translated;
                }
            }
        }

        private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject obj) where T : DependencyObject
        {
            if (obj == null) yield break;
            foreach (object child in LogicalTreeHelper.GetChildren(obj))
            {
                if (child is DependencyObject depChild)
                {
                    if (depChild is T t) yield return t;
                    foreach (T childOfChild in FindLogicalChildren<T>(depChild))
                        yield return childOfChild;
                }
            }
        }

        private void SetLanguageSerbian_Click(object sender, RoutedEventArgs e) { _currentLanguage = "sr"; ApplyLanguage(); LogMessage("Jezik: Srpski", true); }
        private void SetLanguageEnglish_Click(object sender, RoutedEventArgs e) { _currentLanguage = "en"; ApplyLanguage(); LogMessage("Language: English", true); }
        private void SetLanguageGerman_Click(object sender, RoutedEventArgs e) { _currentLanguage = "de"; ApplyLanguage(); LogMessage("Sprache: Deutsch", true); }

        // ── TEME ────────────────────────────────────────────────────
        private void SetThemeDark_Click(object sender, System.Windows.RoutedEventArgs e)
        { _currentTheme = "dark"; ApplyTheme(); LogMessage("Tema: Tamna", true); }
        private void SetThemeContrast_Click(object sender, System.Windows.RoutedEventArgs e)
        { _currentTheme = "contrast"; ApplyTheme(); LogMessage("Tema: Visoki kontrast", true); }
        private void SetThemeLight_Click(object sender, System.Windows.RoutedEventArgs e)
        { _currentTheme = "light"; ApplyTheme(); LogMessage("Tema: Svetla", true); }

        private void ApplyTheme()
        {
            string bg, bgPanel, accent;
            switch (_currentTheme)
            {
                case "contrast":
                    bg = "#000000"; bgPanel = "#111111"; accent = "#FFFF00";
                    break;
                case "light":
                    bg = "#F0F0F0"; bgPanel = "#FFFFFF"; accent = "#1565C0";
                    break;
                default: // dark
                    bg = "#1A1A2E"; bgPanel = "#1E1E1E"; accent = "#00E676";
                    break;
            }
            try
            {
                var conv = new System.Windows.Media.BrushConverter();
                this.Background = (System.Windows.Media.Brush)conv.ConvertFrom(bg);
                if (txtJawsLog != null)
                {
                    txtJawsLog.Background = (System.Windows.Media.Brush)conv.ConvertFrom(bgPanel);
                    txtJawsLog.Foreground = (System.Windows.Media.Brush)conv.ConvertFrom(accent);
                }
                if (txtStatus != null)
                    txtStatus.Foreground = (System.Windows.Media.Brush)conv.ConvertFrom(accent);
                // Snimi temu
                try
                {
                    File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theme.cfg"),
                    _currentTheme);
                }
                catch { }
            }
            catch (Exception ex) { LogMessage("Greska pri primeni teme: " + ex.Message, false); }
        }

        private void LoadSavedTheme()
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theme.cfg");
                if (File.Exists(p))
                {
                    string t = File.ReadAllText(p).Trim();
                    if (t == "contrast" || t == "light" || t == "dark")
                    { _currentTheme = t; ApplyTheme(); }
                }
            }
            catch { }
        }


        private string GetApiKey(string keyName)
        {
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
            string keyFile = Path.Combine(settingsPath, $"{keyName}_key.bin");
            if (!File.Exists(keyFile)) return null;
            try
            {
                byte[] encrypted = File.ReadAllBytes(keyFile);
                byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch { return null; }
        }

        private void SaveApiKey(string keyName, string apiKey)
        {
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
            Directory.CreateDirectory(settingsPath);
            string keyFile = Path.Combine(settingsPath, $"{keyName}_key.bin");
            byte[] data = Encoding.UTF8.GetBytes(apiKey);
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(keyFile, encrypted);
        }

        private string GetHuggingFaceApiKey() => GetApiKey("huggingface");
        private void SaveHuggingFaceApiKey(string key) => SaveApiKey("huggingface", key);
        private string GetCloudflareApiKey() => GetApiKey("cloudflare");
        private void SaveCloudflareApiKey(string key) => SaveApiKey("cloudflare", key);
        private string GetOpenAiApiKey() => GetApiKey("openai");
        private void SaveOpenAiApiKey(string key) => SaveApiKey("openai", key);
        private string GetPixabayApiKey() => GetApiKey("pixabay");
        private void SavePixabayApiKey(string key) => SaveApiKey("pixabay", key);
        private void SetupSliderAnnouncements()
        {
            var sliders = new[] { sldBrightness, sldContrast, sldBlur, sldBass, sldTreble, sldReverb, sldZoom, sldRotation, sldX, sldY, sldOpacity, sldPreviewDuration, sldTransitionDuration };
            foreach (var slider in sliders)
            {
                if (slider == null) continue;
                slider.ValueChanged += (s, e) =>
                {
                    if (accessibilityMode)
                    {
                        string name = AutomationProperties.GetName(slider);
                        if (string.IsNullOrEmpty(name)) name = "Slider";
                        LogMessage($"{name}: {e.NewValue:F1}", true);
                    }
                };
            }
        }

        public void LogMessage(string message, bool announce = false)
        {
            Dispatcher.Invoke(() =>
            {
                if (txtJawsLog != null)
                    txtJawsLog.Text = message;
                // Log prozor uvijek prima poruke (bez obzira na accessibility mod)
                _logWindow?.AddMessage(message, announce);
                // Screen reader announcement: samo jednom, ne duplo
                if (announce && accessibilityMode)
                    AnnounceToJaws(message);
            });
        }
        /// <summary>
        /// Dodaje titl na timeline za JAWS i prikaz na ekranu
        /// </summary>
        public void AddSubtitle(string text, double startTime, double endTime)
        {
            try
            {
                var subtitle = new SubtitleItem
                {
                    Text = text,
                    Start = startTime,
                    End = endTime
                };

                subtitles.Add(subtitle);

                Dispatcher.Invoke(() =>
                {
                    if (lstSubtitles != null)
                    {
                        lstSubtitles.Items.Add($"{FormatTime(startTime)} -> {FormatTime(endTime)}: {text}");
                    }
                });

                LogMessage($"📝 Titl: {text} ({FormatTime(startTime)} - {FormatTime(endTime)})", true);
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("subtitle_error"), ex.Message), true);
            }
        }
        private void AnnounceToJaws(string message)
        {
            // Status bar je uvijek vidljiv i JAWS/NVDA ga cita automatski (Assertive).
            // Korisnik NIKAD ne mora otvarati Log prozor da bi dobio info.
            Dispatcher.Invoke(() =>
            {
                // ── Primarni live region (uvijek vidljiv status bar) ──────────────
                if (txtJawsLog != null)
                {
                    txtJawsLog.Text = message;
                    var peer = UIElementAutomationPeer.FromElement(txtJawsLog);
                    peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
                // ── Ghost live region — JAWS trick: NVDA/JAWS pouzdanije cita
                //    kad se isti tekst pojavi u dva odvojena live regiona naizmjenicno ──
                if (txtJawsGhost != null && accessibilityMode)
                {
                    txtJawsGhost.Text = message;
                    var peer2 = UIElementAutomationPeer.FromElement(txtJawsGhost);
                    peer2?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            });
        }

        private void SetAccessibilityNames()
        {
            if (nativeListView != null)
            {
                nativeListView.AccessibleName = "Lista klipova na vremenskoj traci. Koristite strelice za navigaciju. Enter ili Space za reprodukciju. Page Up/Down za brzo listanje. Delete za brisanje.";
            }
            AutomationProperties.SetName(btnPlay, "Pusti ili pauziraj, Ctrl Space");
            AutomationProperties.SetName(btnRenderTool, "Renderuj projekat, Ctrl R");
            AutomationProperties.SetName(btnCut, "Seci klip, Ctrl X");
            AutomationProperties.SetName(btnCutAllMarkers, "Seci na svim markerima, Ctrl Shift X");
            AutomationProperties.SetName(btnMoveLeft, "Pomeri levo, Ctrl levo");
            AutomationProperties.SetName(btnMoveRight, "Pomeri desno, Ctrl desno");
            AutomationProperties.SetName(btnDuration, "Podesi trajanje, Ctrl D");
            AutomationProperties.SetName(btnVolume, L("acc_set_volume"));
            AutomationProperties.SetName(btnZoomIn, L("acc_zoom_in"));
            AutomationProperties.SetName(btnZoomOut, "Smanji zoom, Ctrl minus");
            AutomationProperties.SetName(btnAddMarker, "Dodaj marker, Ctrl M");
            AutomationProperties.SetName(btnNextMarker, L("acc_next_marker"));
            AutomationProperties.SetName(btnPrevMarker, "Prethodni marker, Ctrl Shift P");
            AutomationProperties.SetName(btnSeekBack, "Premotaj unazad 5 sekundi");
            AutomationProperties.SetName(btnSeekForward, "Premotaj napred 5 sekundi");
            AutomationProperties.SetName(tabTimeline, "Timeline tab, lista klipova");
            AutomationProperties.SetName(tabAI, "AI funkcije tab");
            AutomationProperties.SetName(tabEffects, "Efekti tab");
            AutomationProperties.SetName(tabSubtitles, "Titlovi tab");
            AutomationProperties.SetName(tabTransitions, "Tranzicije tab");
            AutomationProperties.SetName(tabAnimation, "Animacije tab");
            AutomationProperties.SetName(sldBrightness, "Kontrola svetline");
            AutomationProperties.SetName(sldContrast, "Kontrola kontrasta");
            AutomationProperties.SetName(sldBlur, "Kontrola zamagljenja");
            AutomationProperties.SetName(sldBass, L("acc_bass"));
            AutomationProperties.SetName(sldTreble, L("acc_treble"));
            AutomationProperties.SetName(sldReverb, "Reverb efekat");
            AutomationProperties.SetName(txtVoiceText, "Tekst za AI glas");
            AutomationProperties.SetName(txtAIPrompt, "Prompt za AI slike");
            AutomationProperties.SetName(txtLyrics, "Tekst pesme za sinhronizaciju");
            AutomationProperties.SetName(btnGenerateVoice, L("acc_generate_voice"));
            AutomationProperties.SetName(btnGenerate, L("acc_generate_frames"));
            AutomationProperties.SetName(btnSyncLyrics, "Sinhronizuj tekst");
            AutomationProperties.SetName(txtSubtitleText, "Tekst titla");
            AutomationProperties.SetName(txtSubStart, L("acc_subtitle_start"));
            AutomationProperties.SetName(txtSubEnd, "Kraj u sekundama");
            AutomationProperties.SetName(btnAddSubtitle, "Dodaj titl");
            AutomationProperties.SetName(btnClearSubtitles, L("acc_clear_subtitles"));
            AutomationProperties.SetName(sldPreviewDuration, L("acc_preview_duration"));
            AutomationProperties.SetName(cmbTranscribeAudio, "Izbor audio fajla za transkripciju");
            AutomationProperties.SetName(btnTranscribe, "Pokreni AI transkripciju");
            AutomationProperties.SetName(txtTranscriptionResult, "Rezultat transkripcije");
            AutomationProperties.SetName(cmbTrackSelector, "Izbor prikaza trake");
            AutomationProperties.SetName(lstKeyframes, "Lista keyframe-ova");
            AutomationProperties.SetName(btnAddKeyframe, "Dodaj keyframe");
            AutomationProperties.SetName(btnRemoveKeyframe, "Ukloni keyframe");
            AutomationProperties.SetName(btnApplyKeyframe, "Primeni keyframe");
            AutomationProperties.SetName(btnPreviewAnimation, "Pregledaj animaciju");
            AutomationProperties.SetName(sldZoom, "Kontrola zuma");
            AutomationProperties.SetName(sldRotation, "Kontrola rotacije");
            AutomationProperties.SetName(sldX, "Kontrola horizontalnog pomeranja");
            AutomationProperties.SetName(sldY, "Kontrola vertikalnog pomeranja");
            AutomationProperties.SetName(sldOpacity, "Kontrola providnosti");
            AutomationProperties.SetName(sldTransitionDuration, "Trajanje tranzicije");
            AutomationProperties.SetName(lstTransitions, "Lista tranzicija");
            AutomationProperties.SetName(btnRemoveTransition, "Ukloni tranziciju");
            AutomationProperties.SetName(chkGPUAcceleration, "GPU ubrzanje");
            // txtJawsLog mora biti live region
            if (txtJawsLog != null)
            {
                AutomationProperties.SetName(txtJawsLog, "Status poruka");
                AutomationProperties.SetLiveSetting(txtJawsLog, AutomationLiveSetting.Assertive);
            }
        }

        private void PlayBeep()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { MessageBeep(MB_ICONASTERISK); } catch { }
            }
        }

        private string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        private string FormatTimeDetailed(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f");
        private double GetTotalDuration() => timelineItems.Sum(t => t.Duration);
        private void UpdatePositionDisplay()
        {
            if (txtCurrentPosition != null)
                txtCurrentPosition.Text = $"{FormatTime(currentPlaybackPosition)} / {FormatTime(currentVideoDuration)}";
        }
        private void OnMediaLengthChanged(object sender, LibVLCSharp.Shared.MediaPlayerLengthChangedEventArgs e)
        {
            currentVideoDuration = e.Length / 1000.0;
            Dispatcher.Invoke(UpdatePositionDisplay);
        }
        private void OnTimeChanged(object sender, LibVLCSharp.Shared.MediaPlayerTimeChangedEventArgs e)
        {
            currentPlaybackPosition = e.Time / 1000.0;
            Dispatcher.Invoke(UpdatePositionDisplay);
        }

        private void InitPositionAnnouncer()
        {
            positionAnnounceTimer = new System.Windows.Threading.DispatcherTimer();
            positionAnnounceTimer.Interval = TimeSpan.FromSeconds(10);
            positionAnnounceTimer.Tick += (s, e) =>
            {
                if (isPlaying && accessibilityMode)
                {
                    try
                    {
                        LogMessage($"Pozicija: {FormatTime(currentPlaybackPosition)} od {FormatTime(currentVideoDuration)}", true);
                    }
                    catch { }
                }
            };
            // Pokreni timer odmah ako je pristupačni mod uključen
            if (accessibilityMode)
                positionAnnounceTimer.Start();
        }
        public void UpdateTimelineDisplay()
        {
            UpdateNativeListView();
        }

        private void UpdateMarkerDisplay()
        {
            if (txtMarkerStatus != null)
                txtMarkerStatus.Text = markers.Count > 0 ? $"📍 {markers.Count}" : "";
        }

        private void UpdateZoomDisplay()
        {
            if (txtZoomStatus != null) txtZoomStatus.Text = $"Zoom: {zoomLevel:F1}x";
            UpdateTimelineDisplay();
        }

        private void NewProject_Click(object sender, RoutedEventArgs e)
        {
            if (timelineItems.Count > 0 || subtitles.Count > 0)
            {
                var result = WpfMessageBox.Show(L("save_before_new"),
                                              "Novi projekat", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                    SaveProject_Click(null, null);
                else if (result == MessageBoxResult.Cancel)
                    return;
            }
            timelineItems.Clear();
            subtitles.Clear();
            markers.Clear();
            transitions.Clear();
            undoStack.Clear();
            redoStack.Clear();
            UpdateTimelineDisplay();
            lstSubtitles.Items.Clear();
            LogMessage("Napravljen novi projekat", true);
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            var d = new WpfSaveFileDialog { Filter = "Iskra Projekat|*.iskra", DefaultExt = "iskra", FileName = "projekat.iskra" };
            if (d.ShowDialog() == true)
            {
                try
                {
                    currentProjectFolder = Path.GetDirectoryName(d.FileName) ?? string.Empty;
                    var projectData = new ProjectData
                    {
                        TimelineItems = timelineItems,
                        Subtitles = subtitles,
                        Markers = markers,
                        Transitions = transitions,
                        CurrentTrackFilter = currentTrackFilter,
                        ZoomLevel = zoomLevel,
                        ProjectVersion = "5.0"
                    };
                    string json = JsonConvert.SerializeObject(projectData, Formatting.Indented);
                    File.WriteAllText(d.FileName, json);
                    LogMessage(string.Format(L("project_saved_as"), Path.GetFileName(d.FileName)), true);
                    PlayBeep();
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show(string.Format(L("project_save_error"), ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveAsProject_Click(object sender, RoutedEventArgs e)
        {
            var d = new WpfSaveFileDialog { Filter = "Iskra Projekat|*.iskra", DefaultExt = "iskra", FileName = "projekat.iskra" };
            if (d.ShowDialog() == true)
            {
                try
                {
                    currentProjectFolder = Path.GetDirectoryName(d.FileName) ?? string.Empty;
                    var projectData = new ProjectData
                    {
                        TimelineItems = timelineItems,
                        Subtitles = subtitles,
                        Markers = markers,
                        Transitions = transitions,
                        CurrentTrackFilter = currentTrackFilter,
                        ZoomLevel = zoomLevel,
                        ProjectVersion = "5.0"
                    };
                    string json = JsonConvert.SerializeObject(projectData, Formatting.Indented);
                    File.WriteAllText(d.FileName, json);
                    LogMessage(string.Format(L("project_saved_as"), Path.GetFileName(d.FileName)), true);
                    PlayBeep();
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show(string.Format(L("save_error2"), ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void LoadProject_Click(object sender, RoutedEventArgs e)
        {
            var d = new WpfOpenFileDialog { Filter = "Iskra Projekat|*.iskra" };
            if (d.ShowDialog() == true)
            {
                try
                {
                    string json = await File.ReadAllTextAsync(d.FileName);
                    var projectData = JsonConvert.DeserializeObject<ProjectData>(json);
                    if (projectData == null) { LogMessage(L("project_corrupt"), true); return; }
                    timelineItems = projectData.TimelineItems ?? new List<TimelineItem>();
                    subtitles = projectData.Subtitles ?? new List<SubtitleItem>();
                    markers = projectData.Markers ?? new List<double>();
                    transitions = projectData.Transitions ?? new List<TransitionEffect>();
                    currentTrackFilter = projectData.CurrentTrackFilter;
                    zoomLevel = projectData.ZoomLevel;
                    currentProjectFolder = Path.GetDirectoryName(d.FileName) ?? string.Empty;
                    double currentTime = 0;
                    foreach (var item in timelineItems) { item.Start = currentTime; item.End = currentTime + item.Duration; currentTime += item.Duration; }
                    UpdateZoomDisplay();
                    UpdateTimelineDisplay();
                    UpdateMarkerDisplay();
                    lstSubtitles.Items.Clear();
                    foreach (var sub in subtitles) lstSubtitles.Items.Add($"{FormatTime(sub.Start)} -> {FormatTime(sub.End)}: {sub.Text}");
                    LogMessage(string.Format(L("project_loaded_clips"), Path.GetFileName(d.FileName), timelineItems.Count), true);
                    PlayBeep();

                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show(string.Format(L("project_load_error"), ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    LogMessage(string.Format(L("generic_error"), ex.Message), true);
                }
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private TimelineItem DeepCloneTimelineItem(TimelineItem source)
        {
            var clone = new TimelineItem
            {
                Path = source.Path,
                Duration = source.Duration,
                Name = source.Name,
                Type = source.Type,
                Volume = source.Volume,
                TrackIndex = source.TrackIndex,
                AudioDescription = source.AudioDescription,
                VideoEffect = source.VideoEffect != null ? new VideoEffectData
                {
                    Brightness = source.VideoEffect.Brightness,
                    Contrast = source.VideoEffect.Contrast,
                    Blur = source.VideoEffect.Blur,
                    Speed = source.VideoEffect.Speed
                } : null
            };
            foreach (var kf in source.Keyframes)
            {
                clone.Keyframes.Add(new AnimationKeyframe
                {
                    Time = kf.Time,
                    Zoom = kf.Zoom,
                    Rotation = kf.Rotation,
                    X = kf.X,
                    Y = kf.Y,
                    Opacity = kf.Opacity
                });
            }
            return clone;
        }

        public void SaveState()
        {
            var copy = new List<TimelineItem>();
            foreach (var item in timelineItems)
            {
                copy.Add(DeepCloneTimelineItem(item));
            }
            undoStack.Push(copy);
            redoStack.Clear();
        }

        private void Undo()
        {
            if (undoStack.Count > 0)
            {
                var previous = undoStack.Pop();
                var currentCopy = new List<TimelineItem>();
                foreach (var item in timelineItems) currentCopy.Add(DeepCloneTimelineItem(item));
                redoStack.Push(currentCopy);
                timelineItems = previous;
                UpdateTimelineDisplay();
                LogMessage(L("undo_done"), true);
                PlayBeep();
            }
            else LogMessage(L("no_more_undo2"), true);
        }

        private void Redo()
        {
            if (redoStack.Count > 0)
            {
                var next = redoStack.Pop();
                var currentCopy = new List<TimelineItem>();
                foreach (var item in timelineItems) currentCopy.Add(DeepCloneTimelineItem(item));
                undoStack.Push(currentCopy);
                timelineItems = next;
                UpdateTimelineDisplay();
                LogMessage("Ponovljeno", true);
                PlayBeep();
            }
            else LogMessage(L("no_more_redo2"), true);
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
        private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

        private void CopyClip_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                _copiedClip = DeepCloneTimelineItem(item);
                LogMessage($"Kopiran klip: {item.Name}", true);
                PlayBeep();
            }
            else LogMessage("Nema selektovanog klipa za kopiranje", true);
        }

        private void PasteClip_Click(object sender, RoutedEventArgs e)
        {
            if (_copiedClip == null)
            {
                LogMessage("Nema kopiranog klipa za lepljenje", true);
                return;
            }
            SaveState();
            var newClip = DeepCloneTimelineItem(_copiedClip);
            newClip.Name = _copiedClip.Name + " (kopija)";
            timelineItems.Add(newClip);
            UpdateTimelineDisplay();
            LogMessage("Klip nalepljen", true);
            PlayBeep();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (nativeListView?.Items.Count > 0)
                {
                    foreach (WinForms.ListViewItem item in nativeListView.Items)
                    {
                        item.Selected = true;
                    }
                    LogMessage($"Selektovano {nativeListView.Items.Count} klipova", true);
                    PlayBeep();
                }
                else
                {
                    LogMessage("Nema klipova za selektovanje", true);
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("select_error"), ex.Message), true);
            }
        }

        private void ClearAllMarkers_Click(object sender, RoutedEventArgs e)
        {
            markers.Clear();
            UpdateMarkerDisplay();
            LogMessage("Svi markeri obrisani", true);
            PlayBeep();
        }

        private void ResetZoom_Click(object sender, RoutedEventArgs e)
        {
            zoomLevel = 1.0;
            UpdateZoomDisplay();
            LogMessage("Zoom resetovan na 1.0x", true);
        }

        private void ShowLogWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_logWindow != null)
            {
                _logWindow.Show();
                _logWindow.Activate();
                LogMessage("Log prozor otvoren", true);
            }
        }

        private void ShowAllTracks_Click(object sender, RoutedEventArgs e)
        {
            currentTrackFilter = -1;
            UpdateTimelineDisplay();
            if (cmbTrackSelector != null)
                cmbTrackSelector.SelectedIndex = 4;
            LogMessage("Prikazane sve trake", true);
        }

        private void ShowVideoTracksOnly_Click(object sender, RoutedEventArgs e)
        {
            currentTrackFilter = 0;
            UpdateTimelineDisplay();
            if (cmbTrackSelector != null)
                cmbTrackSelector.SelectedIndex = 0;
            LogMessage("Prikazane samo video trake", true);
        }

        private void ShowAudioTracksOnly_Click(object sender, RoutedEventArgs e)
        {
            currentTrackFilter = 2;
            UpdateTimelineDisplay();
            if (cmbTrackSelector != null)
                cmbTrackSelector.SelectedIndex = 2;
            LogMessage("Prikazane samo audio trake", true);
        }

        private void MoveClipUp_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item && item.TrackIndex > 0)
            {
                SaveState();
                item.TrackIndex--;
                UpdateTimelineDisplay();
                LogMessage($"Klip pomeren na traku {item.TrackIndex + 1}", true);
                PlayBeep();
            }
            else LogMessage(L("cant_move_higher"), true);
        }

        private void MoveClipDown_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                item.TrackIndex++;
                UpdateTimelineDisplay();
                LogMessage($"Klip pomeren na traku {item.TrackIndex + 1}", true);
                PlayBeep();
            }
            else LogMessage(L("cant_move_lower"), true);
        }

        private void ResetEffects_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                if (item.VideoEffect != null)
                {
                    item.VideoEffect.Brightness = 0;
                    item.VideoEffect.Contrast = 0;
                    item.VideoEffect.Blur = 0;
                }
                LogMessage($"Efekti resetovani na {item.Name}", true);
                PlayBeep();
            }
            else LogMessage("Selektuj klip prvo", true);
        }

        private void ShowVideoEffectsTab_Click(object sender, RoutedEventArgs e) => tabEffects.IsSelected = true;
        private void ShowAudioEffectsTab_Click(object sender, RoutedEventArgs e) => tabEffects.IsSelected = true;
        private void ShowTransitionsTab_Click(object sender, RoutedEventArgs e) => tabTransitions.IsSelected = true;
        private void ShowAnimationTab_Click(object sender, RoutedEventArgs e) => tabAnimation.IsSelected = true;
        private void ShowSubtitlesTab_Click(object sender, RoutedEventArgs e) => tabSubtitles.IsSelected = true;

        private void ShowMarkersDialog_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0)
            {
                LogMessage("Nema markera", true);
                return;
            }
            string markerList = string.Join(", ", markers.Select(m => FormatTime(m)));
            WpfMessageBox.Show($"Markeri:\n{markerList}", "Lista markera", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ToggleGPUAcceleration_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                useGPUAcceleration = menuItem.IsChecked;
                chkGPUAcceleration.IsChecked = useGPUAcceleration;
                LogMessage(useGPUAcceleration ? L("gpu_on") : L("gpu_off"), true);
            }
        }

        private void ShowPreferences_Click(object sender, RoutedEventArgs e)
        {
            WpfMessageBox.Show(L("settings_wip"), L("settings_title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowUserManual_Click(object sender, RoutedEventArgs e)
        {
            WpfMessageBox.Show(L("manual_wip"), L("manual_title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            LogMessage(L("check_updates"), true);
            await Task.Delay(1000);
            WpfMessageBox.Show(L("up_to_date"), L("updates_title"), MessageBoxButton.OK, MessageBoxImage.Information);
            LogMessage(L("no_updates"), true);
        }

        private void ShowAbout_Click(object sender, RoutedEventArgs e)
        {
            WpfMessageBox.Show(L("about_text"), L("about_title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetupDragDropFromExplorer()
        {
            this.AllowDrop = true;
            this.Drop += Window_Drop;
            this.DragEnter += Window_DragEnter;
            this.DragOver += Window_DragOver;
        }

        private void Window_DragEnter(object sender, WpfDragEventArgs e)
        {
            if (e.Data.GetDataPresent(WpfDataFormats.FileDrop))
            {
                e.Effects = WpfDragDropEffects.Copy;
                if (accessibilityMode) LogMessage("Prevucite fajlove za dodavanje na timeline", true);
            }
            else
            {
                e.Effects = WpfDragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_DragOver(object sender, WpfDragEventArgs e)
        {
            if (e.Data.GetDataPresent(WpfDataFormats.FileDrop))
            {
                e.Effects = WpfDragDropEffects.Copy;
            }
            else
            {
                e.Effects = WpfDragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void Window_Drop(object sender, WpfDragEventArgs e)
        {
            if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop))
            {
                if (accessibilityMode) LogMessage(L("no_drag_data"), true);
                return;
            }
            string[] files = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
            if (files == null || files.Length == 0)
            {
                if (accessibilityMode) LogMessage("Nema fajlova", true);
                return;
            }
            var supportedExtensions = new[] { ".mp4", ".avi", ".mov", ".mkv", ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".jpg", ".jpeg", ".png", ".bmp" };
            var validFiles = files.Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower())).ToArray();
            if (validFiles.Length == 0)
            {
                if (accessibilityMode) LogMessage(L("unsupported_file"), true);
                return;
            }
            if (accessibilityMode) LogMessage($"Dodavanje {validFiles.Length} fajlova...", true);
            SaveState();
            int videoCount = 0, audioCount = 0, imageCount = 0;
            foreach (string file in validFiles)
            {
                string ext = Path.GetExtension(file).ToLower();
                bool isImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";
                bool isAudio = ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".flac" || ext == ".ogg";
                try
                {
                    double duration = 5.0;
                    string type;
                    int targetTrack = 0;
                    if (isImage)
                    {
                        type = "Image";
                        duration = 5.0;
                        targetTrack = 0;
                        imageCount++;
                    }
                    else if (isAudio)
                    {
                        type = "Audio";
                        duration = await GetMediaDurationWithTimeoutAsync(file, TimeSpan.FromSeconds(30));
                        targetTrack = 2;
                        audioCount++;
                    }
                    else
                    {
                        type = "Video";
                        duration = await GetMediaDurationWithTimeoutAsync(file, TimeSpan.FromSeconds(30));
                        targetTrack = 0;
                        videoCount++;
                    }
                    timelineItems.Add(new TimelineItem
                    {
                        Path = file,
                        Duration = duration,
                        Name = Path.GetFileName(file),
                        Type = type,
                        Volume = 100,
                        TrackIndex = targetTrack,
                        VideoEffect = new VideoEffectData()
                    });
                }
                catch (Exception ex)
                {
                    if (accessibilityMode) LogMessage(string.Format(L("add_file_error"), Path.GetFileName(file), ex.Message), true);
                }
            }
            UpdateTimelineDisplay();
            if (accessibilityMode) LogMessage($"Dodato: {videoCount} video, {audioCount} audio, {imageCount} slika", true);
            PlayBeep();
            e.Handled = true;
        }

        private async Task<double> GetMediaDurationWithTimeoutAsync(string filePath, TimeSpan timeout)
        {
            try
            {
                var task = MediaLoader.GetMediaDurationAsync(filePath, _libVLC);
                var completedTask = await Task.WhenAny(task, Task.Delay(timeout));
                if (completedTask == task)
                {
                    return await task;
                }
                else
                {
                    LogMessage(string.Format(L("duration_timeout"), Path.GetFileName(filePath)), true);
                    return 5.0;
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("duration_error"), Path.GetFileName(filePath), ex.Message), true);
                return 5.0;
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (zoomLevel < 5.0)
            {
                zoomLevel = Math.Min(5.0, zoomLevel + 0.5);
                UpdateZoomDisplay();
                LogMessage(string.Format(L("zoom_level"), zoomLevel), false);
            }
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (zoomLevel > 0.5)
            {
                zoomLevel = Math.Max(0.5, zoomLevel - 0.5);
                UpdateZoomDisplay();
                LogMessage($"Zoom smanjen {zoomLevel:F1}x", false);
            }
        }

        private void AddMarker_Click(object sender, RoutedEventArgs e)
        {
            if (currentPlaybackPosition >= 0)
            {
                markers.Add(currentPlaybackPosition);
                markers.Sort();
                UpdateMarkerDisplay();
                LogMessage($"Marker dodat {FormatTime(currentPlaybackPosition)}", true);
                PlayBeep();
            }
        }

        private void NextMarker_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0) { LogMessage("Nema markera", true); return; }
            var next = markers.FirstOrDefault(m => m > currentPlaybackPosition + 0.1);
            if (next > 0)
            {
                _mediaPlayer.Time = (long)(next * 1000);
                currentPlaybackPosition = next;
                UpdatePositionDisplay();
                LogMessage(string.Format(L("jumped_to_marker"), FormatTime(next)), true);
                PlayBeep();
            }
            else
            {
                _mediaPlayer.Time = (long)(markers[0] * 1000);
                currentPlaybackPosition = markers[0];
                UpdatePositionDisplay();
                LogMessage(string.Format(L("jumped_to_first"), FormatTime(markers[0])), true);
                PlayBeep();
            }
        }

        private void PrevMarker_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0) { LogMessage("Nema markera", true); return; }
            var prev = markers.LastOrDefault(m => m < currentPlaybackPosition - 0.1);
            if (prev > 0)
            {
                _mediaPlayer.Time = (long)(prev * 1000);
                currentPlaybackPosition = prev;
                UpdatePositionDisplay();
                LogMessage(string.Format(L("jumped_to_marker"), FormatTime(prev)), true);
                PlayBeep();
            }
            else
            {
                _mediaPlayer.Time = (long)(markers[markers.Count - 1] * 1000);
                currentPlaybackPosition = markers[markers.Count - 1];
                UpdatePositionDisplay();
                LogMessage(string.Format(L("jumped_to_last"), FormatTime(markers[markers.Count - 1])), true);
                PlayBeep();
            }
        }

        private void CutClip_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Selektuj klip prvo", true);
                return;
            }

            double cutPosition = currentPlaybackPosition - item.Start;
            if (cutPosition > 0.5 && cutPosition < item.Duration - 0.5)
            {
                SaveState();
                var secondPart = DeepCloneTimelineItem(item);
                secondPart.Duration = item.Duration - cutPosition;
                secondPart.Name = $"{item.Name} (deo 2)";
                secondPart.Keyframes.Clear();
                foreach (var kf in item.Keyframes.Where(k => k.Time > cutPosition))
                {
                    secondPart.Keyframes.Add(new AnimationKeyframe
                    {
                        Time = kf.Time - cutPosition,
                        Zoom = kf.Zoom,
                        Rotation = kf.Rotation,
                        X = kf.X,
                        Y = kf.Y,
                        Opacity = kf.Opacity
                    });
                }
                item.Keyframes.RemoveAll(k => k.Time > cutPosition);
                item.Duration = cutPosition;
                item.Name = $"{item.Name} (deo 1)";
                int originalIndex = timelineItems.IndexOf(item);
                timelineItems.Insert(originalIndex + 1, secondPart);
                UpdateTimelineDisplay();
                LogMessage(string.Format(L("clip_cut_at"), FormatTime(cutPosition)), true);
                PlayBeep();
            }
            else
            {
                LogMessage(L("position_too_close"), true);
            }
        }

        private void CutAtAllMarkers_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0) { LogMessage(L("no_markers_to_cut"), true); return; }
            SaveState();
            var newItems = new List<TimelineItem>();
            foreach (var item in timelineItems)
            {
                var markersInItem = markers.Where(m => m > item.Start && m < item.End).OrderBy(m => m).ToList();
                if (markersInItem.Count == 0)
                {
                    newItems.Add(item);
                    continue;
                }
                double currentOffset = 0;
                int partIndex = 1;
                foreach (var marker in markersInItem)
                {
                    double cutPos = marker - item.Start;
                    if (cutPos > 0.5 && cutPos < item.Duration - 0.5)
                    {
                        var part = DeepCloneTimelineItem(item);
                        part.Duration = cutPos - currentOffset;
                        part.Name = $"{item.Name} (deo {partIndex})";
                        part.Keyframes.Clear();
                        foreach (var kf in item.Keyframes.Where(k => k.Time > currentOffset && k.Time <= cutPos))
                        {
                            part.Keyframes.Add(new AnimationKeyframe
                            {
                                Time = kf.Time - currentOffset,
                                Zoom = kf.Zoom,
                                Rotation = kf.Rotation,
                                X = kf.X,
                                Y = kf.Y,
                                Opacity = kf.Opacity
                            });
                        }
                        newItems.Add(part);
                        currentOffset = cutPos;
                        partIndex++;
                    }
                }
                var lastPart = DeepCloneTimelineItem(item);
                lastPart.Duration = item.Duration - currentOffset;
                lastPart.Name = $"{item.Name} (deo {partIndex})";
                lastPart.Keyframes.Clear();
                foreach (var kf in item.Keyframes.Where(k => k.Time > currentOffset))
                {
                    lastPart.Keyframes.Add(new AnimationKeyframe
                    {
                        Time = kf.Time - currentOffset,
                        Zoom = kf.Zoom,
                        Rotation = kf.Rotation,
                        X = kf.X,
                        Y = kf.Y,
                        Opacity = kf.Opacity
                    });
                }
                newItems.Add(lastPart);
            }
            timelineItems = newItems;
            UpdateTimelineDisplay();
            LogMessage(L("cut_all_done"), true);
            PlayBeep();
        }

        private void MoveLeft_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                int oldIndex = timelineItems.IndexOf(item);
                if (oldIndex > 0)
                {
                    timelineItems.RemoveAt(oldIndex);
                    timelineItems.Insert(oldIndex - 1, item);
                    UpdateTimelineDisplay();
                    LogMessage("Klip pomeren levo", true);
                    PlayBeep();
                }
                else LogMessage("Nema klipa levo", true);
            }
        }

        private void MoveRight_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                int oldIndex = timelineItems.IndexOf(item);
                if (oldIndex < timelineItems.Count - 1)
                {
                    timelineItems.RemoveAt(oldIndex);
                    timelineItems.Insert(oldIndex + 1, item);
                    UpdateTimelineDisplay();
                    LogMessage("Klip pomeren desno", true);
                    PlayBeep();
                }
                else LogMessage("Nema klipa desno", true);
            }
        }

        private async void SetClipDuration_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                var dialog = new DurationDialog(item.Duration);
                if (dialog.ShowDialog() == true)
                {
                    item.Duration = dialog.Duration;
                    UpdateTimelineDisplay();
                    LogMessage($"Trajanje promenjeno na {FormatTime(item.Duration)}", true);
                    PlayBeep();
                }
            }
            else LogMessage("Selektuj klip prvo", true);
        }

        private async void SetClipVolume_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                var dialog = new VolumeControl(item.Volume);
                if (dialog.ShowDialog() == true)
                {
                    SaveState();
                    item.Volume = dialog.Volume;
                    LogMessage(string.Format(L("volume_set2"), dialog.Volume), true);
                    PlayBeep();
                    if (_mediaPlayer != null && _mediaPlayer.IsPlaying) _mediaPlayer.Volume = (int)item.Volume;
                }
            }
            else LogMessage("Selektuj klip prvo", true);
        }

        private void RemoveFromTimeline_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                timelineItems.Remove(item);
                UpdateTimelineDisplay();
                LogMessage("Klip obrisan", true);
                PlayBeep();
            }
        }

        private void TogglePlay_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMedia == null && timelineItems.Count > 0)
            {
                var firstItem = timelineItems.FirstOrDefault();
                if (firstItem != null)
                {
                    _currentMedia = new Media(_libVLC, firstItem.Path);
                    _mediaPlayer.Media = _currentMedia;
                    _mediaPlayer.Volume = (int)firstItem.Volume;
                    _mediaPlayer.Play();
                    isPlaying = true;
                    if (btnPlay != null) btnPlay.Content = "⏸ PAUSE";
                }
                return;
            }
            if (_currentMedia == null)
            {
                LogMessage("Selektuj klip prvo", true);
                return;
            }
            if (isPlaying)
            {
                _mediaPlayer.Pause();
                isPlaying = false;
                if (btnPlay != null) btnPlay.Content = "▶ PLAY";
                LogMessage($"Pauzirano na {FormatTimeDetailed(currentPlaybackPosition)}", true);
            }
            else
            {
                _mediaPlayer.Play();
                isPlaying = true;
                if (btnPlay != null) btnPlay.Content = "⏸ PAUSE";
                // Najavimo koji klip se pušta i njegovo trajanje
                var selItem = nativeListView?.SelectedItems.Count > 0
                    ? nativeListView.SelectedItems[0].Tag as TimelineItem : null;
                if (selItem != null)
                    LogMessage($"Reprodukcija: {selItem.Name}, trajanje {FormatTime(selItem.Duration)}", true);
                else
                    LogMessage("Reprodukcija pokrenuta", true);
            }
        }

        private void SeekForward_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            long newTime = _mediaPlayer.Time + 5000;
            if (newTime > _mediaPlayer.Length) newTime = _mediaPlayer.Length;
            _mediaPlayer.Time = newTime;
            currentPlaybackPosition = newTime / 1000.0;
            UpdatePositionDisplay();
            if (accessibilityMode) LogMessage($"Premotano napred {FormatTime(currentPlaybackPosition)}", true);
        }

        private void SeekBack_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            long newTime = _mediaPlayer.Time - 5000;
            if (newTime < 0) newTime = 0;
            _mediaPlayer.Time = newTime;
            currentPlaybackPosition = newTime / 1000.0;
            UpdatePositionDisplay();
            if (accessibilityMode) LogMessage($"Premotano unazad {FormatTime(currentPlaybackPosition)}", true);
        }

        private void AddVideoTrack_Click(object sender, RoutedEventArgs e)
        {
            int maxTrackIndex = timelineItems.Any() ? timelineItems.Max(x => x.TrackIndex) : 0;
            int newTrackIndex = maxTrackIndex + 1;
            LogMessage($"Dodata nova video traka {newTrackIndex + 1}", true);
            PlayBeep();
        }

        private void AddAudioTrack_Click(object sender, RoutedEventArgs e)
        {
            int maxTrackIndex = timelineItems.Any() ? timelineItems.Max(x => x.TrackIndex) : 2;
            int newTrackIndex = maxTrackIndex + 1;
            LogMessage($"Dodata nova audio traka {newTrackIndex + 1}", true);
            PlayBeep();
        }

        private void TrackSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTrackSelector.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                currentTrackFilter = int.Parse(item.Tag.ToString());
                UpdateTimelineDisplay();
                LogMessage($"Prikazana traka: {((ComboBoxItem)cmbTrackSelector.SelectedItem).Content}", true);
            }
        }

        private void ApplyEffects_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                if (item.VideoEffect == null) item.VideoEffect = new VideoEffectData();
                item.VideoEffect.Brightness = sldBrightness?.Value ?? 0;
                item.VideoEffect.Contrast = sldContrast?.Value ?? 0;
                item.VideoEffect.Blur = sldBlur?.Value ?? 0;
                LogMessage($"Efekti primenjeni na {item.Name}", true);
                PlayBeep();
            }
            else LogMessage("Selektuj klip prvo", true);
        }

        private void UpdateAudioEffects()
        {
            if (_equalizer == null) return;
            try
            {
                float bassGain = (float)(sldBass.Value / 2.0);
                _equalizer.SetAmp(bassGain, 0);
                _equalizer.SetAmp(bassGain * 0.7f, 1);
                float trebleGain = (float)(sldTreble.Value / 2.0);
                _equalizer.SetAmp(trebleGain, 7);
                _equalizer.SetAmp(trebleGain * 0.9f, 8);
                _equalizer.SetAmp(trebleGain * 0.8f, 9);
            }
            catch { }
        }

        private void AudioEffect_ValueChanged(object sender, RoutedEventArgs e) => UpdateAudioEffects();

        private async void PreviewBass_Click(object sender, RoutedEventArgs e) => await PreviewAudioEffect(sldBass.Value, 0, 0);
        private async void PreviewTreble_Click(object sender, RoutedEventArgs e) => await PreviewAudioEffect(0, sldTreble.Value, 0);
        private async void PreviewReverb_Click(object sender, RoutedEventArgs e) => await PreviewAudioEffect(0, 0, sldReverb.Value);

        private async Task PreviewAudioEffect(double bass, double treble, double reverb)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item) || !item.IsAudio)
            {
                LogMessage(L("select_audio_clip2"), true);
                return;
            }
            if (!File.Exists(item.Path))
            {
                LogMessage("Audio fajl ne postoji", true);
                return;
            }
            try
            {
                LogMessage(string.Format(L("previewing_audio"), previewDurationSeconds), true);
                double oldBass = sldBass.Value;
                double oldTreble = sldTreble.Value;
                double oldReverb = sldReverb.Value;
                bool oldCompressor = chkCompressor.IsChecked == true;
                if (bass > 0) sldBass.Value = bass;
                if (treble > 0) sldTreble.Value = treble;
                if (reverb > 0) sldReverb.Value = reverb;
                UpdateAudioEffects();
                long savedPosition = 0;
                bool wasPlaying = false;
                if (_currentMedia != null)
                {
                    savedPosition = _mediaPlayer.Time;
                    wasPlaying = isPlaying;
                    if (wasPlaying) _mediaPlayer.Pause();
                }
                using (var previewMedia = new Media(_libVLC, item.Path))
                {
                    _mediaPlayer.Media = previewMedia;
                    _mediaPlayer.Volume = (int)item.Volume;
                    _mediaPlayer.Time = 0;
                    _mediaPlayer.Play();
                    await Task.Delay(previewDurationSeconds * 1000);
                    _mediaPlayer.Pause();
                }
                sldBass.Value = oldBass;
                sldTreble.Value = oldTreble;
                sldReverb.Value = oldReverb;
                chkCompressor.IsChecked = oldCompressor;
                UpdateAudioEffects();
                if (_currentMedia != null)
                {
                    _mediaPlayer.Media = _currentMedia;
                    _mediaPlayer.Time = savedPosition;
                    if (wasPlaying) _mediaPlayer.Play();
                }
                LogMessage(L("preview_done"), true);
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("generic_error"), ex.Message), true);
            }
        }

        private void GPUAcceleration_Checked(object sender, RoutedEventArgs e)
        {
            useGPUAcceleration = chkGPUAcceleration.IsChecked == true;
            LogMessage(useGPUAcceleration ? L("gpu_on2") : L("gpu_off2"), true);
        }

        private void LoadTransitions()
        {
            if (lstTransitions == null) return;
            lstTransitions.Items.Clear();
            lstTransitions.Items.Add(new TransitionEffect { Name = "Fade In/Out", Type = TransitionType.Fade });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Crossfade", Type = TransitionType.Crossfade });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Slide Left", Type = TransitionType.SlideLeft });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Slide Right", Type = TransitionType.SlideRight });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Slide Up", Type = TransitionType.SlideUp });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Slide Down", Type = TransitionType.SlideDown });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Wipe Left", Type = TransitionType.WipeLeft });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Wipe Right", Type = TransitionType.WipeRight });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Zoom In", Type = TransitionType.ZoomIn });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Zoom Out", Type = TransitionType.ZoomOut });
        }

        private void SetupDragDrop()
        {
            // Drag drop za Win32 ListView
        }

        private void Transition_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (lstTransitions.SelectedItem is TransitionEffect transition)
            {
                WpfDragDrop.DoDragDrop(lstTransitions, transition, WpfDragDropEffects.Copy);
            }
        }

        private void Timeline_Drop(object sender, WpfDragEventArgs e)
        {
            if (e.Data.GetData(typeof(TransitionEffect)) is TransitionEffect transition)
            {
                WpfPoint dropPoint = e.GetPosition(wfhTimeline);
                int dropIndex = GetDropIndex(dropPoint);
                if (dropIndex >= 0 && dropIndex < timelineItems.Count - 1)
                {
                    transition.Duration = transitionDuration;
                    transition.ClipIndex1 = dropIndex;
                    transition.ClipIndex2 = dropIndex + 1;
                    transitions.RemoveAll(t => t.ClipIndex1 == dropIndex || t.ClipIndex2 == dropIndex + 1);
                    transitions.Add(transition);
                    LogMessage(string.Format(L("transition_added"), transition.Name, dropIndex + 1, dropIndex + 2), true);
                    PlayBeep();
                }
                else
                {
                    LogMessage(L("transition_only_between"), true);
                }
            }
        }

        private int GetDropIndex(WpfPoint dropPoint)
        {
            if (nativeListView == null) return -1;
            int index = (int)(dropPoint.Y / 30);
            return Math.Clamp(index, 0, timelineItems.Count - 1);
        }

        private void sldTransitionDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            transitionDuration = sldTransitionDuration.Value;
            if (accessibilityMode) LogMessage($"Trajanje tranzicije: {transitionDuration:F1} sekundi", true);
        }

        private void Transition_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (lstTransitions.SelectedItem is TransitionEffect transition)
            {
                LogMessage(string.Format(L("effect_drag_tip"), transition.Name), true);
            }
        }

        private void RemoveTransition_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                int idx = timelineItems.IndexOf(item);
                transitions.RemoveAll(t => t.ClipIndex1 == idx || t.ClipIndex2 == idx);
                LogMessage("Tranzicija uklonjena", true);
                PlayBeep();
                UpdateTransitionIndicators();
            }
            else
            {
                LogMessage("Selektuj prvi klip od para za uklanjanje tranzicije", true);
            }
        }

        private void Keyframe_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (lstKeyframes.SelectedItem is AnimationKeyframe kf)
            {
                currentEditingKeyframe = kf;
                sldZoom.Value = kf.Zoom;
                sldRotation.Value = kf.Rotation;
                sldX.Value = kf.X;
                sldY.Value = kf.Y;
                sldOpacity.Value = kf.Opacity;
                UpdateAnimValueDisplays();
                LogMessage($"Keyframe na {FormatTime(kf.Time)} sekundi", true);
            }
        }

        private void AnimValue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateAnimValueDisplays();

        private void UpdateAnimValueDisplays()
        {
            if (txtZoomValue != null) txtZoomValue.Text = $"{sldZoom.Value:F2}";
            if (txtRotationValue != null) txtRotationValue.Text = $"{sldRotation.Value:F0}°";
            if (txtXValue != null) txtXValue.Text = $"{sldX.Value:F0}";
            if (txtYValue != null) txtYValue.Text = $"{sldY.Value:F0}";
            if (txtOpacityValue != null) txtOpacityValue.Text = $"{sldOpacity.Value:F2}";
        }

        private void AddKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Selektuj klip prvo", true);
                return;
            }
            double time = currentPlaybackPosition - item.Start;
            if (time < 0) time = 0;
            if (time > item.Duration) time = item.Duration;
            var kf = new AnimationKeyframe
            {
                Time = time,
                Zoom = sldZoom.Value,
                Rotation = sldRotation.Value,
                X = sldX.Value,
                Y = sldY.Value,
                Opacity = sldOpacity.Value
            };
            item.AddKeyframe(kf);
            UpdateKeyframeList(item);
            LogMessage($"Keyframe dodat na {FormatTime(time)}", true);
            PlayBeep();
        }
        private void AddKenBurnsKeyframes(TimelineItem item, double duration)
        {
            item.Keyframes.Clear();
            item.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0, X = 0, Y = 0 });
            item.Keyframes.Add(new AnimationKeyframe { Time = duration, Zoom = 1.2, X = 50, Y = 30 });
        }
        private void RemoveKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item)) return;
            if (lstKeyframes.SelectedItem is AnimationKeyframe kf)
            {
                item.RemoveKeyframeAt(kf.Time);
                UpdateKeyframeList(item);
                LogMessage("Keyframe uklonjen", true);
                PlayBeep();
            }
        }

        private void ApplyKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item)) return;
            if (currentEditingKeyframe != null)
            {
                currentEditingKeyframe.Zoom = sldZoom.Value;
                currentEditingKeyframe.Rotation = sldRotation.Value;
                currentEditingKeyframe.X = sldX.Value;
                currentEditingKeyframe.Y = sldY.Value;
                currentEditingKeyframe.Opacity = sldOpacity.Value;
                UpdateKeyframeList(item);
                LogMessage(L("keyframe_updated"), true);
            }
            else
            {
                AddKeyframe_Click(sender, e);
            }
            PlayBeep();
        }

        private void PreviewAnimation_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Selektuj klip za pregled animacije", true);
                return;
            }
            LogMessage(L("animation_preview_note"), true);
        }

        private void UpdateKeyframeList(TimelineItem item)
        {
            if (lstKeyframes == null) return;
            lstKeyframes.Items.Clear();
            foreach (var kf in item.Keyframes.OrderBy(k => k.Time))
            {
                lstKeyframes.Items.Add(kf);
            }
            if (txtSelectedClip != null)
                txtSelectedClip.Text = $"Selektovan klip: {item.Name} (Keyframe-ova: {item.Keyframes.Count})";
        }

        private double GetAudioDuration(string filePath)
        {
            try
            {
                using (var media = new Media(_libVLC, filePath))
                {
                    media.AddOption("play-and-exit");
                    _ = media.Parse(MediaParseOptions.ParseNetwork);
                    Thread.Sleep(300);
                    return media.Duration / 1000.0;
                }
            }
            catch { return 5.0; }
        }

        private async void GenerateVoice_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentProjectFolder)) { WpfMessageBox.Show(L("save_project_first2")); return; }
            if (string.IsNullOrWhiteSpace(txtVoiceText.Text)) { LogMessage("Unesi tekst prvo", true); return; }
            btnGenerateVoice.IsEnabled = false;
            prgVoice.Visibility = Visibility.Visible;
            txtVoiceStatus.Text = "Generisanje...";
            LogMessage("Generisanje AI glasa...", true);
            string lang = rbSrb.IsChecked == true ? "sr" : "en";
            string voiceoverPath = Path.Combine(currentProjectFolder, "AI_Voiceover.mp3");
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                    string url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={Uri.EscapeDataString(txtVoiceText.Text)}&tl={lang}&client=tw-ob";
                    var audioData = await client.GetByteArrayAsync(url);
                    File.WriteAllBytes(voiceoverPath, audioData);
                }
                SaveState();
                timelineItems.Add(new TimelineItem { Path = voiceoverPath, Duration = GetAudioDuration(voiceoverPath), Name = "AI Glas", Type = "Audio", Volume = 100, TrackIndex = 2, VideoEffect = new VideoEffectData() });
                UpdateTimelineDisplay();
                LogMessage("AI glas generisan", true);
                txtVoiceStatus.Text = L("voice_done");
                PlayBeep();
            }
            catch (Exception ex) { LogMessage(string.Format(L("generic_error"), ex.Message), true); }
            finally { btnGenerateVoice.IsEnabled = true; prgVoice.Visibility = Visibility.Collapsed; }
        }
        private async void GenerateAI_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentProjectFolder)) { WpfMessageBox.Show(L("save_project_first2")); return; }
            if (string.IsNullOrWhiteSpace(txtAIPrompt?.Text)) { LogMessage("Unesi opis slike", true); return; }
            string apiToken = GetCloudflareApiKey();
            if (string.IsNullOrEmpty(apiToken))
            {
                var dialog = new ApiKeyDialog("cloudflare", "Cloudflare API token za generisanje slika");
                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.ApiKey))
                {
                    apiToken = dialog.ApiKey;
                    SaveCloudflareApiKey(apiToken);
                    LogMessage(L("cf_key_saved"), true);
                }
                else
                {
                    LogMessage(L("cf_key_cancelled"), true);
                    return;
                }
            }
            string accountId = "9b8004123c153014d851b6056d2da4fe";
            btnGenerate.IsEnabled = false;
            prgAI.Visibility = Visibility.Visible;
            txtAIStatus.Text = "Generisanje preko Cloudflare...";
            LogMessage("Generisanje AI kadrova preko Cloudflare Workers AI...", true);
            string[] prompts = txtAIPrompt.Text.Split(',');
            int generated = 0;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
                for (int idx = 0; idx < prompts.Length; idx++)
                {
                    string cleanP = prompts[idx].Trim();
                    if (string.IsNullOrEmpty(cleanP)) continue;
                    txtAIStatus.Text = $"{idx + 1}/{prompts.Length}: {cleanP} (Cloudflare)";
                    string img = Path.Combine(currentProjectFolder, $"AI_{Guid.NewGuid().ToString().Substring(0, 8)}.jpg");
                    try
                    {
                        string url = $"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/run/@cf/black-forest-labs/flux-1-schnell";
                        var requestBody = new { prompt = cleanP };
                        var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                        var response = await client.PostAsync(url, content);
                        var responseBody = await response.Content.ReadAsStringAsync();
                        if (response.IsSuccessStatusCode)
                        {
                            var result = JsonConvert.DeserializeObject<CloudflareImageResponse>(responseBody);
                            if (result?.result?.image != null)
                            {
                                byte[] imageBytes = Convert.FromBase64String(result.result.image);
                                await File.WriteAllBytesAsync(img, imageBytes);
                                SaveState();
                                var dialog = new DurationDialog(5.0);
                                double duration = dialog.ShowDialog() == true ? dialog.Duration : 5.0;
                                LogMessage("Generisem opis za: " + cleanP + "...", false);
                                string imgDesc = await GenerateImageDescriptionAsync(img, cleanP);
                                if (string.IsNullOrEmpty(imgDesc)) imgDesc = "AI generisana slika: " + cleanP;

                                timelineItems.Add(new TimelineItem
                                {
                                    Path = img,
                                    Duration = duration,
                                    Name = "AI: " + cleanP,
                                    AudioDescription = imgDesc,
                                    Type = "Image",
                                    Volume = 100,
                                    TrackIndex = 0,
                                    VideoEffect = new VideoEffectData()
                                });
                                generated++;
                                LogMessage("Slika " + (idx + 1) + " gotova. Opis: " + imgDesc, true);
                            }
                            else
                            {
                                LogMessage(string.Format(L("cf_no_image"), cleanP), true);
                            }
                        }
                        else
                        {
                            LogMessage(string.Format(L("cf_api_error"), response.StatusCode), true);
                            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) break;
                        }
                    }
                    catch (Exception ex) { LogMessage(string.Format(L("cf_gen_error"), cleanP, ex.Message), true); }
                    if (prgAI != null) prgAI.Value = (idx + 1) * 100 / prompts.Length;
                    await Task.Delay(500);
                }
            }
            UpdateTimelineDisplay();
            btnGenerate.IsEnabled = true;
            prgAI.Visibility = Visibility.Collapsed;
            txtAIStatus.Text = string.Format(L("frames_done"), generated, prompts.Length);
            LogMessage($"AI kadrovi generisani: {generated}/{prompts.Length}", true);
            PlayBeep();
        }

        private void AudioClip_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (cmbAudioClips.SelectedItem is TimelineItem item && item.IsAudio)
            {
                selectedAudioPath = item.Path;
                LogMessage($"Selektovan audio klip: {item.Name}", true);
            }
        }

        private void TranscribeAudio_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTranscribeAudio.SelectedItem is TimelineItem item && item.IsAudio)
            {
                selectedTranscribeAudioPath = item.Path;
                LogMessage($"Selektovan audio za transkripciju: {item.Name}", true);
            }
        }

        private async void TranscribeAudio_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedTranscribeAudioPath))
            {
                LogMessage("Selektuj audio fajl za transkripciju", true);
                return;
            }

            if (!AITranscription.IsWhisperAvailable())
            {
                LogMessage("faster-whisper-xxl nije pronađen. Postavi faster-whisper-xxl.exe pored UltraVideoEditor.exe.", true);
                WpfMessageBox.Show(
                    "faster-whisper-xxl nije pronađen.\n\n" +
                    "Preuzmi faster-whisper-xxl.exe i postavi ga pored UltraVideoEditor.exe.\n\n" +
                    "Besplatno: https://github.com/Purfview/whisper-standalone-win/releases",
                    "Whisper nije instaliran", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnTranscribe.IsEnabled = false;
            txtTranscriptionResult.Text = "Transkripcija u toku (large-v3)...";
            LogMessage(L("transcription_running"), true);
            try
            {
                string language = rbSrb.IsChecked == true ? "sr" : (rbEng.IsChecked == true ? "en" : "de");
                string ffmpegPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                var transcriptionResult = await AITranscription.TranscribeAsync(
                    selectedTranscribeAudioPath,
                    language: language,
                    ffmpegPath: ffmpegPath,
                    modelSize: "large-v3");
                string result = transcriptionResult.Success
                    ? transcriptionResult.FullText
                    : transcriptionResult.ErrorMessage;
                txtTranscriptionResult.Text = result;
                LogMessage(L("transcription_done2"), true);
                if (WpfMessageBox.Show(L("add_as_subtitles"), L("add_subtitles_title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    var sentences = result.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
                    double duration = GetAudioDuration(selectedTranscribeAudioPath);
                    double timePerSentence = duration / Math.Max(sentences.Length, 1);
                    for (int i = 0; i < sentences.Length; i++)
                    {
                        string sentence = sentences[i].Trim();
                        if (!string.IsNullOrEmpty(sentence))
                        {
                            subtitles.Add(new SubtitleItem { Text = sentence + ".", Start = i * timePerSentence, End = (i + 1) * timePerSentence });
                            lstSubtitles.Items.Add($"{FormatTime(i * timePerSentence)} -> {FormatTime((i + 1) * timePerSentence)}: {sentence}.");
                        }
                    }
                    LogMessage($"Dodato {sentences.Length} titlova iz transkripta", true);
                }
            }
            catch (Exception ex)
            {
                txtTranscriptionResult.Text = string.Format(L("generic_error"), ex.Message);
                LogMessage(string.Format(L("at_error"), ex.Message), true);
            }
            finally { btnTranscribe.IsEnabled = true; }
        }

        private async void SyncLyrics_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedAudioPath)) { LogMessage("Selektuj audio klip prvo", true); return; }
            if (string.IsNullOrWhiteSpace(txtLyrics.Text)) { LogMessage("Unesi tekst pesme", true); return; }
            btnSyncLyrics.IsEnabled = false;
            prgSync.Visibility = Visibility.Visible;
            txtSyncStatus.Text = "Sinhronizacija...";
            LogMessage("Sinhronizacija teksta...", true);
            try
            {
                string[] lines = txtLyrics.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var lyrics = lines.Where(l => !string.IsNullOrWhiteSpace(l.Trim())).ToList();
                if (lyrics.Count == 0) { LogMessage("Nema ispravnih stihova", true); return; }
                double duration = GetAudioDuration(selectedAudioPath);
                await Task.Delay(500);
                syncedSubtitles.Clear();
                double timePerLine = duration / lyrics.Count;
                for (int i = 0; i < lyrics.Count; i++)
                {
                    syncedSubtitles.Add(new AISubtitle { Text = lyrics[i], Start = i * timePerLine, End = (i + 1) * timePerLine });
                }
                lstAutoSubtitles.Items.Clear();
                foreach (var sub in syncedSubtitles)
                    lstAutoSubtitles.Items.Add($"[{FormatTime(sub.Start)} -> {FormatTime(sub.End)}] {sub.Text}");
                txtSyncStatus.Text = string.Format(L("subtitles_done"), syncedSubtitles.Count);
                LogMessage(string.Format(L("subtitles_done"), syncedSubtitles.Count), true);
                PlayBeep();
            }
            catch (Exception ex) { LogMessage(string.Format(L("generic_error"), ex.Message), true); }
            finally { btnSyncLyrics.IsEnabled = true; prgSync.Visibility = Visibility.Collapsed; }
        }

        private void AddSyncedSubtitles_Click(object sender, RoutedEventArgs e)
        {
            if (syncedSubtitles.Count == 0) { LogMessage("Nema titlova za dodavanje", true); return; }
            SaveState();
            foreach (var sub in syncedSubtitles)
            {
                subtitles.Add(new SubtitleItem { Text = sub.Text, Start = sub.Start, End = sub.End });
                lstSubtitles.Items.Add($"{FormatTime(sub.Start)} -> {FormatTime(sub.End)}: {sub.Text}");
            }
            LogMessage($"Dodato {syncedSubtitles.Count} titlova", true);
            PlayBeep();
        }

        private void AddSubtitle_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSubtitleText?.Text)) { LogMessage("Unesi tekst titla", true); return; }
            if (!double.TryParse(txtSubStart?.Text, out double start) || !double.TryParse(txtSubEnd?.Text, out double end)) { LogMessage(L("time_format_error"), true); return; }
            subtitles.Add(new SubtitleItem { Text = txtSubtitleText.Text, Start = start, End = end });
            lstSubtitles.Items.Add($"{FormatTime(start)} -> {FormatTime(end)}: {txtSubtitleText.Text}");
            txtSubtitleText.Clear();
            LogMessage("Titl dodat", true);
            PlayBeep();
        }

        private void ClearSubtitles_Click(object sender, RoutedEventArgs e)
        {
            subtitles.Clear();
            lstSubtitles.Items.Clear();
            LogMessage("Titlovi obrisani", true);
            PlayBeep();
        }

        private void ShowExportOptions_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ExportOptions(currentExportSettings);
            if (dialog.ShowDialog() == true)
            {
                currentExportSettings = dialog.Settings;
                LogMessage($"Opcije izvoza: {currentExportSettings.Format}, {currentExportSettings.Quality}", true);
            }
        }

        private async void AddVideoImage_Click(object sender, RoutedEventArgs e)
        {
            var d = new WpfOpenFileDialog { Multiselect = true, Filter = "Video i slike|*.mp4;*.avi;*.mov;*.mkv;*.jpg;*.png;*.jpeg;*.bmp" };
            if (d.ShowDialog() == true)
            {
                SaveState();
                int targetTrack = currentTrackFilter >= 0 && currentTrackFilter <= 1 ? currentTrackFilter : 0;
                foreach (var f in d.FileNames)
                {
                    if (!MediaLoader.IsValidMediaFile(f)) { LogMessage(string.Format(L("not_valid_media"), Path.GetFileName(f)), true); continue; }
                    try
                    {
                        double duration = 5.0;
                        string type = "Video";
                        string ext = Path.GetExtension(f).ToLower();
                        if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp") { type = "Image"; duration = 5.0; }
                        else duration = await GetMediaDurationWithTimeoutAsync(f, TimeSpan.FromSeconds(30));
                        string autoDesc = "";
                        if (type == "Image")
                        {
                            LogMessage("Generisem opis za " + Path.GetFileName(f) + "...", false);
                            autoDesc = await GenerateImageDescriptionAsync(f);
                            if (string.IsNullOrEmpty(autoDesc))
                                autoDesc = "Slika: " + Path.GetFileNameWithoutExtension(f);
                        }
                        timelineItems.Add(new TimelineItem
                        {
                            Path = f,
                            Duration = duration,
                            Name = Path.GetFileName(f),
                            AudioDescription = autoDesc,
                            Type = type,
                            Volume = 100,
                            TrackIndex = targetTrack,
                            VideoEffect = new VideoEffectData()
                        });
                        if (!string.IsNullOrEmpty(autoDesc))
                            LogMessage("Dodato: " + Path.GetFileName(f) + ". Opis: " + autoDesc, true);
                    }
                    catch (Exception ex) { LogMessage(string.Format(L("generic_error"), ex.Message), true); }
                }
                UpdateTimelineDisplay();
                LogMessage($"Stavki dodato {d.FileNames.Length}", true);
                PlayBeep();
            }
        }

        private async void AddAudioOnly_Click(object sender, RoutedEventArgs e)
        {
            var d = new WpfOpenFileDialog { Multiselect = true, Filter = "Audio|*.mp3;*.wav;*.m4a;*.flac;*.ogg" };
            if (d.ShowDialog() == true)
            {
                SaveState();
                int targetTrack = (currentTrackFilter == 2 || currentTrackFilter == 3) ? currentTrackFilter : 2;
                foreach (var f in d.FileNames)
                {
                    if (!MediaLoader.IsValidMediaFile(f)) { LogMessage(string.Format(L("not_valid_audio"), Path.GetFileName(f)), true); continue; }
                    try
                    {
                        double duration = await GetMediaDurationWithTimeoutAsync(f, TimeSpan.FromSeconds(30));
                        timelineItems.Add(new TimelineItem { Path = f, Duration = duration, Name = "Audio: " + Path.GetFileName(f), Type = "Audio", Volume = 100, TrackIndex = targetTrack, VideoEffect = new VideoEffectData() });
                    }
                    catch (Exception ex) { LogMessage(string.Format(L("generic_error"), ex.Message), true); }
                }
                UpdateTimelineDisplay();
                LogMessage($"Stavki dodato {d.FileNames.Length}", true);
                PlayBeep();
            }
        }
        private async void FinalRender_Click(object sender, RoutedEventArgs e)
        {
            if (timelineItems.Count == 0)
            {
                LogMessage("Nema klipova za render", true);
                return;
            }

            if (string.IsNullOrEmpty(currentProjectFolder))
            {
                var dlg = new WpfSaveFileDialog { Filter = "Iskra Projekat|*.iskra", DefaultExt = "iskra", FileName = "projekat.iskra" };
                if (dlg.ShowDialog() == true)
                {
                    currentProjectFolder = Path.GetDirectoryName(dlg.FileName) ?? string.Empty;
                    var projectData = new ProjectData
                    {
                        TimelineItems = timelineItems,
                        Subtitles = subtitles,
                        Markers = markers,
                        Transitions = transitions,
                        CurrentTrackFilter = currentTrackFilter,
                        ZoomLevel = zoomLevel,
                        ProjectVersion = "5.0"
                    };
                    string json = JsonConvert.SerializeObject(projectData, Formatting.Indented);
                    await File.WriteAllTextAsync(dlg.FileName, json);
                }
                else
                {
                    LogMessage(L("save_project_first2"), true);
                    return;
                }
            }

            var saveDialog = new WpfSaveFileDialog
            {
                Filter = "MP4 video|*.mp4|WebM video|*.webm|AVI video|*.avi",
                DefaultExt = "mp4",
                FileName = "izlazni_video.mp4"
            };

            if (saveDialog.ShowDialog() != true) return;

            string outputPath = saveDialog.FileName;
            string format = Path.GetExtension(outputPath).ToLower().TrimStart('.');
            if (format == "mp4") format = "mp4";
            else if (format == "webm") format = "webm";
            else if (format == "avi") format = "avi";
            else format = "mp4";

            // Provjeri da li postoji audio za odabir rezolucije
            string selectedResolution = "1920x1080";
            var audioItem = timelineItems.FirstOrDefault(i => i.IsAudio);
            if (audioItem != null)
            {
                var resolutionDialog = new ResolutionDialog();
                if (resolutionDialog.ShowDialog() == true)
                {
                    selectedResolution = resolutionDialog.SelectedResolution;
                    _selectedResolution = selectedResolution; // sačuvaj za AIVideoCreator
                }
            }

            _renderCancellation = new CancellationTokenSource();

            prgRender.Visibility = Visibility.Visible;
            prgRender.Value = 0;
            btnRenderTool.IsEnabled = false;
            txtRenderStatus.Text = "Renderovanje...";

            LogMessage("Renderovanje pokrenuto...", true);

            try
            {
                string ffmpegExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                LogMessage($"Proveravam FFmpeg na: {ffmpegExePath}", true);

                if (!File.Exists(ffmpegExePath))
                {
                    LogMessage(L("ffmpeg_not_found"), true);
                    WpfMessageBox.Show(string.Format(L("ffmpeg_missing_msg"), ffmpegExePath),
                                    "FFmpeg nedostaje", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                LogMessage(L("ffmpeg_found"), true);

                var renderProgress = new Progress<int>(percent =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        prgRender.Value = percent;
                        if (accessibilityMode && percent % 10 == 0)
                        {
                            LogMessage($"Render napredak: {percent}%", true);
                        }
                    });
                });

                var renderSubtitles = subtitles.ToList();

                await _renderEngine.RenderSimpleAsync(
                    timelineItems,
                    outputPath,
                    format,
                    renderProgress,
                    renderSubtitles,
                    currentExportSettings,
                    _renderCancellation.Token,
                    useGPUAcceleration,
                    selectedResolution,  // PROSLIJEĐENA REZOLUCIJA
                    AIVideoCreator.FastRenderMode);  // BRZI RENDER (bez Ken Burns)

                if (File.Exists(outputPath))
                {
                    long fileSize = new FileInfo(outputPath).Length;
                    LogMessage(string.Format(L("render_done_log"), outputPath, fileSize / 1024 / 1024), true);
                    txtRenderStatus.Text = L("render_done_status");
                    PlayBeep();
                    WpfMessageBox.Show(string.Format(L("render_done_msg"), outputPath, fileSize / 1024 / 1024), L("render_done_title2"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    LogMessage(string.Format(L("render_no_file"), outputPath), true);
                    txtRenderStatus.Text = L("render_no_file_status");
                    WpfMessageBox.Show(string.Format(L("render_no_file_msg"), outputPath), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (OperationCanceledException)
            {
                LogMessage("Renderovanje otkazano", true);
                txtRenderStatus.Text = "Otkazano";
                PlayBeep();
            }
            catch (Exception ex)
            {
                LogMessage($"RENDER GRESKA: {ex.Message}", true);
                LogMessage($"Stack trace: {ex.StackTrace}", true);
                txtRenderStatus.Text = L("render_failed_status");
                WpfMessageBox.Show(string.Format(L("render_failed_msg"), ex.Message, ex.StackTrace), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                prgRender.Visibility = Visibility.Collapsed;
                btnRenderTool.IsEnabled = true;
                _renderCancellation = null;
            }
        }

        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            WpfMessageBox.Show(L("test_click"));
            LogMessage("Test dugme je kliknuto!", true);
        }

        private void ToggleAccessibilityMode_Click(object sender, RoutedEventArgs e)
        {
            accessibilityMode = !accessibilityMode;
            if (accessibilityMode)
            {
                borderAccessibilityStatus.Visibility = Visibility.Visible;
                txtAccessibilityStatus.Text = "♿ PRISTUPACNI MOD UKLJUCEN";
                try
                {
                    if (positionAnnounceTimer != null)
                        positionAnnounceTimer.Start();
                }
                catch { }
                LogMessage(L("accessibility_on"), true);
            }
            else
            {
                borderAccessibilityStatus.Visibility = Visibility.Collapsed;
                try
                {
                    if (positionAnnounceTimer != null)
                        positionAnnounceTimer.Stop();
                }
                catch { }
                LogMessage(L("accessibility_off"), true);
            }
            PlayBeep();
        }

        private void SetupKeyboardCommands()
        {
            this.PreviewKeyDown += (sender, e) =>
            {
                // Ctrl+G – Idi na broj klipa
                if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    var dialog = new NumericDialog(nativeListView?.Items.Count ?? 0);
                    if (dialog.ShowDialog() == true)
                    {
                        NavigateToItemByNumber(dialog.SelectedNumber);
                    }
                    e.Handled = true;
                }
                if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    NewProject_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    LoadProject_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    SaveProject_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.F12)
                {
                    SaveAsProject_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                else if (e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                else if (e.Key == Key.Delete) { e.Handled = false; }
                else if (e.Key == Key.F1) { e.Handled = false; }
                else if (e.Key == Key.F2) { e.Handled = false; }
                else if (e.Key == Key.F3) { e.Handled = false; }
                else if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    if (_logWindow != null) { _logWindow.Show(); _logWindow.Activate(); LogMessage("Log prozor otvoren", true); }
                    e.Handled = true;
                }
                else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    nativeListView?.Focus();
                    if (nativeListView?.Items.Count > 0 && nativeListView.SelectedItems.Count == 0)
                        nativeListView.Items[0].Selected = true;
                    LogMessage("Fokus na listi klipova. Koristite strelice gore/dole za navigaciju.", true);
                    e.Handled = true;
                }
                else if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    ToggleAccessibilityMode_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    FinalRender_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    FinalRender_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    SetClipDuration_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.V && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
                {
                    SetClipVolume_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    AddMarker_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    NextMarker_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    PrevMarker_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    AddKeyframe_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.K && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    RemoveKeyframe_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    TogglePlay_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    MoveLeft_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    MoveRight_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    MoveClipUp_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    MoveClipDown_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Add && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ZoomIn_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Subtract && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ZoomOut_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ResetZoom_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.X && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    CutAtAllMarkers_Click(null, null);
                    e.Handled = true;
                }
                else if ((e.Key == Key.Up || e.Key == Key.Down) && nativeListView?.Focused == true)
                {
                    int newIndex = -1;
                    if (nativeListView.SelectedItems.Count > 0)
                    {
                        int currentIndex = nativeListView.SelectedItems[0].Index;
                        newIndex = currentIndex + (e.Key == Key.Down ? 1 : -1);
                    }
                    else if (nativeListView.Items.Count > 0)
                    {
                        newIndex = 0;
                    }
                    if (newIndex >= 0 && newIndex < nativeListView.Items.Count)
                    {
                        nativeListView.Items[newIndex].Selected = true;
                        nativeListView.Items[newIndex].EnsureVisible();
                        if (nativeListView.SelectedItems[0].Tag is TimelineItem newItem)
                            LogMessage($"Selektovan klip {newItem.Index}: {newItem.Name}", true);
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.PageUp && nativeListView?.Focused == true)
                {
                    int newIndex = Math.Max(0, (nativeListView.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Index : 0) - 5);
                    nativeListView.Items[newIndex].Selected = true;
                    nativeListView.Items[newIndex].EnsureVisible();
                    LogMessage($"Klip {newIndex + 1} od {timelineItems.Count}", true);
                    e.Handled = true;
                }
                else if (e.Key == Key.PageDown && nativeListView?.Focused == true)
                {
                    int newIndex = Math.Min(timelineItems.Count - 1, (nativeListView.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Index : 0) + 5);
                    nativeListView.Items[newIndex].Selected = true;
                    nativeListView.Items[newIndex].EnsureVisible();
                    LogMessage($"Klip {newIndex + 1} od {timelineItems.Count}", true);
                    e.Handled = true;
                }
            };
        }

        private void ShowHelp_Click(object sender, RoutedEventArgs e)
        {
            var helpWindow = new HelpWindow();
            helpWindow.Owner = this;
            helpWindow.ShowDialog();
        }

        private void AddSubtitleToClip_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                var dialog = new TextOverlayDialog($"Dodaj titl na: {item.Name}");
                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
                {
                    subtitles.Add(new SubtitleItem { Text = dialog.Text, Start = item.Start, End = item.End });
                    lstSubtitles.Items.Add($"{FormatTime(item.Start)} -> {FormatTime(item.End)}: {dialog.Text}");
                    LogMessage($"Titl dodat na klip: {item.Name}", true);
                    PlayBeep();
                }
            }
            else LogMessage("Selektuj klip prvo", true);
        }

        private void AddIntroText_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TextOverlayDialog("Najavni tekst (naslov pesme)");
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
            {
                subtitles.Add(new SubtitleItem { Text = dialog.Text, Start = 0, End = 5 });
                lstSubtitles.Items.Add($"0:00 -> 0:05: {dialog.Text}");
                LogMessage($"Najavni tekst dodat: {dialog.Text}", true);
                PlayBeep();
            }
        }

        private void AddOutroText_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TextOverlayDialog("Odjavni tekst (autor, kanal)");
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
            {
                double totalDuration = GetTotalDuration();
                subtitles.Add(new SubtitleItem { Text = dialog.Text, Start = totalDuration - 5 > 0 ? totalDuration - 5 : 0, End = totalDuration });
                lstSubtitles.Items.Add($"{FormatTime(totalDuration - 5)} -> {FormatTime(totalDuration)}: {dialog.Text}");
                LogMessage($"Odjavni tekst dodat: {dialog.Text}", true);
                PlayBeep();
            }
        }

        private void AddTransitionBetweenSelected_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                int idx = timelineItems.IndexOf(item);
                if (idx >= 0 && idx < timelineItems.Count - 1)
                {
                    if (lstTransitions.SelectedItem is TransitionEffect transition)
                    {
                        transition.Duration = transitionDuration;
                        transition.ClipIndex1 = idx;
                        transition.ClipIndex2 = idx + 1;
                        transitions.Add(transition);
                        LogMessage(string.Format(L("transition_added"), transition.Name, idx + 1, idx + 2), true);
                        PlayBeep();
                    }
                    else LogMessage("Selektuj tranziciju prvo", true);
                }
                else LogMessage("Selektuj prvi klip od para (ne poslednji)", true);
            }
        }

        private void AddTransitionBetweenSpecific_Click(object sender, RoutedEventArgs e)
        {
            if (timelineItems.Count < 2) { LogMessage("Nema dovoljno klipova za dodavanje tranzicije", true); return; }
            if (lstTransitions.SelectedItem is not TransitionEffect transition) { LogMessage("Selektuj tranziciju prvo", true); return; }
            var dialog = new TransitionDialog(timelineItems.Count);
            if (dialog.ShowDialog() == true)
            {
                int clipIndex = dialog.ClipIndex - 1;
                if (clipIndex >= 0 && clipIndex < timelineItems.Count - 1)
                {
                    transition.Duration = transitionDuration;
                    transition.ClipIndex1 = clipIndex;
                    transition.ClipIndex2 = clipIndex + 1;
                    transitions.Add(transition);
                    LogMessage(string.Format(L("transition_added"), transition.Name, clipIndex + 1, clipIndex + 2), true);
                    PlayBeep();
                }
                else LogMessage("Neispravan broj klipa", true);
            }
        }

        private void ClearPrompt_Click(object sender, RoutedEventArgs e)
        {
            txtAIPrompt.Clear();
            LogMessage("Prompt obrisan", true);
            PlayBeep();
        }

        private void PastePrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (WpfClipboard.ContainsText())
                {
                    string text = WpfClipboard.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        txtAIPrompt.Text = text;
                        LogMessage(string.Format(L("prompt_pasted"), text.Length), true);
                        PlayBeep();
                    }
                    else
                    {
                        LogMessage("Clipboard je prazan", true);
                    }
                }
                else
                {
                    LogMessage(L("clipboard_no_text"), true);
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("paste_error"), ex.Message), true);
            }
        }

        private void AddAudioDescription_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item && item.IsImage)
            {
                var dialog = new TextOverlayDialog($"Audio opis za sliku: {item.Name}");
                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
                {
                    item.AudioDescription = dialog.Text;
                    LogMessage($"Audio opis dodat za sliku {item.Index}: {dialog.Text}", true);
                    UpdateTimelineDisplay();
                    PlayBeep();
                }
            }
            else
            {
                LogMessage("Selektujte sliku prvo", true);
            }
        }

        private void ReadAudioDescription_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item && item.IsImage)
            {
                if (!string.IsNullOrEmpty(item.AudioDescription))
                {
                    LogMessage($"Opis slike {item.Index}: {item.AudioDescription}", true);
                }
                else
                {
                    LogMessage($"Slika {item.Index} nema audio opis", true);
                }
            }
            else
            {
                LogMessage("Selektujte sliku prvo", true);
            }
        }

        private async void GenerateScenario_Click(object sender, RoutedEventArgs e)
        {
            LogMessage("GenerateScenario_Click pozvan", true);

            if (string.IsNullOrWhiteSpace(txtAnimationPrompt.Text))
            {
                LogMessage(L("enter_animation_desc"), true);
                return;
            }

            string apiToken = GetCloudflareApiKey();
            if (string.IsNullOrEmpty(apiToken))
            {
                var dialog = new ApiKeyDialog("cloudflare", "Cloudflare API token za generisanje scenarija");
                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.ApiKey))
                {
                    apiToken = dialog.ApiKey;
                    SaveCloudflareApiKey(apiToken);
                    LogMessage(L("cf_api_key_saved2"), true);
                }
                else
                {
                    LogMessage(L("cf_api_cancelled2"), true);
                    return;
                }
            }

            var availableImages = timelineItems.Where(i => i.IsImage).Select(i => i.Name).ToList();
            if (availableImages.Count == 0)
            {
                LogMessage("Nema slika na timeline-u za kreiranje animacije", true);
                return;
            }

            LogMessage(L("ai_generating_scenario"), true);

            string prompt = $@"Kreiraj scenario za animaciju na osnovu sljedećeg zahtjeva: '{txtAnimationPrompt.Text}'
Dostupne slike: {string.Join(", ", availableImages)}
Rezultat vrati isključivo u JSON formatu bez dodatnog teksta. JSON treba da bude niz scena. Svaka scena treba da ima: 'imageName' (ime slike iz liste dostupnih slika), 'duration' (broj u sekundama, između 3 i 10), 'effect' (jedan od: Fade In, Fade Out, Zoom In, Zoom Out, Slide Left, Slide Right, None), 'description' (kratak opis na srpskom šta se dešava u sceni). Napravi između 3 i 6 scena. Vrati SAMO JSON niz, bez ikakvog dodatnog teksta.";

            try
            {
                string result = await CallCloudflareAI(prompt, apiToken);
                LogMessage($"AI odgovor (prvih 300 karaktera): {result?.Substring(0, Math.Min(300, result?.Length ?? 0))}", true);

                if (string.IsNullOrEmpty(result))
                {
                    LogMessage("AI nije vratio odgovor", true);
                    return;
                }

                string cleanedResult = result.Trim();

                if (cleanedResult.StartsWith("```"))
                    cleanedResult = cleanedResult.Substring(cleanedResult.IndexOf('\n') + 1);
                if (cleanedResult.EndsWith("```"))
                    cleanedResult = cleanedResult.Substring(0, cleanedResult.LastIndexOf("```"));

                int jsonStart = cleanedResult.IndexOf('[');
                int jsonEnd = cleanedResult.LastIndexOf(']');

                if (jsonStart < 0 || jsonEnd <= jsonStart)
                {
                    LogMessage("AI nije vratio validan JSON niz", true);
                    return;
                }

                string jsonArray = cleanedResult.Substring(jsonStart, jsonEnd - jsonStart + 1);
                string wrappedJson = $"{{\"scenes\": {jsonArray}}}";

                LogMessage($"JSON za parsiranje: {wrappedJson.Substring(0, Math.Min(200, wrappedJson.Length))}", true);

                var scenes = JsonConvert.DeserializeObject<dynamic>(wrappedJson);

                _animationScenes.Clear();
                if (scenes?.scenes != null)
                {
                    foreach (var scene in scenes.scenes)
                    {
                        var newScene = new AnimationScene
                        {
                            ImageName = scene.imageName.ToString(),
                            Duration = double.Parse(scene.duration.ToString()),
                            Effect = scene.effect.ToString(),
                            Description = scene.description.ToString(),
                            AvailableImages = availableImages,
                            AvailableEffects = new List<string> { "Fade In", "Fade Out", "Zoom In", "Zoom Out", "Slide Left", "Slide Right", "None" }
                        };
                        _animationScenes.Add(newScene);
                    }
                }
                var previewDialog = new AnimationPreviewDialog(_animationScenes, timelineItems.Where(i => i.IsImage).Select(i => i.Name).ToList());
                if (previewDialog.ShowDialog() == true)
                {
                    _animationScenes = previewDialog.GetScenes();
                    LogMessage(string.Format(L("scenario_confirmed"), _animationScenes.Count), true);
                    CreateAnimationFromScenes_Click(sender, e);
                }
                else
                {
                    LogMessage("Generisanje scenarija otkazano", true);
                }
                lstAnimationScenes.ItemsSource = null;
                lstAnimationScenes.ItemsSource = _animationScenes;

                LogMessage($"Scenario generisan: {_animationScenes.Count} scena", true);
                PlayBeep();
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("scenario_error"), ex.Message), true);
            }
        }

        private async void AISuggestLayout_Click(object sender, RoutedEventArgs e)
        {
            LogMessage("AISuggestLayout_Click pozvan", true);

            var animacije = timelineItems.Where(i => i.Type == "Animation" || i.Name.Contains("animacija")).ToList();
            if (animacije.Count == 0)
            {
                LogMessage(L("no_animations"), true);
                return;
            }

            var audio = timelineItems.FirstOrDefault(i => i.IsAudio);
            double audioDuration = audio?.Duration ?? GetTotalDuration();

            string apiToken = GetCloudflareApiKey();
            if (string.IsNullOrEmpty(apiToken))
            {
                LogMessage(L("need_cf_key"), true);
                return;
            }

            var animacijeInfo = string.Join(", ", animacije.Select((a, i) => $"animacija{i + 1}: {a.Duration}s"));

            string prompt = $@"Imam audio zapis dužine {audioDuration} sekundi. 
Imam {animacije.Count} animacija: {animacijeInfo}.
Predloži mi optimalan redoslijed i početne pozicije ovih animacija na timeline-u tako da pokriju cijeli audio.
Rezultat vrati isključivo u JSON formatu bez dodatnog teksta.
JSON treba da sadrži listu 'raspored' gdje svaki element ima: 'animacija_index' (broj od 1 do {animacije.Count}), 'pocetak' (broj u sekundama), 'kraj' (broj u sekundama), 'razlog' (kratak opis na srpskom).";

            LogMessage(L("ai_analyzing_audio"), true);

            try
            {
                string result = await CallCloudflareAI(prompt, apiToken);
                var layout = JsonConvert.DeserializeObject<AILayoutResponse>(result);

                if (layout?.raspored == null || layout.raspored.Count == 0)
                {
                    LogMessage("AI nije vratio validan raspored", true);
                    return;
                }

                var message = new StringBuilder();
                message.AppendLine(L("ai_arrangement_result"));
                foreach (var item in layout.raspored)
                {
                    var anim = animacije[item.animacija_index - 1];
                    message.AppendLine($"Animacija {item.animacija_index}: '{anim.Name}' od {FormatTime(item.pocetak)} do {FormatTime(item.kraj)}. Razlog: {item.razlog}");
                }
                message.AppendLine(L("arrangement_accept"));

                var resultDialog = WpfMessageBox.Show(message.ToString(), "AI Predlog rasporeda",
                                                   MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (resultDialog == MessageBoxResult.Yes)
                {
                    SaveState();

                    foreach (var anim in animacije)
                        timelineItems.Remove(anim);

                    foreach (var item in layout.raspored.OrderBy(p => p.pocetak))
                    {
                        var anim = animacije[item.animacija_index - 1];
                        anim.Start = item.pocetak;
                        anim.Duration = item.kraj - item.pocetak;
                        timelineItems.Add(anim);
                    }

                    UpdateTimelineDisplay();
                    LogMessage(L("animations_arranged"), true);
                    PlayBeep();
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("arrangement_error"), ex.Message), true);
            }
        }

        private void AddScene_Click(object sender, RoutedEventArgs e)
        {
            var availableImages = timelineItems.Where(i => i.IsImage).Select(i => i.Name).ToList();
            var newScene = new AnimationScene
            {
                ImageName = availableImages.FirstOrDefault() ?? "",
                Duration = 5,
                Effect = "Fade In",
                Description = "Nova scena",
                AvailableImages = availableImages,
                AvailableEffects = new List<string> { "Fade In", "Fade Out", "Zoom In", "Zoom Out", "Slide Left", "Slide Right", "None" }
            };
            _animationScenes.Add(newScene);
            lstAnimationScenes.ItemsSource = null;
            lstAnimationScenes.ItemsSource = _animationScenes;
            LogMessage("Dodata nova scena", true);
        }

        private void RemoveScene_Click(object sender, RoutedEventArgs e)
        {
            if (lstAnimationScenes.SelectedItem is AnimationScene scene)
            {
                _animationScenes.Remove(scene);
                lstAnimationScenes.ItemsSource = null;
                lstAnimationScenes.ItemsSource = _animationScenes;
                LogMessage("Scena obrisana", true);
                PlayBeep();
            }
            else
            {
                LogMessage("Selektujte scenu za brisanje", true);
            }
        }

        private void MoveSceneUp_Click(object sender, RoutedEventArgs e)
        {
            if (lstAnimationScenes.SelectedItem is AnimationScene scene)
            {
                int index = _animationScenes.IndexOf(scene);
                if (index > 0)
                {
                    _animationScenes.RemoveAt(index);
                    _animationScenes.Insert(index - 1, scene);
                    lstAnimationScenes.ItemsSource = null;
                    lstAnimationScenes.ItemsSource = _animationScenes;
                    lstAnimationScenes.SelectedItem = scene;
                    LogMessage("Scena pomjerena gore", true);
                    PlayBeep();
                }
            }
        }

        private void MoveSceneDown_Click(object sender, RoutedEventArgs e)
        {
            if (lstAnimationScenes.SelectedItem is AnimationScene scene)
            {
                int index = _animationScenes.IndexOf(scene);
                if (index < _animationScenes.Count - 1)
                {
                    _animationScenes.RemoveAt(index);
                    _animationScenes.Insert(index + 1, scene);
                    lstAnimationScenes.ItemsSource = null;
                    lstAnimationScenes.ItemsSource = _animationScenes;
                    lstAnimationScenes.SelectedItem = scene;
                    LogMessage("Scena pomjerena dolje", true);
                    PlayBeep();
                }
            }
        }

        private void SetAnimationPosition_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem animacija)
            {
                if (!double.TryParse(txtAnimationStartTime.Text, out double startTime))
                {
                    LogMessage("Unesite ispravan broj sekundi", true);
                    return;
                }
                SaveState();
                animacija.Start = startTime;
                UpdateTimelineDisplay();
                LogMessage($"Animacija '{animacija.Name}' postavljena na {FormatTime(startTime)}", true);
                PlayBeep();
            }
            else
            {
                LogMessage("Selektujte animaciju na timeline-u prvo", true);
            }
        }

        private async void CreateAnimationFromScenes_Click(object sender, RoutedEventArgs e)
        {
            if (_animationScenes.Count == 0)
            {
                LogMessage("Nema scena za kreiranje animacije", true);
                return;
            }

            LogMessage("Kreiram animaciju iz scena...", true);
            SaveState();

            foreach (var scene in _animationScenes)
            {
                var image = timelineItems.FirstOrDefault(i => i.IsImage && i.Name == scene.ImageName);
                if (image != null)
                {
                    var animacija = DeepCloneTimelineItem(image);
                    animacija.Name = $"Animacija: {scene.Description}";
                    animacija.Type = "Animation";
                    animacija.Duration = scene.Duration;
                    animacija.Start = 0;
                    timelineItems.Add(animacija);
                }
            }
            UpdateTimelineDisplay();
            LogMessage($"Kreirano {_animationScenes.Count} animacija", true);
            PlayBeep();
        }

        private async Task<string> CallCloudflareAI(string prompt, string apiToken)
        {
            LogMessage(L("cf_ai_starting"), true);

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
                client.Timeout = TimeSpan.FromSeconds(60);

                var requestBody = new
                {
                    messages = new[] { new { role = "user", content = prompt } },
                    max_tokens = 2000,
                    temperature = 0.7
                };

                string jsonBody = JsonConvert.SerializeObject(requestBody);
                LogMessage($"CallCloudflareAI: Request body: {jsonBody.Substring(0, Math.Min(200, jsonBody.Length))}", true);

                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                string url = "https://api.cloudflare.com/client/v4/accounts/9b8004123c153014d851b6056d2da4fe/ai/run/@cf/meta/llama-2-7b-chat-int8";
                LogMessage($"CallCloudflareAI: Pozivam URL: {url}", true);

                try
                {
                    var response = await client.PostAsync(url, content);
                    string responseBody = await response.Content.ReadAsStringAsync();

                    LogMessage($"CallCloudflareAI: Status code: {response.StatusCode}", true);
                    LogMessage($"CallCloudflareAI: Response (prvih 500): {responseBody.Substring(0, Math.Min(500, responseBody.Length))}", true);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonConvert.DeserializeObject<dynamic>(responseBody);
                        string aiResponse = result?.result?.response?.ToString();
                        LogMessage($"CallCloudflareAI: AI response: {aiResponse?.Substring(0, Math.Min(300, aiResponse?.Length ?? 0))}", true);
                        return aiResponse ?? "";
                    }
                    else
                    {
                        LogMessage(string.Format(L("cf_ai_api_error"), response.StatusCode, responseBody), true);
                        return "";
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"CallCloudflareAI: Izuzetak: {ex.Message}", true);
                    return "";
                }
            }
        }

        private void SetTransitionDuration_Click(object sender, RoutedEventArgs e)
        {
            if (lstTransitions.SelectedItem is TransitionEffect transition)
            {
                var dialog = new DurationDialog(transition.Duration);
                if (dialog.ShowDialog() == true)
                {
                    transition.Duration = dialog.Duration;
                    LogMessage(string.Format(L("transition_duration"), transition.Name, dialog.Duration), true);
                    PlayBeep();
                }
            }
            else
            {
                LogMessage("Selektuj tranziciju prvo", true);
            }
        }

        private void SetZoomKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                double time = currentPlaybackPosition - item.Start;
                if (time < 0) time = 0;
                if (time > item.Duration) time = item.Duration;

                var existing = item.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.1);
                if (existing != null)
                {
                    existing.Zoom = sldZoom.Value;
                    LogMessage(string.Format(L("keyframe_zoom"), FormatTime(time), sldZoom.Value), true);
                    UpdateKeyframeList(item);
                    PlayBeep();
                }
                else
                {
                    LogMessage($"Nema keyframe-a na {FormatTime(time)}. Prvo dodajte keyframe (Ctrl+K)", true);
                }
            }
            else
            {
                LogMessage("Selektuj klip prvo", true);
            }
        }

        private void SetRotationKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                double time = currentPlaybackPosition - item.Start;
                if (time < 0) time = 0;
                if (time > item.Duration) time = item.Duration;

                var existing = item.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.1);
                if (existing != null)
                {
                    existing.Rotation = sldRotation.Value;
                    LogMessage(string.Format(L("keyframe_rotation"), FormatTime(time), sldRotation.Value), true);
                    UpdateKeyframeList(item);
                    PlayBeep();
                }
                else
                {
                    LogMessage($"Nema keyframe-a na {FormatTime(time)}. Prvo dodajte keyframe (Ctrl+K)", true);
                }
            }
            else
            {
                LogMessage("Selektuj klip prvo", true);
            }
        }

        private void SetPositionKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                double time = currentPlaybackPosition - item.Start;
                if (time < 0) time = 0;
                if (time > item.Duration) time = item.Duration;

                var existing = item.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.1);
                if (existing != null)
                {
                    existing.X = sldX.Value;
                    existing.Y = sldY.Value;
                    LogMessage(string.Format(L("keyframe_position"), FormatTime(time), sldX.Value, sldY.Value), true);
                    UpdateKeyframeList(item);
                    PlayBeep();
                }
                else
                {
                    LogMessage($"Nema keyframe-a na {FormatTime(time)}. Prvo dodajte keyframe (Ctrl+K)", true);
                }
            }
            else
            {
                LogMessage("Selektuj klip prvo", true);
            }
        }

        private async Task<string> GenerateImageDescriptionAsync(string imagePath, string contextPrompt = "")
        {
            string openAiKey = GetOpenAiApiKey();
            if (!string.IsNullOrEmpty(openAiKey))
                return await DescribeWithGPT4Vision(imagePath, contextPrompt, openAiKey);

            string cfKey = GetCloudflareApiKey();
            if (!string.IsNullOrEmpty(cfKey))
                return await DescribeWithCloudflareLlava(imagePath, contextPrompt, cfKey);

            return "";
        }

        private async Task<string> DescribeWithGPT4Vision(string imagePath, string contextPrompt, string apiKey)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(imagePath);
                string b64 = Convert.ToBase64String(bytes);
                string prompt = string.IsNullOrWhiteSpace(contextPrompt)
                    ? "Opisi sliku kratko i jasno na srpskom jeziku, maksimalno dve recenice. Opis je namenjen slepim osobama."
                    : "Opisi sliku kratko na srpskom. Kontekst: " + contextPrompt + ". Maksimalno dve recenice.";

                var body = new
                {
                    model = "gpt-4o",
                    max_tokens = 150,
                    messages = new object[]
                    {
                        new { role = "user", content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + b64 } }
                        }}
                    }
                };

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                var resp = await client.PostAsync(
                    "https://api.openai.com/v1/chat/completions",
                    new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    var text = await resp.Content.ReadAsStringAsync();
                    dynamic obj = JsonConvert.DeserializeObject(text);
                    return obj?.choices?[0]?.message?.content?.ToString()?.Trim() ?? "";
                }
            }
            catch (Exception ex) { LogMessage("GPT-4 Vision greska: " + ex.Message, false); }
            return "";
        }

        private async Task<string> DescribeWithCloudflareLlava(string imagePath, string contextPrompt, string apiKey)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(imagePath);
                string b64 = Convert.ToBase64String(bytes);
                string accountId = "9b8004123c153014d851b6056d2da4fe";
                string url = "https://api.cloudflare.com/client/v4/accounts/" + accountId + "/ai/run/@cf/llava-hf/llava-1.5-7b-hf";
                string prompt = string.IsNullOrWhiteSpace(contextPrompt)
                    ? "Opisi sliku kratko i jasno na srpskom jeziku, maksimalno dve recenice. Opis je namenjen slepim osobama."
                    : "Opisi sliku kratko na srpskom. Kontekst: " + contextPrompt + ". Maksimalno dve recenice.";

                var requestBody = new
                {
                    messages = new object[]
                    {
                        new { role = "user", content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + b64 } }
                        }}
                    }
                };

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                var resp = await client.PostAsync(url,
                    new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    var text = await resp.Content.ReadAsStringAsync();
                    var obj = JsonConvert.DeserializeObject<LlavaResponse>(text);
                    return obj?.result?.response?.Trim() ?? "";
                }
            }
            catch (Exception ex) { LogMessage("Cloudflare llava greska: " + ex.Message, false); }
            return "";
        }
        private void SetImagePosition_Click(object sender, RoutedEventArgs e)
        {
            if (!(nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item) || (!item.IsImage && !item.IsVideo))
            {
                LogMessage("Selektuj sliku ili video klip na timeline-u.", true);
                return;
            }

            var audioItem = timelineItems.FirstOrDefault(i => i.IsAudio);
            double audioDur = audioItem != null ? audioItem.Duration : GetTotalDuration();

            var dlg = new PositionDialog(item, timelineItems, audioDur);
            if (dlg.ShowDialog() == true)
            {
                SaveState();

                if (dlg.AutoPlaceAll)
                {
                    // Automatski rasporedi sve slike
                    var images = timelineItems.Where(i => i.IsImage).ToList();
                    if (images.Count > 0 && audioItem != null)
                    {
                        double timePerImage = audioItem.Duration / images.Count;
                        for (int i = 0; i < images.Count; i++)
                        {
                            images[i].FixedPosition = i * timePerImage;
                            images[i].Duration = timePerImage;
                            images[i].UseFixedPosition = true;
                        }
                    }
                    LogMessage("Ravnomerni raspored primenjen na sve slike.", true);
                }
                else if (dlg.SelectedPosition >= 0)
                {
                    LogMessage($"DEBUG: Postavljam '{item.Name}' na poziciju {dlg.SelectedPosition:F2}s, trajanje {dlg.SelectedDuration:F2}s", true);

                    item.FixedPosition = dlg.SelectedPosition;
                    item.Duration = dlg.SelectedDuration;
                    item.UseFixedPosition = true;
                }

                // Ponovo rasporedi sve klipove
                ReorderAllClips();

                LogMessage($"Slika '{item.Name}' postavljena na {FormatTime(dlg.SelectedPosition)}", true);
                UpdateTimelineDisplay();
                PlayBeep();
            }
        }
        private void ReorderAllClips()
        {
            var allItems = timelineItems.ToList();
            var videoItems = allItems.Where(i => i.IsImage || i.IsVideo).ToList();
            var audioItems = allItems.Where(i => i.IsAudio).ToList();

            // Podijeli na fiksirane i nefiksirane
            var fixedVideos = videoItems.Where(i => i.UseFixedPosition && i.FixedPosition > 0).OrderBy(i => i.FixedPosition).ToList();
            var nonFixedVideos = videoItems.Where(i => !i.UseFixedPosition || i.FixedPosition <= 0).ToList();

            // Napravi novu listu
            var newTimeline = new List<TimelineItem>();

            // Dodaj fiksirane video klipove na njihove tačne pozicije
            foreach (var video in fixedVideos)
            {
                video.Start = video.FixedPosition;
                video.End = video.FixedPosition + video.Duration;
                newTimeline.Add(video);
            }

            // Dodaj nefiksirane video klipove – pozicioniraj ih iza najvećeg fiksiranog ili na početak
            double lastEnd = fixedVideos.Any() ? fixedVideos.Max(i => i.End) : 0;

            // Sortiraj nefiksirane po trenutnom Start (da zadrže redoslijed)
            nonFixedVideos = nonFixedVideos.OrderBy(i => i.Start).ToList();

            foreach (var video in nonFixedVideos)
            {
                // Pronađi prvu slobodnu poziciju koja nije zauzeta fiksiranim klipovima
                while (newTimeline.Any(i => i.Start < lastEnd + video.Duration && i.End > lastEnd))
                {
                    lastEnd = newTimeline.Where(i => i.Start < lastEnd + video.Duration && i.End > lastEnd)
                                         .Max(i => i.End);
                }

                video.Start = lastEnd;
                video.End = lastEnd + video.Duration;
                lastEnd = video.End;
                newTimeline.Add(video);
            }

            // Dodaj audio klipove (njihove pozicije ostaju iste)
            foreach (var audio in audioItems)
            {
                newTimeline.Add(audio);
            }

            // Sortiraj sve po početku
            timelineItems = newTimeline.OrderBy(i => i.Start).ToList();

            // Log za debug
            LogMessage("=== REORDER FINAL ===", true);
            foreach (var i in timelineItems.Where(i => i.IsImage || i.IsVideo))
            {
                LogMessage($"{i.Name}: Start={i.Start:F2}, End={i.End:F2}, Fixed={i.FixedPosition:F2}, UseFixed={i.UseFixedPosition}", true);
            }

            UpdateTimelineDisplay();
        }
        private void ApplyFixedPositions()
        {
            var videoItems = timelineItems.Where(i => i.IsImage || i.IsVideo).ToList();
            var audioItems = timelineItems.Where(i => i.IsAudio).ToList();

            // Samo one sa UseFixedPosition = true zadržavaju fiksnu poziciju
            var fixedItems = videoItems.Where(i => i.UseFixedPosition && i.FixedPosition > 0).ToList();
            var nonFixedItems = videoItems.Where(i => !i.UseFixedPosition || i.FixedPosition <= 0).ToList();

            // Prvo rasporedi fiksirane stavke
            foreach (var it in fixedItems)
            {
                it.Start = it.FixedPosition;
                it.End = it.FixedPosition + it.Duration;
            }

            // Zatim rasporedi nefiksirane stavke iza fiksiranih
            double cursor = 0;
            if (fixedItems.Any())
                cursor = fixedItems.Max(i => i.End);

            foreach (var it in nonFixedItems)
            {
                it.Start = cursor;
                it.End = cursor + it.Duration;
                cursor = it.End;
            }

            // Kombinuj sve
            timelineItems.Clear();
            timelineItems.AddRange(fixedItems);
            timelineItems.AddRange(nonFixedItems);
            timelineItems.AddRange(audioItems);

            // Sortiraj po početku
            timelineItems = timelineItems.OrderBy(i => i.Start).ToList();

            UpdateTimelineDisplay();
        }
        private void SetupTransitionContextMenus()
        {
            if (lstTransitions == null) return;

            var ctxTrans = new ContextMenu();
            var miApply = new MenuItem { Header = L("ctx_trans_apply_selected") };
            miApply.Click += (s, ev) => ApplyTransitionToSelectedClip();
            var miApplyAll = new MenuItem { Header = L("ctx_trans_apply_all") };
            miApplyAll.Click += (s, ev) => ApplyTransitionToAll();
            var miDesc = new MenuItem { Header = L("ctx_trans_read_desc") };
            miDesc.Click += (s, ev) => DescribeSelectedTransition();
            ctxTrans.Items.Add(miApply);
            ctxTrans.Items.Add(miApplyAll);
            ctxTrans.Items.Add(new Separator());
            ctxTrans.Items.Add(miDesc);
            lstTransitions.ContextMenu = ctxTrans;

            lstTransitions.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Return || ev.Key == Key.Space) { ApplyTransitionToSelectedClip(); ev.Handled = true; }
                else if (ev.Key == Key.F1) { DescribeSelectedTransition(); ev.Handled = true; }
            };
        }

        private void ApplyTransitionToSelectedClip()
        {
            if (!(lstTransitions.SelectedItem is TransitionEffect tpl))
            { LogMessage("Selektuj tranziciju u listi tranzicija.", true); return; }
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            { LogMessage("Selektuj klip koji nije prvi — tranzicija ide izmedju prethodnog i selektovanog klipa.", true); return; }
            int idx = timelineItems.IndexOf(item);
            if (idx <= 0) { LogMessage("Selektuj klip koji nije prvi", true); return; }
            var t = new TransitionEffect
            {
                Name = tpl.Name,
                Type = tpl.Type,
                Duration = transitionDuration,
                ClipIndex1 = idx - 1,
                ClipIndex2 = idx
            };
            transitions.RemoveAll(x => x.ClipIndex1 == idx - 1 || x.ClipIndex2 == idx);
            transitions.Add(t);
            SaveState();
            string c1 = idx - 1 < timelineItems.Count ? timelineItems[idx - 1].Name : "?";
            string c2 = idx < timelineItems.Count ? timelineItems[idx].Name : "?";
            LogMessage("Tranzicija '" + t.Name + "' (" + transitionDuration.ToString("F1") + "s) izmedju '" + c1 + "' i '" + c2 + "'.", true);
            PlayBeep();
            UpdateTransitionIndicators();
        }

        private void ApplyTransitionToAll()
        {
            if (!(lstTransitions.SelectedItem is TransitionEffect tpl))
            { LogMessage("Selektuj tranziciju u listi.", true); return; }
            var vids = timelineItems.Where(i => i.IsVideo || i.IsImage).ToList();
            if (vids.Count < 2) { LogMessage("Potrebna su najmanje 2 video/slika klipa.", true); return; }
            SaveState();
            transitions.Clear();
            for (int i = 1; i < vids.Count; i++)
            {
                int g1 = timelineItems.IndexOf(vids[i - 1]);
                int g2 = timelineItems.IndexOf(vids[i]);
                transitions.Add(new TransitionEffect { Name = tpl.Name, Type = tpl.Type, Duration = transitionDuration, ClipIndex1 = g1, ClipIndex2 = g2 });
            }
            LogMessage("Tranzicija '" + tpl.Name + "' primenjena na " + transitions.Count + " prelaza.", true);
            PlayBeep();
            UpdateTransitionIndicators();
        }

        private void DescribeSelectedTransition()
        {
            if (!(lstTransitions.SelectedItem is TransitionEffect t))
            { LogMessage("Nije selektovana tranzicija.", true); return; }
            string desc;
            if (t.Type == TransitionType.Fade) desc = "Fade: slika postepeno nestaje u crno, pa se pojavljuje sledeca.";
            else if (t.Type == TransitionType.Crossfade) desc = "Crossfade: dva klipa se postepeno mesaju jedno u drugo.";
            else if (t.Type == TransitionType.SlideLeft) desc = "Slide Left: novi klip ulazi s desne strane i gura stari ulevo.";
            else if (t.Type == TransitionType.SlideRight) desc = "Slide Right: novi klip ulazi s leve strane i gura stari udesno.";
            else if (t.Type == TransitionType.SlideUp) desc = "Slide Up: novi klip ulazi odozdo i gura stari nagore.";
            else if (t.Type == TransitionType.SlideDown) desc = "Slide Down: novi klip ulazi odozgo i gura stari nadole.";
            else if (t.Type == TransitionType.WipeLeft) desc = "Wipe Left: novi klip se otkriva poput zavese s desna na levo.";
            else if (t.Type == TransitionType.WipeRight) desc = "Wipe Right: novi klip se otkriva poput zavese s leva na desno.";
            else if (t.Type == TransitionType.ZoomIn) desc = "Zoom In: sledeci klip se uvecava iz centra i prekriva prethodni.";
            else if (t.Type == TransitionType.ZoomOut) desc = "Zoom Out: prethodni klip se suzava i otkriva sledeci.";
            else desc = t.Name;
            LogMessage("Efekat: " + t.Name + ". " + desc, true);
        }

        private void ShowTransitionOnSelectedClip()
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            { LogMessage("Nije selektovan klip.", true); return; }
            int idx = timelineItems.IndexOf(item);
            var before = transitions.FirstOrDefault(t => t.ClipIndex2 == idx);
            var after = transitions.FirstOrDefault(t => t.ClipIndex1 == idx);
            string msg = "";
            if (before != null) msg += "Ispred klipa: '" + before.Name + "' (" + before.Duration.ToString("F1") + "s). ";
            if (after != null) msg += "Iza klipa: '" + after.Name + "' (" + after.Duration.ToString("F1") + "s). ";
            if (msg == "") msg = "Nema tranzicija na ovom klipu.";
            LogMessage(msg, true);
        }

        private void RemoveTransitionFromSelectedClip()
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            { LogMessage("Nije selektovan klip.", true); return; }
            int idx = timelineItems.IndexOf(item);
            int removed = transitions.RemoveAll(t => t.ClipIndex1 == idx || t.ClipIndex2 == idx);
            if (removed > 0) { SaveState(); LogMessage("Uklonjeno " + removed + " tranzicija sa klipa " + (idx + 1) + ".", true); PlayBeep(); UpdateTransitionIndicators(); }
            else LogMessage("Nema tranzicija na ovom klipu.", true);
        }

        private void UpdateTransitionIndicators()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Opcionalno
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void InitNativeListView()
        {
            try
            {
                if (wfhTimeline?.Child is WinForms.ListView listView)
                {
                    nativeListView = listView;
                    nativeListView.View = WinForms.View.Details;
                    nativeListView.FullRowSelect = true;
                    nativeListView.GridLines = false;
                    nativeListView.MultiSelect = true;
                    nativeListView.HideSelection = false;
                    nativeListView.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
                    nativeListView.ForeColor = System.Drawing.Color.White;
                    nativeListView.Font = new System.Drawing.Font("Segoe UI", 10);

                    nativeListView.Columns.Clear();
                    nativeListView.Columns.Add(L("listview_col_num"), 50, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_name"), 350, WinForms.HorizontalAlignment.Left);
                    nativeListView.Columns.Add(L("listview_col_type"), 60, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_duration"), 80, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_start"), 80, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_end"), 80, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_audio_desc"), 300, WinForms.HorizontalAlignment.Left);

                    nativeListView.SelectedIndexChanged += NativeListView_SelectedIndexChanged;
                    nativeListView.KeyDown += NativeListView_KeyDown;
                    nativeListView.MouseDoubleClick += (s, e) =>
                    {
                        if (nativeListView.SelectedItems.Count > 0)
                            TogglePlay_Click(null, null);
                    };

                    var contextMenu = new WinForms.ContextMenuStrip();
                    contextMenu.Items.Add("✂ " + L("cut"), null, (s, e) => CutClip_Click(null, null));
                    contextMenu.Items.Add("📋 " + L("copy"), null, (s, e) => CopyClip_Click(null, null));
                    contextMenu.Items.Add(L("ctx_paste_clip"), null, (s, e) => PasteClip_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add(L("ctx_delete_clip"), null, (s, e) => RemoveFromTimeline_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("⬅ " + L("move_left"), null, (s, e) => MoveLeft_Click(null, null));
                    contextMenu.Items.Add(L("ctx_move_right"), null, (s, e) => MoveRight_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("⬅ " + L("ctx_move_first"), null, (s, e) => MoveToFirst_Click(null, null));
                    contextMenu.Items.Add(L("ctx_move_last"), null, (s, e) => MoveToLast_Click(null, null));
                    contextMenu.Items.Add("🔢 " + L("ctx_set_position"), null, (s, e) => MoveToPosition_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("⏱ " + L("mnu_set_duration"), null, (s, e) => SetClipDuration_Click(null, null));
                    contextMenu.Items.Add(L("ctx_set_volume"), null, (s, e) => SetClipVolume_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("🎤 " + L("ctx_add_audio_desc"), null, (s, e) => AddAudioDescription_Click(null, null));
                    contextMenu.Items.Add(L("ctx_read_audio"), null, (s, e) => ReadAudioDescription_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add(L("ctx_set_position"), null, (s, e) => SetImagePosition_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("📝 " + L("ctx_add_intro_text"), null, (s, e) => AddIntroText_Click(null, null));
                    contextMenu.Items.Add("📝 " + L("ctx_add_outro_text"), null, (s, e) => AddOutroText_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("🎨 " + L("ctx_add_text_to_image"), null, (s, e) => AddTextToImage_Click(null, null));
                    nativeListView.ContextMenuStrip = contextMenu;
                    nativeListView.AccessibleName = "Lista klipova na vremenskoj traci. Koristite strelice za navigaciju. Ctrl+Space za play/pause. Page Up/Down za brzo listanje.";

                    LogMessage("Win32 ListView inicijalizovan - kolone postavljene", true);
                    UpdateTimelineDisplay();
                }
                else
                {
                    LogMessage(L("listview_error"), true);
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("listview_init_error"), ex.Message), true);
            }
        }

        private void NativeListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_selectionProcessing) return;

            _selectionProcessing = true;
            _selectionDebounceTimer.Stop();
            _selectionDebounceTimer.Start();

            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                _pendingPlaybackItem = item;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_selectionProcessing)
                {
                    ProcessSelectedItem();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ProcessSelectedItem()
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                // ── JAWS/NVDA ispravan redosljed čitanja ──────────────────────────────
                // UVIJEK: "N od UKUPNO, Tip: Ime, trajanje X, početak Y, kraj Z"
                // NIKAD:  statusna poruka ispred naziva fajla
                //
                // Primjer: "1 od 21, Video: Tokom proljeća i jeseni, trajanje 00:09, početak 00:04, kraj 00:13"
                //
                string typeLabel = item.Type switch
                {
                    "Video" => "Video",
                    "Audio" => "Audio",
                    "Image" => "Slika",
                    _ => item.Type ?? "Nepoznato"
                };
                string duration = TimeSpan.FromSeconds(item.Duration).ToString(@"mm\:ss");
                string start = TimeSpan.FromSeconds(item.Start).ToString(@"mm\:ss");
                string end = TimeSpan.FromSeconds(item.End).ToString(@"mm\:ss");
                string total = nativeListView.Items.Count.ToString();

                // Ime klipa: koristi AudioDescription ako postoji (bolji kontekst),
                // inače samo naziv fajla bez putanje i bez ekstenzije
                string clipName = !string.IsNullOrWhiteSpace(item.AudioDescription)
                    ? item.AudioDescription
                    : Path.GetFileNameWithoutExtension(item.Name ?? string.Empty);

                // Format koji JAWS čita prirodno i u pravom redosljedu:
                // "1 od 21, Video: Naziv klipa, trajanje 00:09, početak 00:04, kraj 00:13"
                string msg = string.Format(L("timeline_item_label"), item.Index, total, typeLabel, clipName, duration, start, end);

                LogMessage(msg, true);

                // Notifikacija UIA provajdera — osvježi accessibility tree za JAWS/NVDA
                nativeListView.Invoke((System.Windows.Forms.MethodInvoker)(() =>
                {
                    nativeListView.Refresh();
                }));
            }
        }

        private void NativeListView_KeyDown(object sender, WinFormsKeyEventArgs e)
        {
            if (e.Control && e.KeyCode == WinForms.Keys.C)
            {
                CopyClip_Click(null, null);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == WinForms.Keys.V)
            {
                PasteClip_Click(null, null);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == WinForms.Keys.X)
            {
                CutClip_Click(null, null);
                e.Handled = true;
            }
            else if (e.KeyCode == WinForms.Keys.Delete)
            {
                RemoveFromTimeline_Click(null, null);
                e.Handled = true;
            }
            else if (e.KeyCode == WinForms.Keys.F5)
            {
                FinalRender_Click(null, null);
                e.Handled = true;
            }
            else if (e.KeyCode == WinForms.Keys.Space || e.KeyCode == WinForms.Keys.Enter)
            {
                // I Space i Enter pokreću play — čitači ekrana često koriste Enter
                TogglePlay_Click(null, null);
                e.Handled = true;
            }
            else if (e.KeyCode == WinForms.Keys.F6)
            {
                // F6 čita cijeli opis selektovanog klipa (za JAWS/NVDA)
                ProcessSelectedItem();
                e.Handled = true;
            }
        }

        private void UpdateNativeListView()
        {
            if (nativeListView == null)
            {
                LogMessage("UpdateNativeListView: nativeListView je null", true);
                return;
            }

            try
            {
                // Zapamti indeks selektovanog elementa
                int selectedIndex = nativeListView.SelectedItems.Count > 0
                    ? nativeListView.SelectedItems[0].Index
                    : -1;

                nativeListView.BeginUpdate();
                nativeListView.Items.Clear();

                var filteredItems = currentTrackFilter == -1
                    ? timelineItems
                    : timelineItems.Where(x => x.TrackIndex == currentTrackFilter).ToList();

                double currentTime = 0;

                for (int i = 0; i < filteredItems.Count; i++)
                {
                    var item = filteredItems[i];
                    item.Start = currentTime;
                    item.End = currentTime + item.Duration;
                    item.Index = i + 1;

                    var listItem = new WinForms.ListViewItem(item.Index.ToString());
                    listItem.SubItems.Add(item.Name);
                    listItem.SubItems.Add(item.TypeIcon);
                    listItem.SubItems.Add(TimeSpan.FromSeconds(item.Duration).ToString(@"mm\:ss"));
                    listItem.SubItems.Add(TimeSpan.FromSeconds(item.Start).ToString(@"mm\:ss"));
                    listItem.SubItems.Add(TimeSpan.FromSeconds(item.End).ToString(@"mm\:ss"));
                    listItem.SubItems.Add(item.AudioDescription ?? "");
                    listItem.Tag = item;

                    nativeListView.Items.Add(listItem);
                    currentTime += item.Duration;
                }

                // Vrati selekciju na isti indeks
                if (selectedIndex >= 0 && selectedIndex < nativeListView.Items.Count)
                {
                    nativeListView.Items[selectedIndex].Selected = true;
                    nativeListView.Items[selectedIndex].EnsureVisible();
                }
                else if (nativeListView.Items.Count > 0 && selectedIndex >= nativeListView.Items.Count)
                {
                    // Ako je obrisan zadnji, selektuj novi zadnji
                    nativeListView.Items[nativeListView.Items.Count - 1].Selected = true;
                }

                nativeListView.EndUpdate();

                if (txtTimelineInfo != null)
                {
                    string infoText = filteredItems.Count > 0
                        ? $"Ukupno klipova: {filteredItems.Count} | Ukupno trajanje: {FormatTime(GetTotalDuration())} | Zoom: {zoomLevel:F1}x"
                        : "Nema klipova na timeline-u.";
                    txtTimelineInfo.Text = infoText;
                    // Pomozi screen readerima da pročitaju ažurirani status
                    AutomationProperties.SetName(txtTimelineInfo, infoText);
                }

                LogMessage(string.Format(L("listview_refreshed"), filteredItems.Count), false);
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("listview_refresh_error"), ex.Message), true);
            }
        }

        private void PlaySelectedItem(TimelineItem item)
        {
            if (!File.Exists(item.Path)) return;

            try
            {
                if (_mediaPlayer.IsPlaying) _mediaPlayer.Stop();
                _currentMedia = new Media(_libVLC, item.Path);
                _mediaPlayer.Media = _currentMedia;
                _mediaPlayer.Volume = (int)item.Volume;
                _mediaPlayer.Play();
                isPlaying = true;
                if (btnPlay != null) btnPlay.Content = "⏸ PAUSE";
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("playback_error"), ex.Message), true);
            }
        }


        // ══════════════════════════════════════════════════════════════
        // POLLINATIONS.AI — besplatno generisanje slika, bez API kljuca
        // ══════════════════════════════════════════════════════════════

        private async void GenerateWithPollinations_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentProjectFolder))
            {
                System.Windows.MessageBox.Show("Prvo sacuvaj projekat!");
                return;
            }
            if (string.IsNullOrWhiteSpace(txtAIPrompt?.Text))
            {
                LogMessage("Unesi opis slike u polje za prompt.", true);
                return;
            }

            if (btnGenerate != null) btnGenerate.IsEnabled = false;
            if (prgAI != null) prgAI.Visibility = System.Windows.Visibility.Visible;

            string[] prompts = txtAIPrompt.Text.Split(',');
            int generated = 0;

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(90);
                for (int i = 0; i < prompts.Length; i++)
                {
                    string cleanP = prompts[i].Trim();
                    if (string.IsNullOrEmpty(cleanP)) continue;
                    LogMessage("Pollinations.ai generise: " + cleanP + "...", true);
                    try
                    {
                        // Pollinations.ai — samo URL sa promptom, nula konfiguracije
                        string url = "https://image.pollinations.ai/prompt/" +
                                     Uri.EscapeDataString(cleanP) +
                                     "?width=1024&height=576&nologo=true&seed=" + i;
                        byte[] bytes = await client.GetByteArrayAsync(url);
                        string imgPath = System.IO.Path.Combine(currentProjectFolder,
                            "Pol_" + Guid.NewGuid().ToString().Substring(0, 8) + ".jpg");
                        await File.WriteAllBytesAsync(imgPath, bytes);

                        string desc = await GenerateImageDescriptionAsync(imgPath, cleanP);
                        if (string.IsNullOrEmpty(desc)) desc = "Pollinations slika: " + cleanP;

                        SaveState();
                        timelineItems.Add(new TimelineItem
                        {
                            Path = imgPath,
                            Duration = 5.0,
                            Name = "Pol: " + cleanP,
                            AudioDescription = desc,
                            Type = "Image",
                            Volume = 100,
                            TrackIndex = 0,
                            VideoEffect = new VideoEffectData()
                        });
                        generated++;
                        LogMessage("Slika " + (i + 1) + " gotova. Opis: " + desc, true);
                        if (prgAI != null)
                            Dispatcher.Invoke(() => prgAI.Value = (i + 1) * 100 / prompts.Length);
                    }
                    catch (Exception ex) { LogMessage("Pollinations greska: " + ex.Message, true); }
                    await Task.Delay(300);
                }
            }

            UpdateTimelineDisplay();
            if (btnGenerate != null) btnGenerate.IsEnabled = true;
            if (prgAI != null) prgAI.Visibility = System.Windows.Visibility.Collapsed;
            LogMessage("Pollinations.ai: generisano " + generated + "/" + prompts.Length + " slika.", true);
            PlayBeep();
        }

        // ══════════════════════════════════════════════════════════════
        // SKIA ANIMACIJE — animirani tekst, 7 stilova, bez eksternih alata
        // ══════════════════════════════════════════════════════════════

        private async void GenerateSkiaAnimation_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentProjectFolder))
            {
                System.Windows.MessageBox.Show("Prvo sacuvaj projekat!");
                return;
            }
            var dlg = new SkiaAnimationDialog();
            if (dlg.ShowDialog() != true) return;

            LogMessage("Generisem SkiaSharp animaciju: " + dlg.AnimationText + "...", true);
            try
            {
                string outPath = System.IO.Path.Combine(currentProjectFolder,
                    "Skia_" + Guid.NewGuid().ToString().Substring(0, 8) + ".mp4");

                await Task.Run(() =>
                    SkiaAnimationEngine.RenderToVideo(
                        dlg.AnimationText,
                        dlg.AnimationStyle,
                        dlg.TextColor,
                        dlg.BgColor,
                        dlg.DurationSeconds,
                        outPath));

                string desc = "Animacija teksta: " + dlg.AnimationText + ", stil: " + dlg.AnimationStyle;
                SaveState();
                timelineItems.Add(new TimelineItem
                {
                    Path = outPath,
                    Duration = dlg.DurationSeconds,
                    Name = "Anim: " + dlg.AnimationText,
                    AudioDescription = desc,
                    Type = "Video",
                    Volume = 100,
                    TrackIndex = 0,
                    VideoEffect = new VideoEffectData()
                });
                UpdateTimelineDisplay();
                LogMessage("Skia animacija kreirana: " + System.IO.Path.GetFileName(outPath), true);
                PlayBeep();
            }
            catch (Exception ex)
            {
                LogMessage("Greska pri generisanju animacije: " + ex.Message, true);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // CTRL+V FIX ZA AI PROMPT POLJE
        // ══════════════════════════════════════════════════════════════

        private void SetupAIPromptPaste()
        {
            if (txtAIPrompt == null) return;
            // PreviewKeyDown se okida PRE nego sto TextBox konzumira Ctrl+V
            txtAIPrompt.PreviewKeyDown += (s, ev) =>
            {
                if (ev.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    PastePrompt_Click(null, null);
                    ev.Handled = true;
                }
            };
        }
        private async void btnAutoArrange_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Pronađi audio fajl
                var audioItem = timelineItems.FirstOrDefault(i => i.IsAudio);
                if (audioItem == null)
                {
                    LogMessage("Nema audio fajla na timeline-u. Prvo dodajte pjesmu.", true);
                    return;
                }

                // Pronađi sve slike
                var images = timelineItems.Where(i => i.IsImage &&
                    !i.Name.Contains("Najavni") &&
                    !i.Name.Contains("Odjavni")).ToList();
                if (images.Count == 0)
                {
                    LogMessage("Nema slika na timeline-u. Prvo dodajte slike.", true);
                    return;
                }

                // Otvori dijalog
                var dialog = new AutoArrangeDialog(audioItem.Duration, images.Count);
                if (dialog.ShowDialog() != true) return;

                // Sačuvaj trenutno stanje za Undo
                SaveState();

                // Ukloni postojeće tekstualne slojeve
                var existingTextItems = timelineItems.Where(i => i.Name.Contains("Najavni") || i.Name.Contains("Odjavni")).ToList();
                foreach (var textItem in existingTextItems)
                {
                    timelineItems.Remove(textItem);
                }

                double totalTime = audioItem.Duration;
                double availableTime = totalTime - dialog.IntroDuration - dialog.OutroDuration;
                double timePerImage = availableTime / images.Count;

                if (timePerImage <= 0)
                {
                    LogMessage("Nema dovoljno vremena za slike. Smanjite trajanje teksta.", true);
                    return;
                }

                LogMessage($"Auto-raspored: {images.Count} slika, svaka po {timePerImage:F2} sekundi", true);

                // Inicijalizuj lokalnu zvučnu biblioteku ako su zvukovi uključeni
                bool soundsEnabled = dialog.EnableTransitionSounds || dialog.EnableAmbientSounds;
                if (soundsEnabled)
                {
                    LogMessage("🔊 Lokalna zvučna biblioteka aktivna za slideshow", true);
                }
                // Lista za tranzicione zvukove
                var transitionSounds = new List<TimelineItem>();

                // Rasporedi slike
                double currentTime = dialog.IntroDuration;
                for (int i = 0; i < images.Count; i++)
                {
                    var img = images[i];

                    // Postavi poziciju
                    img.FixedPosition = currentTime;
                    img.Duration = timePerImage;
                    img.UseFixedPosition = true;
                    img.Start = currentTime;
                    img.End = currentTime + timePerImage;

                    // Ken Burns efekat (ako je uključen)
                    if (dialog.EnableKenBurns)
                    {
                        AddKenBurnsKeyframes(img, timePerImage);
                    }
                    else
                    {
                        // Inače koristi odabrani efekat
                        string effectName = GetEffectName(dialog.EffectMode, dialog.EffectSequence, i);
                        AddKeyframesForEffect(img, effectName, timePerImage);
                    }

                    // Dodaj tekst na sliku ako je uključeno
                    if (dialog.TextOnImageEnabled)
                    {
                        string overlayText = dialog.OverlayText;
                        if (dialog.UseCounter)
                        {
                            overlayText = $"{overlayText} {i + 1}";
                        }

                        img.TextOverlay = new TextOverlayData
                        {
                            Text = overlayText,
                            Font = dialog.OverlayFont,
                            SelectedFontSize = 48,
                            Color = dialog.OverlayColor,
                            Position = "Centar",
                            Enabled = true
                        };
                    }

                    // Dodaj tranzicioni zvuk (pop/whoosh) između scena (osim poslije zadnje)
                    if (dialog.EnableTransitionSounds && i < images.Count - 1)
                    {
                        string transitionType = i % 2 == 0 ? "whoosh" : "pop";
                        string soundPath = LocalSoundLibrary.GetTransitionSound(transitionType);
                        if (!string.IsNullOrEmpty(soundPath))
                        {
                            var soundItem = new TimelineItem
                            {
                                Path = soundPath,
                                Duration = 0.5,
                                Start = currentTime + timePerImage - 0.2,
                                End = currentTime + timePerImage + 0.3,
                                Name = $"🔊 Tranzicija: {transitionType}",
                                Type = "Audio",
                                Volume = 30,
                                TrackIndex = 2,
                                VideoEffect = new VideoEffectData()
                            };
                            transitionSounds.Add(soundItem);
                        }
                    }

                    LogMessage($"Slika {i + 1}: '{img.Name}' na {FormatTime(img.Start)} - {(dialog.EnableKenBurns ? "Ken Burns zoom" : GetEffectName(dialog.EffectMode, dialog.EffectSequence, i))}", false);

                    currentTime += timePerImage;
                }

                // Dodaj tranzicione zvukove u timeline
                foreach (var sound in transitionSounds)
                {
                    timelineItems.Add(sound);
                }
                if (transitionSounds.Count > 0)
                {
                    LogMessage($"Dodato {transitionSounds.Count} tranzicionih zvukova", true);
                }

                // Ambijentalni zvukovi — lokalna biblioteka
                if (dialog.EnableAmbientSounds)
                {
                    LogMessage("Dodajem ambijentalne zvukove iz lokalne biblioteke...", true);
                    var ambientSounds = new List<TimelineItem>();

                    string[] ambientTypes = { "birds", "children laughing", "wind" };
                    double ambientStart = dialog.IntroDuration;
                    double ambientEnd = totalTime - dialog.OutroDuration;

                    if (ambientEnd > ambientStart)
                    {
                        foreach (var ambientType in ambientTypes)
                        {
                            string soundPath = LocalSoundLibrary.GetAmbientSound(ambientType);
                            if (!string.IsNullOrEmpty(soundPath))
                            {
                                var ambientItem = new TimelineItem
                                {
                                    Path = soundPath,
                                    Duration = ambientEnd - ambientStart,
                                    Start = ambientStart,
                                    End = ambientEnd,
                                    Name = $"🌳 Ambijentalni zvuk: {ambientType}",
                                    Type = "Audio",
                                    Volume = 15,
                                    TrackIndex = 2,
                                    VideoEffect = new VideoEffectData()
                                };
                                ambientSounds.Add(ambientItem);
                            }
                        }
                    }

                    foreach (var ambient in ambientSounds)
                    {
                        timelineItems.Add(ambient);
                    }
                    if (ambientSounds.Count > 0)
                    {
                        LogMessage($"Dodato {ambientSounds.Count} ambijentalnih zvukova", true);
                    }
                }
                // DODAJ NAJAVNI TEKST (na početku)
                if (!string.IsNullOrEmpty(dialog.IntroText))
                {
                    string introImagePath = await CreateTextImage(dialog.IntroText, dialog.IntroDuration, true);
                    var introTextItem = new TimelineItem
                    {
                        Path = introImagePath,
                        Duration = dialog.IntroDuration,
                        FixedPosition = 0,
                        UseFixedPosition = true,
                        Start = 0,
                        End = dialog.IntroDuration,
                        Name = $"Najavni tekst: {dialog.IntroText}",
                        Type = "Image",
                        Volume = 100,
                        TrackIndex = 0,
                        VideoEffect = new VideoEffectData()
                    };
                    timelineItems.Add(introTextItem);
                    LogMessage($"Dodat najavni tekst: {dialog.IntroText}", true);
                }

                // DODAJ ODJAVNI TEKST (na kraju)
                if (!string.IsNullOrEmpty(dialog.OutroText))
                {
                    double outroStart = totalTime - dialog.OutroDuration;
                    if (outroStart < 0) outroStart = 0;

                    string outroImagePath = await CreateTextImage(dialog.OutroText, dialog.OutroDuration, false);
                    var outroTextItem = new TimelineItem
                    {
                        Path = outroImagePath,
                        Duration = dialog.OutroDuration,
                        FixedPosition = outroStart,
                        UseFixedPosition = true,
                        Start = outroStart,
                        End = outroStart + dialog.OutroDuration,
                        Name = $"Odjavni tekst: {dialog.OutroText}",
                        Type = "Image",
                        Volume = 100,
                        TrackIndex = 0,
                        VideoEffect = new VideoEffectData()
                    };
                    timelineItems.Add(outroTextItem);
                    LogMessage($"Dodat odjavni tekst: {dialog.OutroText}", true);
                }

                // Logo
                if (dialog.ShowLogo && !string.IsNullOrEmpty(dialog.LogoPath) && File.Exists(dialog.LogoPath))
                {
                    var logoItem = new TimelineItem
                    {
                        Path = dialog.LogoPath,
                        Duration = dialog.LogoDuration,
                        FixedPosition = 0,
                        UseFixedPosition = true,
                        Start = 0,
                        End = dialog.LogoDuration,
                        Name = "Logo kanala",
                        Type = "Image",
                        Volume = 100,
                        TrackIndex = 0,
                        VideoEffect = new VideoEffectData()
                    };
                    timelineItems.Add(logoItem);
                    LogMessage($"Dodat logo: {Path.GetFileName(dialog.LogoPath)}", true);
                }

                // Crossfade između slika (ako je uključen)
                if (dialog.EnableCrossfade)
                {
                    AddCrossfadeBetweenImages(images, timePerImage, dialog.IntroDuration);
                    LogMessage(L("crossfade_applied"), true);
                }

                ReorderAllClips();
                LogMessage(string.Format(L("auto_arrange_done"), images.Count), true);
                PlayBeep();
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("auto_arrange_error"), ex.Message), true);
            }
        }
        /// <summary>
        /// Kreira sliku sa tekstom za najavni ili odjavni špic
        /// </summary>
        private async Task<string> CreateTextImage(string text, double duration, bool isIntro)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"text_{Guid.NewGuid()}.png");

            await Task.Run(() =>
            {
                int surfW = 1920, surfH = 1080;
                var resParts = _selectedResolution?.Split('x');
                if (resParts?.Length == 2 && int.TryParse(resParts[0], out int rw) && int.TryParse(resParts[1], out int rh))
                { surfW = rw; surfH = rh; }

                using (var surface = SKSurface.Create(new SKImageInfo(surfW, surfH)))
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
        /// <summary>
        /// Dodaje crossfade između slika (fade out na kraju jedne, fade in na početku sljedeće)
        /// </summary>
        private void AddCrossfadeBetweenImages(List<TimelineItem> images, double timePerImage, double introDuration)
        {
            for (int i = 0; i < images.Count - 1; i++)
            {
                var current = images[i];
                var next = images[i + 1];

                double crossfadeDuration = 0.5;

                current.Keyframes.Add(new AnimationKeyframe { Time = current.Duration - crossfadeDuration, Opacity = 1 });
                current.Keyframes.Add(new AnimationKeyframe { Time = current.Duration, Opacity = 0 });

                next.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 0 });
                next.Keyframes.Add(new AnimationKeyframe { Time = crossfadeDuration, Opacity = 1 });
            }
        }
        private string GetEffectName(string mode, List<string> manualSequence, int index)
        {
            var effects = new List<string> {
        "ZoomIn", "ZoomOut", "SlideLeft", "SlideRight",
        "SlideUp", "SlideDown", "FadeIn", "FadeOut",
        "RotateLeft", "RotateRight", "KenBurns"
    };

            if (mode == "manual" && manualSequence.Count > 0)
            {
                return manualSequence[index % manualSequence.Count];
            }
            else if (mode == "random")
            {
                var random = new Random();
                return effects[random.Next(effects.Count)];
            }
            else // auto - ciklično
            {
                var autoEffects = new List<string> { "ZoomIn", "SlideLeft", "FadeIn", "ZoomOut", "SlideRight" };
                return autoEffects[index % autoEffects.Count];
            }
        }

        private void AddKeyframesForEffect(TimelineItem item, string effectName, double duration)
        {
            item.Keyframes.Clear();

            switch (effectName)
            {
                case "ZoomIn":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Zoom = 1.15 });
                    break;
                case "ZoomOut":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.15 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Zoom = 1.0 });
                    break;
                case "SlideLeft":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, X = -100 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, X = 0 });
                    break;
                case "SlideRight":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, X = 100 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, X = 0 });
                    break;
                case "SlideUp":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Y = -100 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Y = 0 });
                    break;
                case "SlideDown":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Y = 100 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Y = 0 });
                    break;
                case "FadeIn":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 0 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Opacity = 1 });
                    break;
                case "FadeOut":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 1 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Opacity = 0 });
                    break;
                case "RotateLeft":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Rotation = -5 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Rotation = 0 });
                    break;
                case "RotateRight":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Rotation = 5 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Rotation = 0 });
                    break;
                case "KenBurns":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0, X = 0, Y = 0 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Zoom = 1.2, X = 50, Y = 30 });
                    break;
            }
        }

        private void AddAnimatedText(string text, double duration, double startTime, string animation)
        {
            var subtitle = new SubtitleItem
            {
                Text = text,
                Start = startTime,
                End = startTime + duration
            };
            subtitles.Add(subtitle);
            lstSubtitles.Items.Add($"{FormatTime(startTime)} -> {FormatTime(startTime + duration)}: {text}");

            LogMessage($"Dodat tekst: '{text}' od {FormatTime(startTime)} do {FormatTime(startTime + duration)}", true);
        }
        private void AddTextToImage_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Selektujte sliku prvo", true);
                return;
            }

            if (!item.IsImage)
            {
                LogMessage(L("text_only_on_image"), true);
                return;
            }

            var dialog = new TextOverlayDialog($"Dodaj tekst na: {item.Name}");
            if (dialog.ShowDialog() == true)
            {
                item.TextOverlay = new TextOverlayData
                {
                    Text = dialog.Text,
                    Font = dialog.Font,
                    SelectedFontSize = dialog.SelectedFontSize,
                    Color = dialog.TextColor,
                    Position = dialog.Position,
                    Enabled = true
                };

                LogMessage($"Tekst '{dialog.Text}' dodat na sliku {item.Index}", true);
                PlayBeep();

                // Osvježi prikaz u ListView-u
                UpdateTimelineDisplay();
            }
        }
        private void btnTestFix_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                LogMessage($"=== TEST ===", true);
                LogMessage($"Prije: Start={item.Start}, End={item.End}, FixedPos={item.FixedPosition}, UseFixed={item.UseFixedPosition}", true);

                item.FixedPosition = 10;
                item.Duration = 5;
                item.UseFixedPosition = true;

                ApplyFixedPositions();

                LogMessage($"Poslije: Start={item.Start}, End={item.End}", true);
                UpdateTimelineDisplay();
            }
        }
        private void NavigateToItemByNumber(int number)
        {
            if (nativeListView == null || nativeListView.Items.Count == 0) return;

            if (number >= 1 && number <= nativeListView.Items.Count)
            {
                nativeListView.Items[number - 1].Selected = true;
                nativeListView.Items[number - 1].EnsureVisible();
                LogMessage($"Navigirano na klip {number}", true);
                PlayBeep();
            }
            else
            {
                LogMessage($"Broj {number} ne postoji (1-{nativeListView.Items.Count})", true);
            }
        }
        private void MoveToFirst_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                timelineItems.Remove(item);
                timelineItems.Insert(0, item);
                UpdateTimelineDisplay();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (nativeListView.Items.Count > 0)
                    {
                        nativeListView.Items[0].Selected = true;
                        nativeListView.Items[0].EnsureVisible();
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);

                LogMessage($"Klip '{item.Name}' pomjeren na prvo mjesto", true);
                PlayBeep();
            }
        }

        private void MoveToLast_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                timelineItems.Remove(item);
                timelineItems.Add(item);
                UpdateTimelineDisplay();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    int lastIndex = nativeListView.Items.Count - 1;
                    if (lastIndex >= 0)
                    {
                        nativeListView.Items[lastIndex].Selected = true;
                        nativeListView.Items[lastIndex].EnsureVisible();
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);

                LogMessage($"Klip '{item.Name}' pomjeren na zadnje mjesto", true);
                PlayBeep();
            }
        }

        private void MoveToPosition_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                int maxPos = timelineItems.Count;
                var dialog = new NumericDialog(maxPos);
                if (dialog.ShowDialog() == true)
                {
                    int newPosition = dialog.SelectedNumber;
                    if (newPosition >= 1 && newPosition <= maxPos)
                    {
                        SaveState();
                        int oldIndex = timelineItems.IndexOf(item);
                        timelineItems.RemoveAt(oldIndex);
                        timelineItems.Insert(newPosition - 1, item);
                        UpdateTimelineDisplay();

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (nativeListView.Items.Count > newPosition - 1)
                            {
                                nativeListView.Items[newPosition - 1].Selected = true;
                                nativeListView.Items[newPosition - 1].EnsureVisible();
                            }
                        }), System.Windows.Threading.DispatcherPriority.Background);

                        LogMessage($"Klip '{item.Name}' pomjeren na poziciju {newPosition}", true);
                        PlayBeep();
                    }
                }
            }
        }
        private async void btnPixabay_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PixabaySearchDialog();
            if (dialog.ShowDialog() == true && dialog.DownloadedMediaPaths.Count > 0)
            {
                SaveState();

                foreach (var mediaPath in dialog.DownloadedMediaPaths)
                {
                    string ext = Path.GetExtension(mediaPath).ToLower();
                    string type = (ext == ".mp4" || ext == ".avi" || ext == ".mov" || ext == ".mkv") ? "Video" : "Image";

                    var newItem = new TimelineItem
                    {
                        Path = mediaPath,
                        Duration = 5.0,
                        Name = Path.GetFileName(mediaPath),
                        Type = type,
                        Volume = 100,
                        TrackIndex = type == "Video" ? 0 : 0,
                        VideoEffect = new VideoEffectData()
                    };
                    timelineItems.Add(newItem);
                }

                UpdateTimelineDisplay();
                LogMessage($"✅ Dodato {dialog.DownloadedMediaPaths.Count} medija sa Pixabay-a", true);
                PlayBeep();
            }
        }
        private void btnAIVideoCreator_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AIVideoCreator();
            dialog.ShowDialog();
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void OpenVideoEngine_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new VideoEngineDialog();
            dlg.Owner = this;
            dlg.ShowDialog();
        }

        private void OpenVideoReview_Click(object sender, RoutedEventArgs e)
        {
            var thread = new System.Threading.Thread(() =>
            {
                var dlg = new VideoReviewDialog();
                dlg.Show();
                // Dispatcher.Run() — STA thread treba vlastiti message loop
                // bez ovoga, async/await i UI eventi ne rade
                System.Windows.Threading.Dispatcher.Run();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Seek media playera na zadanu poziciju u sekundama.
        /// Poziva VideoReviewDialog kad korisnik klikne "▶ Idi" na timestamp.
        /// </summary>
        public void SeekToPosition(double seconds)
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    long ms = (long)(seconds * 1000);
                    Dispatcher.Invoke(() =>
                    {
                        _mediaPlayer.Time = ms;
                        if (!_mediaPlayer.IsPlaying) _mediaPlayer.Play();
                    });
                    LogMessage($"▶ Seek na {TimeSpan.FromSeconds(seconds):mm\\:ss}", false);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Seek greška: {ex.Message}", false);
            }
        }

        // ── Multi-Format Export ──────────────────────────────────────
        private async void ExportMultiFormat_Click(object sender, RoutedEventArgs e)
        {
            string finalVideo = GetLastRenderedVideo();
            if (string.IsNullOrEmpty(finalVideo) || !File.Exists(finalVideo))
            {
                System.Windows.MessageBox.Show(
                    L("no_render_for_export"),
                    "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ffmpeg = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            string dir = System.IO.Path.GetDirectoryName(finalVideo);
            string name = System.IO.Path.GetFileNameWithoutExtension(finalVideo);

            LogMessage("Multi-format export: kreiram Shorts (9:16) i Instagram (1:1)...", true);

            try
            {
                var results = await MultiFormatExport.ExportAllFormats(
                    finalVideo, dir, ffmpeg, name,
                    new System.Threading.CancellationToken());

                string nl = Environment.NewLine;
                string msg = "Export zavrsen!" + nl + nl;
                msg += "Original (16:9): " + System.IO.Path.GetFileName(finalVideo) + nl;
                if (results.HasShorts) msg += "Shorts/TikTok (9:16): " + System.IO.Path.GetFileName(results.Shorts) + nl;
                if (results.HasSquare) msg += "Instagram (1:1): " + System.IO.Path.GetFileName(results.Square) + nl;
                msg += nl + "Svi fajlovi su u: " + dir;

                LogMessage(L("export_done"), true);
                System.Windows.MessageBox.Show(msg, "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("export_error"), ex.Message), true);
            }
        }

        private string GetLastRenderedVideo()
        {
            if (string.IsNullOrEmpty(currentProjectFolder)) return null;
            var files = System.IO.Directory.GetFiles(currentProjectFolder, "*.mp4");
            if (files.Length == 0) return null;
            // Vrati najnoviji MP4 fajl
            return files.OrderByDescending(f => System.IO.File.GetLastWriteTime(f))
                        .FirstOrDefault();
        }

        // ── Vision AI - opisi klipova ────────────────────────────────
        private async void DescribeAllClips_Click(object sender, RoutedEventArgs e)
        {
            if (!await VisionAI.IsVisionModelAvailable("moondream"))
            {
                string models = string.Join(Environment.NewLine, VisionAI.RecommendedModels
                    .Select(m => "  " + m.pullCmd));
                string visionMsg = "Vision AI model nije dostupan." + Environment.NewLine + Environment.NewLine +
                    "Instaliraj jedan od modela:" + Environment.NewLine + models;
                System.Windows.MessageBox.Show(visionMsg,
                    "Vision AI", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string ffmpeg = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            LogMessage("Vision AI: Generisujem opise klipova...", true);
            var items = timelineItems.Where(i => !i.IsAudio).ToList();

            await VisionAI.DescribeAllClips(
                items, ffmpeg, "moondream",
                (idx2, desc) =>
                {
                    if (idx2 == -1 || idx2 == -2)
                        LogMessage(desc, true);
                    else
                        Dispatcher.Invoke(() =>
                        {
                            LogMessage(desc, true);
                            UpdateTimelineDisplay();
                        });
                });

        }
        // OVE KLASE DODATI NA KRAJ FAJLA (poslije zatvaranja MainWindow klase)

        public class CloudflareImageResponse
        {
            public CloudflareImageResult result { get; set; }
            public bool success { get; set; }
        }

        public class CloudflareImageResult
        {
            public string image { get; set; }
        }

        public class LlavaResponse
        {
            public LlavaResult result { get; set; }
        }

        public class LlavaResult
        {
            public string response { get; set; }
        }

        // Dodaj ove klase ako nedostaju
        public class AILayoutResponse
        {
            public List<AILayoutItem> raspored { get; set; }
        }

        public class AILayoutItem
        {
            public int animacija_index { get; set; }
            public double pocetak { get; set; }
            public double kraj { get; set; }
            public string razlog { get; set; }
        }
    }
}