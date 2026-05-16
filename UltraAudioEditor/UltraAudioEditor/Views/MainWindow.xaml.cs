using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltraAudioEditor.ViewModels;
using UltraAudioEditor.Controls;
using UltraAudioEditor.Views.Controls;

namespace UltraAudioEditor.Views
{
    public partial class MainWindow : Window
    {
        private MainViewModel VM => (MainViewModel)DataContext;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            VM.OnToggleWorkspaceMode = () =>
            {
                if (VM.IsJawsMode) SetVisualMode(); else SetJawsMode();
            };
            VM.OnRebuildTrackList = () =>
            {
                Dispatcher.Invoke(() =>
                {
                    AccessibleTrackList.DataContext = VM;
                    AccessibleTrackList.Rebuild();
                });
            };
            AccessibleTrackList.DataContext = VM;
            // PropertyChanged na traci trigguje rebuild
            VM.Project.Tracks.CollectionChanged += (_, __) =>
                Dispatcher.Invoke(() => AccessibleTrackList.Rebuild());
            VM.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(VM.PlayheadPosition) or nameof(VM.TimeDisplay)
                    or nameof(VM.SelectedClip) or nameof(VM.SelectedTrack))
                    Dispatcher.Invoke(() => AccessibleTrackList.UpdateStatus());
            };
            // PreviewKeyDown hvata Space/S/R prije nego List kontrole progutaju event
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            this.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            VM.Announce("Ultra Audio Editor ucitan. Alt+W za JAWS mod, F6 za status, Ctrl+I za uvoz.");
        }

        private void MenuItem_Exit(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Izaći iz Ultra Audio Editora?", "Izlaz",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Application.Current.Shutdown();
        }

        private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            VM.AiApiKey = ((PasswordBox)sender).Password;
        }

        private void WaveformArea_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    var ext = System.IO.Path.GetExtension(file).ToLower();
                    if (ext is ".wav" or ".mp3" or ".ogg" or ".flac" or ".m4a" or ".aiff" or ".aif")
                    {
                        var track = VM.AddTrackInternal(System.IO.Path.GetFileNameWithoutExtension(file));
                        double dur = 5;
                        try { using var r = new NAudio.Wave.AudioFileReader(file); dur = r.TotalTime.TotalSeconds; } catch { }
                        track.Clips.Add(new Models.AudioClip
                        {
                            Name = System.IO.Path.GetFileName(file),
                            FilePath = file,
                            StartTime = 0,
                            Duration = dur,
                            WaveformData = Services.AudioEngine.LoadWaveformData(file)
                        });
                        VM.Announce($"Prevučen fajl: {System.IO.Path.GetFileName(file)}, trajanje {dur:F1}s.");
                    }
                }
            }
        }

        private void WaveformArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is WaveformControl wc)
            {
                var track = wc.DataContext as Models.AudioTrack;
                if (track == null) return;

                VM.SelectedTrack = track;
                double x = e.GetPosition(wc).X;
                double duration = Math.Max(1, VM.Project.Duration);
                double clickTime = (x / wc.ActualWidth) * duration / VM.ZoomLevel;

                // Provjeri da li je klik na neki klip
                Models.AudioClip? clickedClip = null;
                foreach (var clip in track.Clips)
                {
                    double clipX     = clip.StartTime / duration * wc.ActualWidth * VM.ZoomLevel;
                    double clipEndX  = (clip.StartTime + clip.Duration) / duration * wc.ActualWidth * VM.ZoomLevel;
                    if (x >= clipX && x <= clipEndX)
                    {
                        clickedClip = clip;
                        break;
                    }
                }

                if (clickedClip != null)
                {
                    // Selektuj klip
                    VM.SelectClip(clickedClip, track);
                    // Dvostruki klik otvara dialog
                    if (e.ClickCount == 2)
                        VM.OpenSetClipPositionDialog();
                }
                else
                {
                    // Klik na prazno — postavi playhead
                    VM.SelectedClip = null;
                    VM.PlayheadPosition = Math.Max(0, clickTime);
                    VM.Announce($"Pozicija: {VM.TimeDisplay}.");
                }
            }
        }

        private void ShowShortcuts_Click(object sender, RoutedEventArgs e)
        {
            var msg = @"TASTATURNE PREČICE - Ultra Audio Editor

TRANSPORT:
  Space          — Reprodukuj / Pauziraj
  S              — Zaustavi
  R              — Snimi
  L              — Loop uključi/isključi
  Home           — Na početak
  End            — Na kraj

FAJL:
  Ctrl+N         — Novi projekat
  Ctrl+I         — Uvezi audio
  Ctrl+E         — Izvezi audio

UREĐIVANJE:
  Ctrl+Z         — Poništi
  Ctrl+Y         — Ponovi
  Ctrl+D         — Dupliraj traku

TRAKE:
  Ctrl+Alt+T     — Nova traka
  Alt+Up         — Traka gore
  Alt+Down       — Traka dole

PRIKAZ:
  Ctrl++         — Uvećaj
  Ctrl+-         — Umanji
  Ctrl+0         — Podesi na ekran

KLIPOVI:
  Klik na klip      — Odaberi klip
  Dvostruki klik    — Postavi poziciju (dialog)
  F2                — Postavi poziciju odabranog klipa
  Ctrl+Lijevo       — Pomjeri klip lijevo 1s
  Ctrl+Desno        — Pomjeri klip desno 1s
  Ctrl+Shift+Lijevo — Pomjeri klip lijevo 0.1s
  Ctrl+Shift+Desno  — Pomjeri klip desno 0.1s
  Shift+Delete      — Obriši klip
  Desni klik        — Kontekst meni klipa

NAVIGACIJA (JAWS):
  Tab            — Sledeći element
  Shift+Tab      — Prethodni element
  Enter/Space    — Aktiviraj dugme
  Alt+F4         — Izlaz";

            MessageBox.Show(msg, "Tastaturne prečice - Ultra Audio Editor",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenApiKeyLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = VM.ApiKeyLink;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

                // ─── Dual-Mode Workspace ───────────────────────────────────────────

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var mods = e.KeyboardDevice.Modifiers;
            bool noMods = mods == System.Windows.Input.ModifierKeys.None;

            // Ako je fokus UNUTAR JAWS panela (AccessibleTrackList),
            // ne palimo globalne transport prečice — kontrola sama hendla šta treba.
            bool focusInJaws = VM.IsJawsMode && AccessibleTrackList.IsKeyboardFocusWithin;

            // Space — Play/Pause SAMO kad fokus nije u JAWS panelu
            if (e.Key == System.Windows.Input.Key.Space && noMods && !focusInJaws)
            {
                VM.PlayPauseCommand.Execute(null);
                e.Handled = true;
                return;
            }
            // S — Stop (ne u textboxu, ne u JAWS panelu)
            if (e.Key == System.Windows.Input.Key.S && noMods
                && !focusInJaws
                && !(e.OriginalSource is System.Windows.Controls.TextBox))
            {
                VM.StopCommand.Execute(null);
                e.Handled = true;
                return;
            }
            // Home/End — transport SAMO kad fokus nije u JAWS panelu
            if ((e.Key == System.Windows.Input.Key.Home || e.Key == System.Windows.Input.Key.End)
                && noMods && focusInJaws)
            {
                // Ne radimo ništa — JAWS panel sam hendla Home/End
                return;
            }
            // F6 — Status (uvijek OK)
            if (e.Key == System.Windows.Input.Key.F6 && noMods)
            {
                VM.AnnounceProjectStatus();
                e.Handled = true;
                return;
            }
        }

        private static string FormatDuration(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}.{2:D2}", (int)ts.TotalMinutes, ts.Seconds, ts.Milliseconds / 10);
        }

        private void BtnVisualMode_Click(object sender, RoutedEventArgs e) => SetVisualMode();
        private void BtnJawsMode_Click(object sender, RoutedEventArgs e) => SetJawsMode();

        private void SetVisualMode()
        {
            VisualWorkspace.Visibility     = System.Windows.Visibility.Visible;
            AccessibleTrackList.Visibility = System.Windows.Visibility.Collapsed;
            CurrentModeLabel.Text      = "● VIZUALNI MOD";
            BtnVisualMode.Style        = (System.Windows.Style)FindResource("AIButton");
            BtnJawsMode.Style          = (System.Windows.Style)FindResource("StdButton");
            VM.IsJawsMode = false;
            VM.Announce("Vizualni mod aktiviran. Waveform prikaz.");
        }

        private void SetJawsMode()
        {
            VisualWorkspace.Visibility      = System.Windows.Visibility.Collapsed;
            AccessibleTrackList.Visibility  = System.Windows.Visibility.Visible;
            CurrentModeLabel.Text           = "● JAWS MOD";
            BtnJawsMode.Style               = (System.Windows.Style)FindResource("AIButton");
            BtnVisualMode.Style             = (System.Windows.Style)FindResource("StdButton");
            VM.IsJawsMode = true;
            AccessibleTrackList.Rebuild();
            AccessibleTrackList.FocusFirstTrack();
            VM.Announce("JAWS mod aktiviran. Tab za navigaciju, Shift+F10 za meni trake, F6 za status.");
        }

        private void RefreshJawsSummary()
        {
            // AccessibleTrackList.UpdateStatus() preuzeo ovu ulogu
            if (VM.IsJawsMode)
                AccessibleTrackList.UpdateStatus();
        }

        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            // Alt+W — prebaci mod
            if (e.Key == System.Windows.Input.Key.W &&
                e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Alt)
            {
                if (VM.IsJawsMode) SetVisualMode(); else SetJawsMode();
                e.Handled = true;
                return;
            }
            // F6 — čitaj status projekta
            if (e.Key == System.Windows.Input.Key.F6)
            {
                VM.AnnounceProjectStatus();
                e.Handled = true;
                return;
            }
        }

        private void ShowAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Ultra Audio Editor v1.0\n\n" +
                "Profesionalni Windows audio editor sa punom pristupačnošću.\n" +
                "Kompatibilan sa JAWS for Windows čitačem ekrana.\n\n" +
                "Tehnologije:\n" +
                "• WPF (.NET 8)\n" +
                "• NAudio — audio engine\n" +
                "• Anthropic Claude AI — AI funkcije\n\n" +
                "AI funkcije zahtijevaju Anthropic API ključ.\n" +
                "Dobijte ga na: console.anthropic.com",
                "O Ultra Audio Editoru",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
