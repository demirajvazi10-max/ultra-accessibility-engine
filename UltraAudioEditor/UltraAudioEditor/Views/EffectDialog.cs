using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraAudioEditor.Models;

namespace UltraAudioEditor.Views
{
    // Definicija jednog parametra efekta
    public record EffectParam(
        string   Label,
        double   Min,
        double   Max,
        double   Step,
        string   Unit,
        Func<double>   Get,
        Action<double> Set);

    public enum EffectType
    {
        Equalizer, Reverb, Delay, Compressor, NoiseGate, BassBoost, PitchShift, Chorus
    }

    /// <summary>
    /// Pristupačan dijalog za podešavanje jednog efekta.
    /// Svaki parametar = Slider + TextBox. JAWS čita normalno.
    /// Efekat se mijenja LIVE dok muzika svira (DSP lanac čita live vrijednosti).
    /// </summary>
    public class EffectDialog : Window
    {
        private readonly List<(Slider Slider, TextBox Box, EffectParam Param)> _rows = new();

        public EffectDialog(string title, TrackEffects fx, EffectType effectType)
        {
            Title  = title;
            Width  = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode   = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background   = (Brush)Application.Current.Resources["BrBgDark"];

            var (isOn, setOn, paramList) = BuildParams(fx, effectType);

            // Dinamična visina
            Height = 130 + paramList.Length * 52;

            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            for (int i = 0; i < 7; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions[5] = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };

            // ── Naslov ───────────────────────────────────────────────────
            var hdr = new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["BrText"],
                Margin = new Thickness(0, 0, 0, 12)
            };
            hdr.SetValue(AutomationProperties.HeadingLevelProperty, AutomationHeadingLevel.Level1);
            Grid.SetRow(hdr, 0); root.Children.Add(hdr);

            // ── Enable checkbox ───────────────────────────────────────────
            var cb = new CheckBox
            {
                Content   = "Efekat uključen",
                IsChecked = isOn(),
                FontSize  = 13,
                Foreground = (Brush)Application.Current.Resources["BrText"],
                Margin = new Thickness(0, 0, 0, 4)
            };
            UpdateCbAria(cb, title, isOn());
            cb.Checked   += (_, __) => { setOn(true);  UpdateCbAria(cb, title, true); };
            cb.Unchecked += (_, __) => { setOn(false); UpdateCbAria(cb, title, false); };
            Grid.SetRow(cb, 2); root.Children.Add(cb);

            // ── Spacer ────────────────────────────────────────────────────
            var spacer = new Border { Height = 8 };
            Grid.SetRow(spacer, 3); root.Children.Add(spacer);

            // ── Parametri ─────────────────────────────────────────────────
            var paramGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            paramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            paramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            paramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

