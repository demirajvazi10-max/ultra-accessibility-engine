using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;
using UltraAudioEditor.Models;
using UltraAudioEditor.Services;
using UltraAudioEditor.ViewModels;

namespace UltraAudioEditor.Views.Controls
{
    public partial class AccessibleTrackList : UserControl
    {
        private MainViewModel? _vm;
        private AudioTrack?    _activeTrack;
        private Slider?        _playheadSlider;
        private TextBlock?     _playheadTimeBlock;

        public AccessibleTrackList()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _vm = DataContext as MainViewModel
               ?? Window.GetWindow(this)?.DataContext as MainViewModel;
            if (_vm == null) return;

            _vm.Project.Tracks.CollectionChanged += (_, __) => RebuildTrackList();
            _vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MainViewModel.PlayheadPosition)
                                      or nameof(MainViewModel.TimeDisplay))
                { UpdateStatus(); SyncSlider(); }
            };

            // Jedan PreviewKeyDown na UserControl nivou — hvata SVE prije djece
            PreviewKeyDown += OnPreviewKeyDown;
            BuildPlayheadPanel();
            RebuildTrackList();
        }

        // ════════════════════════════════════════════════════════════════════
        // CENTRALNI KEYBOARD HANDLER
        // ════════════════════════════════════════════════════════════════════
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = e.KeyboardDevice.Modifiers;
            bool none  = mods == ModifierKeys.None;
            bool ctrl  = mods == ModifierKeys.Control;
            bool cs    = mods == (ModifierKeys.Control | ModifierKeys.Shift);
            bool shift = mods == ModifierKeys.Shift;

            // ── Kontekstni meni (Shift+F10 ili Apps) ──
            if ((e.Key == Key.F10 && shift) || e.Key == Key.Apps)
            {
                OpenContextMenuForFocus();
                e.Handled = true;
                return;
            }

            // ── Space: blokiraj propagaciju prema Window (play/pause)
            //    Kad je fokus na slideru, Space = play/pause.
            //    Kad je fokus na ListBox/ListView, Space = selekcija (WPF default).
            //    U oba slučaja: ne propagiraj dalje na Window.
            if (e.Key == Key.Space && none)
            {
                if (_playheadSlider != null && _playheadSlider.IsKeyboardFocusWithin)
                {
                    _vm?.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                }
                // Za ListBox/ListView pustimo WPF default, ali blokiramo Window
                // (MainWindow.cs provjerava IsKeyboardFocusWithin)
                return;
            }

            // ── Playhead slider fokusiran ──
            if (_playheadSlider != null && _playheadSlider.IsKeyboardFocusWithin)
            {
                switch (e.Key)
                {
                    case Key.Right when none: Seek(+0.1); e.Handled = true; break;
                    case Key.Left  when none: Seek(-0.1); e.Handled = true; break;
                    case Key.Right when ctrl: Seek(+1.0); e.Handled = true; break;
                    case Key.Left  when ctrl: Seek(-1.0); e.Handled = true; break;
                    case Key.Home  when none: SeekTo(0);  e.Handled = true; break;
                    case Key.End   when none: SeekTo(_vm?.Project.Duration ?? 0); e.Handled = true; break;
                    case Key.Return: case Key.Tab: TrackListBox.Focus(); e.Handled = true; break;
                }
                return;
            }

            // ── ListView fajlova fokusiran ──
            if (WorkspaceContent.IsKeyboardFocusWithin && _activeTrack != null)
            {
                var lv = FindFocusedLV();
                if (lv?.SelectedItem is ClipRow cr)
                {
                    var clip = cr.Clip;
                    switch (e.Key)
                    {
                        case Key.F2:     OpenSetPos(clip); e.Handled = true; break;
                        case Key.Delete when none: DelClip(clip); e.Handled = true; break;
                        case Key.Right when ctrl:  MoveClip(clip,  1.0); e.Handled = true; break;
                        case Key.Left  when ctrl:  MoveClip(clip, -1.0); e.Handled = true; break;
                        case Key.Right when cs:    MoveClip(clip,  0.1); e.Handled = true; break;
                        case Key.Left  when cs:    MoveClip(clip, -0.1); e.Handled = true; break;
                        case Key.Home  when none:  lv.SelectedIndex = 0; e.Handled = true; break;
                        case Key.End   when none:  lv.SelectedIndex = lv.Items.Count - 1; e.Handled = true; break;
                    }
                }
            }
        }

        private void Seek(double delta)
        {
            if (_vm == null) return;
            _vm.PlayheadPosition = Math.Clamp(_vm.PlayheadPosition + delta, 0, _vm.Project.Duration);
            _vm.Announce($"Playhead: {_vm.TimeDisplay}");
        }
        private void SeekTo(double pos)
        {
            if (_vm == null) return;
            _vm.PlayheadPosition = Math.Clamp(pos, 0, _vm.Project.Duration);
            _vm.Announce($"Playhead: {_vm.TimeDisplay}");
        }
        private void OpenSetPos(AudioClip clip)
        {
            if (_vm == null || _activeTrack == null) return;
            _vm.SelectedTrack = _activeTrack; _vm.SelectedClip = clip;
            _vm.OpenSetClipPositionDialog();
        }
        private void DelClip(AudioClip clip) { if (_vm != null) { _vm.SelectedClip = clip; _vm.DeleteClipCommand.Execute(null); } }
        private void MoveClip(AudioClip clip, double delta)
        {
            if (_vm == null) return;
            _vm.SelectedClip = clip;
            clip.StartTime = Math.Max(0, clip.StartTime + delta);
            _vm.Announce($"Klip na {FormatSec(clip.StartTime)}.");
            if (_activeTrack != null) BuildFileList(_activeTrack);
            UpdateStatus();
        }

        // ════════════════════════════════════════════════════════════════════
        // KONTEKSTNI MENI — otvara se na pravo mjesto
        // ════════════════════════════════════════════════════════════════════
        private void OpenContextMenuForFocus()
        {
            // 1. Lista traka
            if (TrackListBox.IsKeyboardFocusWithin
                && TrackListBox.SelectedItem is ListBoxItem lbi
                && lbi.Tag is AudioTrack t)
            {
                var m = BuildTrackMenu(t);
                m.PlacementTarget = TrackListBox;
                m.Placement = PlacementMode.Bottom;
                m.IsOpen = true;
                return;
            }
            // 2. Playhead slider
            if (_playheadSlider?.IsKeyboardFocusWithin == true && _activeTrack != null)
            {
                var m = BuildTrackMenu(_activeTrack);
                m.PlacementTarget = _playheadSlider;
                m.Placement = PlacementMode.Bottom;
                m.IsOpen = true;
                return;
            }
            // 3. ListView fajlova
            var lv = FindFocusedLV();
            if (lv != null && _activeTrack != null)
            {
                ContextMenu menu = lv.SelectedItem is ClipRow cr
                    ? BuildClipMenu(cr.Clip, _activeTrack)
                    : BuildTrackMenu(_activeTrack);
                menu.PlacementTarget = lv;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }

        private ListView? FindFocusedLV()
        {
            foreach (UIElement c in WorkspaceContent.Children)
                if (c is ListView lv && lv.IsKeyboardFocusWithin) return lv;
            return null;
        }

        // ════════════════════════════════════════════════════════════════════
        // PLAYHEAD PANEL
        // ════════════════════════════════════════════════════════════════════
        private void BuildPlayheadPanel()
        {
            PlayheadPanel.Children.Clear();
            var grid = new Grid { Margin = new Thickness(8, 5, 8, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var phLbl = new TextBlock
            {
                Text = "PLAYHEAD", FontSize = 10, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 184)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(phLbl, 0); grid.Children.Add(phLbl);

            _playheadSlider = new Slider
            {
                Minimum = 0, Maximum = Math.Max(10, _vm?.Project.Duration ?? 60),
                Value = _vm?.PlayheadPosition ?? 0,
                VerticalAlignment = VerticalAlignment.Center,
                LargeChange = 5.0, SmallChange = 0.1, IsTabStop = true
            };
            _playheadSlider.SetValue(AutomationProperties.NameProperty,
                "Playhead pozicija. Strelice levo/desno za 0.1 sekunde, " +
                "Ctrl+strelice za 1 sekundu, Home početak, End kraj. " +
                "Space za reprodukciju. Enter ili Tab za listu traka. " +
                "Shift+F10 za kontekstni meni trake.");
            _playheadSlider.ValueChanged += (_, ev) =>
            {
                if (_vm != null && Math.Abs(_vm.PlayheadPosition - ev.NewValue) > 0.001)
                    _vm.PlayheadPosition = ev.NewValue;
                if (_playheadTimeBlock != null) _playheadTimeBlock.Text = FormatSec(ev.NewValue);
            };
            Grid.SetColumn(_playheadSlider, 1); grid.Children.Add(_playheadSlider);

            _playheadTimeBlock = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 207, 160)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0), MinWidth = 100, Text = "00:00.00"
            };
            _playheadTimeBlock.SetValue(AutomationProperties.NameProperty, "Playhead pozicija");
            _playheadTimeBlock.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Assertive);
            Grid.SetColumn(_playheadTimeBlock, 2); grid.Children.Add(_playheadTimeBlock);

            var btnGoto = new Button
            {
                Content = "Idi na...", Height = 24, Padding = new Thickness(8, 0, 8, 0),
                Style = (Style)Application.Current.Resources["StdButton"],
                VerticalAlignment = VerticalAlignment.Center
            };
            btnGoto.SetValue(AutomationProperties.NameProperty, "Idi na poziciju, otvara dijalog");
            btnGoto.Click += (_, __) =>
            {
                if (_vm == null) return;
                var d = new SetValueDialog("Idi na poziciju", "Sekunde (15.01) ili MM:SS (1:30):", _vm.PlayheadPosition.ToString("F2"), "s");
                d.Owner = Window.GetWindow(this);
                if (d.ShowDialog() == true) { double p = ParsePos(d.ResultValue); if (p >= 0) { _vm.PlayheadPosition = Math.Clamp(p, 0, _vm.Project.Duration); SyncSlider(); } }
            };
            Grid.SetColumn(btnGoto, 3); grid.Children.Add(btnGoto);

            PlayheadPanel.Children.Add(grid);
            PlayheadPanel.Children.Add(new TextBlock
            {
                Text = "Strelice = 0.1s  |  Ctrl+strelice = 1s  |  Home/End  |  Space = reprodukcija  |  Shift+F10 = meni",
                FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 130)),
                Margin = new Thickness(8, 0, 8, 3)
            });
            SyncSlider();
        }

        private void SyncSlider()
        {
            if (_vm == null || _playheadSlider == null) return;
            _playheadSlider.Maximum = Math.Max(10, _vm.Project.Duration);
            if (Math.Abs(_playheadSlider.Value - _vm.PlayheadPosition) > 0.001)
                _playheadSlider.Value = _vm.PlayheadPosition;
            if (_playheadTimeBlock != null) _playheadTimeBlock.Text = FormatSec(_vm.PlayheadPosition);
        }

        // ════════════════════════════════════════════════════════════════════
        // LISTA TRAKA
        // ════════════════════════════════════════════════════════════════════
        public void RebuildTrackList()
        {
            if (_vm == null) return;
            TrackListBox.Items.Clear();
            foreach (var track in _vm.Project.Tracks)
            {
                var item = new ListBoxItem { Tag = track };
                item.SetValue(AutomationProperties.NameProperty, TrackAria(track));
                item.Content = BuildTrackContent(track);
                TrackListBox.Items.Add(item);
                track.PropertyChanged         += (_, __) => Dispatcher.Invoke(() => RefreshItem(item, track));
                track.Clips.CollectionChanged += (_, __) => Dispatcher.Invoke(() => RefreshItem(item, track));
            }
            if (TrackListBox.Items.Count > 0) TrackListBox.SelectedIndex = 0;
            SyncSlider(); UpdateStatus();
        }

        public void Rebuild() => RebuildTrackList();

        private void RefreshItem(ListBoxItem item, AudioTrack track)
        {
            item.Content = BuildTrackContent(track);
            item.SetValue(AutomationProperties.NameProperty, TrackAria(track));
            if (_activeTrack == track) BuildFileList(track);
            SyncSlider();
        }

        private static UIElement BuildTrackContent(AudioTrack track)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bar = new Border { Background = new SolidColorBrush(track.Color), Width = 4 };
            Grid.SetColumn(bar, 0); g.Children.Add(bar);

            var icon = new TextBlock
            {
                Text = track.Type switch { TrackType.Vocal => "🎤", TrackType.Instrumental => "🎸", TrackType.Effects => "🎛", _ => track.Clips.Count > 0 ? "🎵" : "📁" },
                FontSize = 14, Margin = new Thickness(8, 10, 6, 10), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 1); g.Children.Add(icon);

            var stack = new StackPanel { Margin = new Thickness(0, 8, 8, 8) };
            var name = new TextBlock { FontSize = 13, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(Color.FromRgb(232, 232, 240)), TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name") { Source = track, Mode = System.Windows.Data.BindingMode.OneWay });
            stack.Children.Add(name);
            stack.Children.Add(new TextBlock
            {
                Text = $"{track.Type}  ·  Vol {track.Volume:P0}{(track.IsMuted ? "  [MUTE]" : "")}{(track.IsSolo ? "  [SOLO]" : "")}  ·  {track.Clips.Count} fajl(ova)",
                FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 184))
            });
            if (track.Clips.Count > 0)
                stack.Children.Add(new TextBlock { Text = $"Trajanje: {FormatSec(track.Clips.Max(c => c.StartTime + c.Duration))}", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(100, 160, 100)) });
            Grid.SetColumn(stack, 2); g.Children.Add(stack);
            return g;
        }

        private static string TrackAria(AudioTrack t)
        {
            double dur = t.Clips.Count > 0 ? t.Clips.Max(c => c.StartTime + c.Duration) : 0;
            return $"Traka {t.Name}, {t.Type}, glasnoća {t.Volume:P0}, " +
                   $"{t.Clips.Count} fajlova, trajanje {FormatSec(dur)}" +
                   $"{(t.IsMuted ? ", utišano" : "")}{(t.IsSolo ? ", solo" : "")}. " +
                   "Shift+F10 za meni trake.";
        }

        private void TrackListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrackListBox.SelectedItem is ListBoxItem item && item.Tag is AudioTrack track)
            {
                _vm!.SelectedTrack = track; _vm.SelectedClip = null;
                _activeTrack = track;
                BuildFileList(track);
                UpdateStatus();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // LISTA FAJLOVA — jedini sadržaj desnog panela
        // ════════════════════════════════════════════════════════════════════
        private void BuildFileList(AudioTrack track)
        {
            WorkspaceContent.Children.Clear();
            TxtWorkspaceTitle.Text =
                $"{track.Name}  [{track.Type}]  " +
                $"Vol: {track.Volume:P0}  Pan: {track.Pan:F2}" +
                $"{(track.IsMuted ? "  [MUTE]" : "")}{(track.IsSolo ? "  [SOLO]" : "")}";

            WorkspaceContent.Children.Add(new TextBlock
            {
                Text = "Shift+F10 = meni  |  F2 = postavi poziciju  |  Ctrl+←/→ = pomjeri 1s  |  Del = obriši",
                FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 130)),
                Margin = new Thickness(8, 5, 8, 4)
            });

            if (track.Clips.Count == 0)
            {
                WorkspaceContent.Children.Add(new TextBlock
                {
                    Text = "📁  Nema fajlova na ovoj traci.\nShift+F10 → \"Uvezi audio\" ili Ctrl+I.",
                    FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 180)),
                    Margin = new Thickness(16, 20, 16, 8), TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            var lv = new ListView
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 34)),
                BorderThickness = new Thickness(0, 1, 0, 1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 82)),
                SelectionMode = SelectionMode.Single, IsTabStop = true
            };
            lv.SetValue(AutomationProperties.NameProperty,
                $"Fajlovi trake {track.Name}. " +
                "Strelice gore i dole za navigaciju. " +
                "Shift+F10 za kontekstni meni fajla. " +
                "F2 za postavljanje pozicije. " +
                "Ctrl+strelice za pomjeranje. " +
                "Delete za brisanje.");

            var gv = new GridView();
            gv.Columns.Add(MkCol("Naziv",     "Name",         200));
            gv.Columns.Add(MkCol("Početak",   "StartTimeFmt",  90));
            gv.Columns.Add(MkCol("Poč. (s)",  "StartTimeStr",  70));
            gv.Columns.Add(MkCol("Trajanje",  "DurationFmt",   90));
            gv.Columns.Add(MkCol("Tra. (s)",  "DurationStr",   70));
            gv.Columns.Add(MkCol("Kraj",      "EndTimeFmt",    90));
            lv.View = gv;

            foreach (var clip in track.Clips)
                lv.Items.Add(new ClipRow(clip));

            lv.SelectionChanged += (_, __) =>
            {
                if (lv.SelectedItem is ClipRow cr) { _vm!.SelectedClip = cr.Clip; UpdateStatus(); }
            };
            if (_vm!.SelectedClip != null)
            {
                var row = lv.Items.OfType<ClipRow>().FirstOrDefault(r => r.Clip == _vm.SelectedClip);
                if (row != null) lv.SelectedItem = row;
            }
            WorkspaceContent.Children.Add(lv);
        }

        private static GridViewColumn MkCol(string h, string b, double w) =>
            new() { Header = h, Width = w, DisplayMemberBinding = new System.Windows.Data.Binding(b) };

        // ════════════════════════════════════════════════════════════════════
        // MENI TRAKE — ISKLJUČIVO čiste tekstualne stavke, bez WPF kontrola
        // Sve što treba slajder → otvara se poseban dijalog (JAWS čita normalno)
        // ════════════════════════════════════════════════════════════════════
        private ContextMenu BuildTrackMenu(AudioTrack track)
        {
            var m = new ContextMenu();
            double ph = _vm?.PlayheadPosition ?? 0;
            var fx = track.Effects;

            MIH(m, $"  {track.Name}  [{track.Type}]");
            MIH(m, $"  Vol: {track.Volume:P0}  Pan: {track.Pan:F2}  |  Playhead: {FormatSec(ph)}");
            Sep(m);

            // ── Uvoz ──────────────────────────────────────────────────────
            MI(m, "Uvezi audio na ovu traku\tCtrl+I",
                () => { _vm!.SelectedTrack = track; _vm.ImportAudioCommand.Execute(null); });
            MI(m, $"Uvezi i postavi na playhead  ({FormatSec(ph)})",
                () => ImportAt(track, ph));
            MI(m, "Uvezi i postavi na poziciju...",
                () => { double p = AskPos("Uvezi na poziciju", ph); if (p >= 0) ImportAt(track, p); });
            MI(m, "Uvezi na novu traku",
                () => { _vm!.AddTrackCommand.Execute(null); _vm!.ImportAudioCommand.Execute(null); });
            Sep(m);

            // ── Razdvajanje (Demucs) ───────────────────────────────────────
            MI(m, "Razdvoji vokal i instrumental (Demucs)...", () => OpenDemucsDialog(track));
            Sep(m);

            // ── Mute / Solo ────────────────────────────────────────────────
            MI(m, (track.IsMuted ? "✓ " : "    ") + "Mute traku\tM",
                () => { track.IsMuted = !track.IsMuted; _vm!.Announce($"Mute {(track.IsMuted ? "On" : "Off")}"); RefreshActive(); });
            MI(m, (track.IsSolo ? "✓ " : "    ") + "Solo traku",
                () => { track.IsSolo = !track.IsSolo; _vm!.Announce($"Solo {(track.IsSolo ? "On" : "Off")}"); RefreshActive(); });
            Sep(m);

            // ── Glasnoća / Panorama → dijalog ─────────────────────────────
            MI(m, $"Glasnoća:  {track.Volume:P0}  (podesi)...",
                () =>
                {
                    var d = new SetValueDialog("Glasnoća trake", "Unesite glasnoću od 0 do 100:", (track.Volume * 100).ToString("F0"), "%");
                    d.Owner = Window.GetWindow(this);
                    if (d.ShowDialog() == true && float.TryParse(d.ResultValue, out float v))
                    { track.Volume = Math.Clamp(v / 100f, 0f, 1f); _vm?.Announce($"Glasnoća: {track.Volume:P0}"); RefreshActive(); }
                });
            MI(m, $"Panorama:  {track.Pan:F2}  (podesi)...",
                () =>
                {
                    var d = new SetValueDialog("Panorama", "Levo -100, centar 0, desno 100:", (track.Pan * 100).ToString("F0"), "");
                    d.Owner = Window.GetWindow(this);
                    if (d.ShowDialog() == true && float.TryParse(d.ResultValue, out float v))
                    { track.Pan = Math.Clamp(v / 100f, -1f, 1f); _vm?.Announce($"Pan: {track.Pan:F2}"); RefreshActive(); }
                });
            Sep(m);

            // ── Efekti — svaki otvara vlastiti dijalog sa slajderima ──────
            // Format: "✓ Efekat uključen..." ili "    Efekat isključen..."
            // Dijalog ima slajdere koje JAWS čita normalno — bez WPF u Headeru
            var fxSub = new MenuItem { Header = "Efekti..." };
            fxSub.SetValue(AutomationProperties.NameProperty, "Podmeni efekti");

            AddFxMI(fxSub, "Equalizer (EQ)", fx.EqEnabled, track,
                () => OpenEffectDialog("Equalizer (EQ)", track, EffectType.Equalizer));
            AddFxMI(fxSub, "Reverb", fx.ReverbEnabled, track,
                () => OpenEffectDialog("Reverb", track, EffectType.Reverb));
            AddFxMI(fxSub, "Delay / Echo", fx.DelayEnabled, track,
                () => OpenEffectDialog("Delay / Echo", track, EffectType.Delay));
            AddFxMI(fxSub, "Kompresor", fx.CompressorEnabled, track,
                () => OpenEffectDialog("Kompresor", track, EffectType.Compressor));
            AddFxMI(fxSub, "Noise Gate", fx.NoiseGateEnabled, track,
                () => OpenEffectDialog("Noise Gate", track, EffectType.NoiseGate));
            AddFxMI(fxSub, "Bass Boost", fx.BassBostEnabled, track,
                () => OpenEffectDialog("Bass Boost", track, EffectType.BassBoost));
            AddFxMI(fxSub, "Pitch Shift", fx.PitchEnabled, track,
                () => OpenEffectDialog("Pitch Shift", track, EffectType.PitchShift));
            AddFxMI(fxSub, "Chorus", fx.ChorusEnabled, track,
                () => OpenEffectDialog("Chorus", track, EffectType.Chorus));

            m.Items.Add(fxSub);
            Sep(m);

            // ── Obrada ─────────────────────────────────────────────────────
            MI(m, "Normalizuj glasnoću",  () => { _vm!.SelectedTrack = track; _vm.NormalizeCommand.Execute(null); });
            MI(m, "Fade In (2 sekunde)",  () => { _vm!.SelectedTrack = track; _vm.FadeInCommand.Execute(null); });
            MI(m, "Fade Out (2 sekunde)", () => { _vm!.SelectedTrack = track; _vm.FadeOutCommand.Execute(null); });
            Sep(m);

            // ── Kombinovanje ───────────────────────────────────────────────
            var others = _vm!.Project.Tracks.Where(t => t != track && t.Clips.Any()).ToList();
            if (others.Any())
            {
                var combSub = new MenuItem { Header = "Kombinuj sa trakom..." };
                combSub.SetValue(AutomationProperties.NameProperty, "Podmeni kombinuj sa drugom trakom");
                foreach (var o in others)
                {
                    var oo = o;
                    var si = new MenuItem { Header = $"{oo.Name}  ({oo.Type}  ·  {oo.Clips.Count} fajlova)" };
                    si.SetValue(AutomationProperties.NameProperty, $"Kombinuj sa trakom {oo.Name}");
                    si.Click += (_, __) => CombineDialog(track, oo);
                    combSub.Items.Add(si);
                }
                m.Items.Add(combSub);
                Sep(m);
            }

            // ── Organizacija ───────────────────────────────────────────────
            MI(m, "Pomjeri gore\tAlt+strelica gore",  () => { _vm!.SelectedTrack = track; _vm.MoveTrackUpCommand.Execute(null); });
            MI(m, "Pomjeri dole\tAlt+strelica dole",  () => { _vm!.SelectedTrack = track; _vm.MoveTrackDownCommand.Execute(null); });
            MI(m, "Dupliraj traku\tCtrl+D",           () => { _vm!.SelectedTrack = track; _vm.DuplicateTrackCommand.Execute(null); });
            MI(m, "Preimenuj traku...", () =>
            {
                var d = new SetValueDialog("Preimenuj", "Novi naziv:", track.Name, "");
                d.Owner = Window.GetWindow(this);
                if (d.ShowDialog() == true && !string.IsNullOrWhiteSpace(d.ResultValue))
                { track.Name = d.ResultValue.Trim(); RefreshActive(); _vm?.Announce($"Traka preimenovana u {track.Name}."); }
            });
            Sep(m);
            MI(m, "Obriši traku", () => { _vm!.SelectedTrack = track; _vm.RemoveTrackCommand.Execute(null); });

            return m;
        }

        // Stavka u Efekti podmeniju — čist tekst, klik otvara dijalog
        private static void AddFxMI(MenuItem parent, string name, bool isOn, AudioTrack track, Action openDialog)
        {
            var mi = new MenuItem { Header = (isOn ? "✓ " : "    ") + name };
            mi.SetValue(AutomationProperties.NameProperty,
                $"{name}, {(isOn ? "uključen" : "isključen")}. Pritisni Enter za podešavanje.");
            mi.Click += (_, __) => openDialog();
            parent.Items.Add(mi);
        }

        // Otvori dijalog za efekat — JAWS čita slajdere normalno jer su u Window-u
        private void OpenEffectDialog(string title, AudioTrack track, EffectType type)
        {
            var dlg = new EffectDialog(title, track.Effects, type) { Owner = Window.GetWindow(this) };
            // NIJE modalan — korisnik može svirati dok podešava (live preview)
            dlg.ShowDialog();
            RefreshActive();
        }

        // ════════════════════════════════════════════════════════════════════
        // MENI KLIPA
        // ════════════════════════════════════════════════════════════════════
        private ContextMenu BuildClipMenu(AudioClip clip, AudioTrack track)
        {
            var m = new ContextMenu();
            double ph = _vm?.PlayheadPosition ?? 0;

            MIH(m, $"  {clip.Name}");
            MIH(m, $"  Početak: {FormatSec(clip.StartTime)}  Trajanje: {FormatSec(clip.Duration)}  Kraj: {FormatSec(clip.StartTime + clip.Duration)}");
            Sep(m);

            MI(m, "Postavi poziciju... (F2)",
                () => OpenSetPos(clip));
            MI(m, $"Postavi na playhead  ({FormatSec(ph)})",
                () => { clip.StartTime = Math.Max(0, ph); _vm!.Announce($"Klip na {FormatSec(clip.StartTime)}."); UpdateStatus(); BuildFileList(track); });
            MI(m, "Postavi na poziciju...",
                () => { double p = AskPos("Postavi klip", clip.StartTime); if (p >= 0) { clip.StartTime = Math.Max(0, p); _vm!.Announce($"Klip na {FormatSec(clip.StartTime)}."); UpdateStatus(); BuildFileList(track); } });
            Sep(m);
            MI(m, $"Pomjeri naprijed 1s  →  {FormatSec(clip.StartTime + 1)}",    () => MoveClip(clip,  1.0));
            MI(m, $"Pomjeri nazad 1s     →  {FormatSec(Math.Max(0, clip.StartTime-1))}", () => MoveClip(clip, -1.0));
            MI(m, $"Pomjeri naprijed 0.1s  →  {FormatSec(clip.StartTime + 0.1)}", () => MoveClip(clip,  0.1));
            MI(m, $"Pomjeri nazad 0.1s   →  {FormatSec(Math.Max(0, clip.StartTime-0.1))}", () => MoveClip(clip, -0.1));

            var others = _vm!.Project.Tracks.Where(t => t != track).ToList();
            if (others.Any())
            {
                Sep(m);
                var sub = new MenuItem { Header = $"Uvezi na drugu traku na {FormatSec(clip.StartTime)}..." };
                sub.SetValue(AutomationProperties.NameProperty, $"Podmeni uvezi na drugu traku na poziciju {FormatSec(clip.StartTime)}");
                foreach (var o in others)
                {
                    var oo = o;
                    var si = new MenuItem { Header = $"{oo.Name}  ({oo.Type})" };
                    si.SetValue(AutomationProperties.NameProperty, $"Uvezi na traku {oo.Name}");
                    si.Click += (_, __) => ImportAt(oo, clip.StartTime);
                    sub.Items.Add(si);
                }
                m.Items.Add(sub);
            }

            Sep(m);
            MI(m, "Obriši klip\tDelete", () => DelClip(clip));
            return m;
        }

        // ════════════════════════════════════════════════════════════════════
        // DEMUCS DIALOG
        // ════════════════════════════════════════════════════════════════════
        private async void OpenDemucsDialog(AudioTrack track)
        {
            if (_vm == null) return;

            if (!track.Clips.Any())
            {
                MessageBox.Show("Traka nema audio fajlova. Uvezite audio prije razdvajanja.",
                    "Nema fajlova", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var svc = new DemucsService();
            bool available = await svc.CheckAvailableAsync();

            if (!available)
            {
                var result = MessageBox.Show(
                    $"Demucs nije instaliran.\n\n{svc.StatusMessage}\n\n" +
                    "Demucs je besplatan Meta AI alat za razdvajanje vokala i instrumenta.\n\n" +
                    "Instalacija (jednom):\n" +
                    "  1. Instalirajte Python 3.8+ sa python.org\n" +
                    "  2. Otvorite Command Prompt\n" +
                    "  3. Pokrenite: pip install demucs\n\n" +
                    "Kliknite OK za upute na web-u.",
                    "Demucs nije instaliran",
                    MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (result == MessageBoxResult.OK)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = "https://github.com/facebookresearch/demucs#requirements", UseShellExecute = true });
                return;
            }

            // Odabir fajla (prvi klip na traci)
            var clip = track.Clips.First();

            // Odabir output foldera
            var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
            {
                Description = "Odaberite folder gdje će se sačuvati razdvojeni stemovi",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog() != true) return;

            // Odabir moda (2 ili 4 stema)
            var modeResult = MessageBox.Show(
                "Odaberite mod razdvajanja:\n\n" +
                "DA  — 2 stema: Vokal + Instrumental (brže)\n" +
                "NE  — 4 stema: Vokal + Bubnjevi + Bas + Ostalo (sporije, detaljnije)",
                "Mod razdvajanja",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            var mode = modeResult == MessageBoxResult.Yes
                ? DemucsService.StemMode.TwoStems
                : DemucsService.StemMode.FourStems;

            _vm.Announce("Pokrenuto Demucs razdvajanje. Ovo može trajati nekoliko minuta...");
            _vm.AiProgress = 0;

            var progress = new Progress<(int Percent, string Status)>(p =>
            {
                if (p.Percent >= 0) _vm.AiProgress = p.Percent;
                _vm.StatusMessage = $"Demucs: {p.Status}";
            });

            try
            {
                var stemResult = await svc.SeparateAsync(clip.FilePath, dlg.SelectedPath, mode, "htdemucs", progress);

                _vm.AiProgress = 100;
                _vm.Announce("Demucs završen! Uvozim stemove kao nove trake...");

                // Uvezi stemove kao nove trake
                int added = 0;
                foreach (var stemPath in stemResult.AllStems)
                {
                    if (!System.IO.File.Exists(stemPath)) continue;
                    string stemName = System.IO.Path.GetFileNameWithoutExtension(stemPath);

                    var newTrack = _vm.AddTrackInternal(stemName);
                    newTrack.Type = stemName.Contains("vocal") ? TrackType.Vocal : TrackType.Instrumental;

                    double dur = 0;
                    try { using var r = new AudioFileReader(stemPath); dur = r.TotalTime.TotalSeconds; } catch { }

                    newTrack.Clips.Add(new AudioClip
                    {
                        Name = System.IO.Path.GetFileName(stemPath),
                        FilePath = stemPath,
                        StartTime = clip.StartTime,
                        Duration = dur,
                        WaveformData = AudioEngine.LoadWaveformData(stemPath)
                    });
                    added++;
                }

                RebuildTrackList();
                MessageBox.Show(
                    $"Demucs završen!\n\n" +
                    $"Uvezeno {added} stem traka iz:\n{stemResult.StemDirectory}\n\n" +
                    $"Svaki stem je dodat kao nova traka u projektu.",
                    "Razdvajanje završeno",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _vm.AiProgress = 0;
                MessageBox.Show($"Demucs greška:\n{ex.Message}", "Greška", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // STATUS
        // ════════════════════════════════════════════════════════════════════
        public void UpdateStatus()
        {
            if (_vm == null) return;
            int clips = _vm.Project.Tracks.Sum(t => t.Clips.Count);
            double dur = _vm.Project.Tracks.SelectMany(t => t.Clips).Select(c => c.StartTime + c.Duration).DefaultIfEmpty(0).Max();
            TxtProjectStatus.Text = $"{_vm.Project.Name}  |  Trake: {_vm.Project.Tracks.Count}  |  Fajlova: {clips}  |  Ukupno: {FormatSec(dur)}";
            TxtPlayhead.Text      = $"Playhead: {_vm.TimeDisplay}  ({_vm.PlayheadPosition:F3}s)";
            if (_vm.SelectedClip != null)
            { var c = _vm.SelectedClip; TxtSelection.Text = $"Klip: {c.Name}  |  Poč: {c.StartTime:F3}s  |  Tra: {c.Duration:F3}s  |  Kraj: {(c.StartTime+c.Duration):F3}s"; }
            else if (_vm.SelectedTrack != null)
                TxtSelection.Text = $"Traka: {_vm.SelectedTrack.Name}  |  {_vm.SelectedTrack.Clips.Count} fajlova  |  Vol: {_vm.SelectedTrack.Volume:P0}";
            else
                TxtSelection.Text = "Odaberite traku sa lijeve liste.";
        }

        public void FocusFirstTrack() { TrackListBox.Focus(); if (TrackListBox.Items.Count > 0) TrackListBox.SelectedIndex = 0; }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════
        private void RefreshActive()
        {
            if (_activeTrack == null) return;
            var item = TrackListBox.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == _activeTrack);
            if (item != null) RefreshItem(item, _activeTrack);
        }

        private void ImportAt(AudioTrack track, double position)
        {
            if (_vm == null) return;
            var dlg = new OpenFileDialog { Title = $"Uvezi audio na {FormatSec(position)}", Filter = "Audio|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aiff|Svi|*.*" };
            if (dlg.ShowDialog() != true) return;
            double dur = 5;
            try { using var r = new AudioFileReader(dlg.FileName); dur = r.TotalTime.TotalSeconds; } catch { }
            var clip = new AudioClip { Name = System.IO.Path.GetFileName(dlg.FileName), FilePath = dlg.FileName, StartTime = Math.Max(0, position), Duration = dur, WaveformData = AudioEngine.LoadWaveformData(dlg.FileName) };
            _vm.SelectedTrack = track; track.Clips.Add(clip); _vm.SelectedClip = clip;
            _vm.Announce($"Uvezen: {clip.Name}. Početak {FormatSec(position)}, kraj {FormatSec(position+dur)}.");
            BuildFileList(track);
        }

        private void CombineDialog(AudioTrack t1, AudioTrack t2)
        {
            if (_vm == null) return;
            var dlg = new SetValueDialog($"Kombinuj: {t1.Name} + {t2.Name}", $"Offset za \"{t2.Name}\" u sekundama:", "0", "s");
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() != true) return;
            if (!double.TryParse(dlg.ResultValue.Replace(',','.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double offset)) { MessageBox.Show("Neispravan offset."); return; }
            var saveDlg = new Microsoft.Win32.SaveFileDialog { Filter = "WAV|*.wav", FileName = $"{t1.Name}_plus_{t2.Name}" };
            if (saveDlg.ShowDialog() != true) return;
            _vm.Announce("Kombinujem...");
            var orig = t2.Clips.Select(c => c.StartTime).ToList();
            for (int i = 0; i < t2.Clips.Count; i++) t2.Clips[i].StartTime += offset;
            Task.Run(() =>
            {
                try
                {
                    var tmp = new AudioProject { Name="tmp", SampleRate=_vm.Project.SampleRate, BitDepth=_vm.Project.BitDepth };
                    tmp.Tracks.Add(t1); tmp.Tracks.Add(t2);
                    AudioEngine.ExportMixdown(tmp, saveDlg.FileName, ExportFormat.WAV, 192, pct => Application.Current?.Dispatcher.Invoke(() => _vm.AiProgress = pct));
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        for (int i=0;i<t2.Clips.Count;i++) t2.Clips[i].StartTime=orig[i];
                        _vm.Announce($"Sačuvano.");
                        if (MessageBox.Show($"Uvesti kao novu traku?\n{saveDlg.FileName}", "Gotovo", MessageBoxButton.YesNo)==MessageBoxResult.Yes)
                        {
                            var nt = _vm.AddTrackInternal($"{t1.Name}+{t2.Name}");
                            double dur=0; try { using var r=new AudioFileReader(saveDlg.FileName); dur=r.TotalTime.TotalSeconds; } catch {}
                            nt.Clips.Add(new AudioClip { Name=System.IO.Path.GetFileName(saveDlg.FileName), FilePath=saveDlg.FileName, StartTime=0, Duration=dur, WaveformData=AudioEngine.LoadWaveformData(saveDlg.FileName) });
                            RebuildTrackList();
                        }
                    });
                }
                catch (Exception ex) { Application.Current?.Dispatcher.Invoke(() => { for(int i=0;i<t2.Clips.Count;i++) t2.Clips[i].StartTime=orig[i]; MessageBox.Show($"Greška: {ex.Message}"); }); }
            });
        }

        private double AskPos(string title, double cur)
        {
            var d = new SetValueDialog(title, "Sekunde (15.01) ili MM:SS (1:30):", cur.ToString("F2"), "s");
            d.Owner = Window.GetWindow(this);
            return d.ShowDialog() == true ? ParsePos(d.ResultValue) : -1;
        }

        public static string FormatSec(double sec)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds/10:D2}";
        }

        public static double ParsePos(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            text = text.Trim().Replace(',','.');
            if (text.Contains(':'))
            {
                var p = text.Split(':');
                if (p.Length==2 && int.TryParse(p[0].Trim(),out int mn) && double.TryParse(p[1].Trim(),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out double sc)) return mn*60.0+sc;
                return -1;
            }
            return double.TryParse(text,System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out double s) ? s : -1;
        }

        private static void MIH(ContextMenu m, string t) =>
            m.Items.Add(new MenuItem { Header=t, IsEnabled=false, FontWeight=FontWeights.Bold, Foreground=new SolidColorBrush(Color.FromRgb(160,160,200)) });
        private static void Sep(ContextMenu m) => m.Items.Add(new Separator());
        private static void MI(ContextMenu m, string h, Action a)
        {
            var item = new MenuItem { Header = h };
            item.SetValue(AutomationProperties.NameProperty, h.Split('\t')[0].Trim());
            item.Click += (_, __) => a();
            m.Items.Add(item);
        }
    }

    public class ClipRow
    {
        public AudioClip Clip { get; }
        public ClipRow(AudioClip c) { Clip = c; }
        public string Name         => Clip.Name;
        public string StartTimeStr => $"{Clip.StartTime:F3}";
        public string StartTimeFmt => AccessibleTrackList.FormatSec(Clip.StartTime);
        public string DurationStr  => $"{Clip.Duration:F3}";
        public string DurationFmt  => AccessibleTrackList.FormatSec(Clip.Duration);
        public string EndTimeStr   => $"{(Clip.StartTime+Clip.Duration):F3}";
        public string EndTimeFmt   => AccessibleTrackList.FormatSec(Clip.StartTime+Clip.Duration);
    }
}