            for (int i = 0; i < paramList.Length; i++)
            {
                paramGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
                var param = paramList[i];

                var lbl = new TextBlock
                {
                    Text = param.Label, FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["BrTextSec"],
                    Margin = new Thickness(0, 0, 10, 0)
                };

                var slider = new Slider
                {
                    Minimum = param.Min, Maximum = param.Max,
                    Value   = param.Get(),
                    SmallChange = param.Step,
                    LargeChange = param.Step * 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsTabStop = true
                };
                UpdateSliderAria(slider, param);

                var box = new TextBox
                {
                    Text = Fmt(param.Get(), param.Unit),
                    Width = 68, Height = 28, FontSize = 12,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right
                };
                box.SetValue(AutomationProperties.NameProperty,
                    $"{param.Label}, unesi broj od {Fmt(param.Min, param.Unit)} do {Fmt(param.Max, param.Unit)}");

                // Slider → model + box
                bool busy = false;
                slider.ValueChanged += (_, ev) =>
                {
                    if (busy) return; busy = true;
                    param.Set(ev.NewValue);
                    box.Text = Fmt(ev.NewValue, param.Unit);
                    UpdateSliderAria(slider, param);
                    busy = false;
                };

                // Box → model + slider  (na Enter/Tab/LostFocus)
                void ApplyBox()
                {
                    if (busy) return; busy = true;
                    if (double.TryParse(box.Text.Replace(',', '.'),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                    {
                        v = Math.Clamp(v, param.Min, param.Max);
                        slider.Value = v;
                        param.Set(v);
                        box.Text = Fmt(v, param.Unit);
                    }
                    else box.Text = Fmt(param.Get(), param.Unit);
                    busy = false;
                }
                box.KeyDown   += (_, ke) => { if (ke.Key is Key.Return or Key.Tab) ApplyBox(); };
                box.LostFocus += (_, __) => ApplyBox();

                Grid.SetRow(lbl,    i); Grid.SetColumn(lbl,    0); paramGrid.Children.Add(lbl);
                Grid.SetRow(slider, i); Grid.SetColumn(slider, 1); paramGrid.Children.Add(slider);
                Grid.SetRow(box,    i); Grid.SetColumn(box,    2); paramGrid.Children.Add(box);

                _rows.Add((slider, box, param));
            }
            Grid.SetRow(paramGrid, 4); root.Children.Add(paramGrid);

            // ── Dugmad ────────────────────────────────────────────────────
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnReset = new Button
            {
                Content = "Resetuj na default",
                Style   = (Style)Application.Current.Resources["StdButton"],
                Height  = 30, Padding = new Thickness(10, 0, 10, 0),
                Margin  = new Thickness(0, 0, 8, 0)
            };
            btnReset.SetValue(AutomationProperties.NameProperty,
                "Resetuj sve parametre na podrazumijevane vrijednosti");
            btnReset.Click += (_, __) => ResetDefaults(fx, effectType);

            var btnClose = new Button
            {
                Content = "Zatvori",
                Style   = (Style)Application.Current.Resources["AIButton"],
                Height  = 30, Padding = new Thickness(10, 0, 10, 0),
                IsDefault = true
            };
            btnClose.SetValue(AutomationProperties.NameProperty,
                "Zatvori dijalog. Efekat ostaje primijenjen.");
            btnClose.Click += (_, __) => Close();

            btnPanel.Children.Add(btnReset);
            btnPanel.Children.Add(btnClose);
            Grid.SetRow(btnPanel, 6); root.Children.Add(btnPanel);

            Content = root;
            Loaded  += (_, __) => cb.Focus();
            KeyDown += (_, ke) => { if (ke.Key == Key.Escape) Close(); };
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private static void UpdateCbAria(CheckBox cb, string title, bool on) =>
            cb.SetValue(AutomationProperties.NameProperty,
                $"{title} {(on ? "uključen" : "isključen")}. Pritisnite Space da promijenite.");

        private static void UpdateSliderAria(Slider s, EffectParam p) =>
            s.SetValue(AutomationProperties.NameProperty,
                $"{p.Label}, trenutno {Fmt(p.Get(), p.Unit)}, " +
                $"od {Fmt(p.Min, p.Unit)} do {Fmt(p.Max, p.Unit)}. Strelice za podešavanje.");

        public static string Fmt(double v, string unit) => unit switch
        {
            "dB" => $"{v:F1} dB",
            "s"  => $"{v:F2} s",
            "st" => $"{v:F1} st",
            "%"  => $"{v * 100:F0}%",
            _    => $"{v:F2}"
        };

        // ── Reset na defaultne vrijednosti ────────────────────────────────
        private void ResetDefaults(TrackEffects fx, EffectType t)
        {
            switch (t)
            {
                case EffectType.Equalizer:  fx.EqLow = 0; fx.EqMid = 0; fx.EqHigh = 0; break;
                case EffectType.Reverb:     fx.ReverbMix = 0.3f; fx.ReverbRoom = 0.5f; break;
                case EffectType.Delay:      fx.DelayTime = 0.25f; fx.DelayFeedback = 0.3f; break;
                case EffectType.Compressor: fx.CompThreshold = -20; fx.CompRatio = 4; fx.CompAttack = 10; fx.CompRelease = 100; break;
                case EffectType.NoiseGate:  fx.GateThreshold = -40; break;
                case EffectType.BassBoost:  fx.BassGain = 6; break;
                case EffectType.PitchShift: fx.PitchSemitones = 0; break;
                case EffectType.Chorus:     fx.ChorusDepth = 0.5f; break;
            }
            // Ažuriraj UI da reflektuje nove vrijednosti iz modela
            foreach (var (slider, box, param) in _rows)
            {
                slider.Value = param.Get();
                box.Text     = Fmt(param.Get(), param.Unit);
                UpdateSliderAria(slider, param);
            }
        }

        // ── Definicije parametara po tipu efekta ──────────────────────────
        private static (Func<bool> IsOn, Action<bool> SetOn, EffectParam[] Params)
            BuildParams(TrackEffects fx, EffectType t) => t switch
        {
            EffectType.Equalizer => (
                () => fx.EqEnabled,
                v  => fx.EqEnabled = v,
                new EffectParam[]
                {
                    new("Bas (200 Hz)",    -12, 12, 0.5,  "dB", () => fx.EqLow,  v => fx.EqLow  = (float)v),
                    new("Srednji (1 kHz)", -12, 12, 0.5,  "dB", () => fx.EqMid,  v => fx.EqMid  = (float)v),
                    new("Visoki (8 kHz)",  -12, 12, 0.5,  "dB", () => fx.EqHigh, v => fx.EqHigh = (float)v),
                }),

            EffectType.Reverb => (
                () => fx.ReverbEnabled,
                v  => fx.ReverbEnabled = v,
                new EffectParam[]
                {
                    new("Mix (suho/mokro)", 0, 1, 0.01, "%", () => fx.ReverbMix,  v => fx.ReverbMix  = (float)v),
                    new("Veličina sobe",    0, 1, 0.01, "%", () => fx.ReverbRoom, v => fx.ReverbRoom = (float)v),
                }),

            EffectType.Delay => (
                () => fx.DelayEnabled,
                v  => fx.DelayEnabled = v,
                new EffectParam[]
                {
                    new("Kašnjenje",  0, 1,   0.01, "s", () => fx.DelayTime,     v => fx.DelayTime     = (float)v),
                    new("Povratnost", 0, 0.9, 0.01, "%", () => fx.DelayFeedback, v => fx.DelayFeedback = (float)v),
                }),

            EffectType.Compressor => (
                () => fx.CompressorEnabled,
                v  => fx.CompressorEnabled = v,
                new EffectParam[]
                {
                    new("Prag (Threshold)", -60, 0,   1,   "dB", () => fx.CompThreshold, v => fx.CompThreshold = (float)v),
                    new("Omjer (Ratio)",      1, 20,  0.5, "",   () => fx.CompRatio,     v => fx.CompRatio     = (float)v),
                    new("Napad (Attack ms)",  1, 200, 1,   "",   () => fx.CompAttack,    v => fx.CompAttack    = (float)v),
                    new("Otpust (Release ms)",10, 500, 10, "",   () => fx.CompRelease,   v => fx.CompRelease   = (float)v),
                }),

            EffectType.NoiseGate => (
                () => fx.NoiseGateEnabled,
                v  => fx.NoiseGateEnabled = v,
                new EffectParam[]
                {
                    new("Prag (Threshold)", -80, 0, 1, "dB", () => fx.GateThreshold, v => fx.GateThreshold = (float)v),
                }),

            EffectType.BassBoost => (
                () => fx.BassBostEnabled,
                v  => fx.BassBostEnabled = v,
                new EffectParam[]
                {
                    new("Pojačanje", 0, 24, 0.5, "dB", () => fx.BassGain, v => fx.BassGain = (float)v),
                }),

            EffectType.PitchShift => (
                () => fx.PitchEnabled,
                v  => fx.PitchEnabled = v,
                new EffectParam[]
                {
                    new("Polutonovi", -12, 12, 0.5, "st", () => fx.PitchSemitones, v => fx.PitchSemitones = (float)v),
                }),

            EffectType.Chorus => (
                () => fx.ChorusEnabled,
                v  => fx.ChorusEnabled = v,
                new EffectParam[]
                {
                    new("Dubina", 0, 1, 0.01, "%", () => fx.ChorusDepth, v => fx.ChorusDepth = (float)v),
                }),

            _ => throw new ArgumentOutOfRangeException(nameof(t), t, null)
        };
    }
}
