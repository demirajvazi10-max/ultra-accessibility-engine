using System.Windows;
using System.Windows.Input;

namespace UltraAudioEditor.Views
{
    public partial class SetClipPositionDialog : Window
    {
        public double ResultSeconds { get; private set; }

        public SetClipPositionDialog(double currentSeconds, string clipName)
        {
            InitializeComponent();
            TitleBlock.Text = $"Postavi poziciju: {clipName}";
            ResultSeconds = currentSeconds;
            SecondsBox.Text = currentSeconds.ToString("F2");
            TimeBox.Text = SecondsToMmSs(currentSeconds);

            // Sinhroniziraj polja
            SecondsBox.TextChanged += (s, e) =>
            {
                if (SecondsBox.IsFocused && double.TryParse(
                    SecondsBox.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double sec))
                {
                    TimeBox.Text = SecondsToMmSs(sec);
                }
            };

            TimeBox.TextChanged += (s, e) =>
            {
                if (TimeBox.IsFocused && TryParseMmSs(TimeBox.Text, out double sec))
                    SecondsBox.Text = sec.ToString("F2");
            };

            Loaded += (s, e) =>
            {
                SecondsBox.Focus();
                SecondsBox.SelectAll();
            };
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Confirm();
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void SecondsBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Confirm();
            if (e.Key == Key.Escape) DialogResult = false;
        }

        private void TimeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Confirm();
            if (e.Key == Key.Escape) DialogResult = false;
        }

        private void Confirm()
        {
            // Probaj parsirati sekunde
            string txt = SecondsBox.Text.Replace(',', '.');
            if (double.TryParse(txt,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double sec))
            {
                ResultSeconds = Math.Max(0, sec);
                DialogResult = true;
                return;
            }
            // Probaj MM:SS
            if (TryParseMmSs(TimeBox.Text, out double fromTime))
            {
                ResultSeconds = Math.Max(0, fromTime);
                DialogResult = true;
                return;
            }
            MessageBox.Show(
                "Unesite valjanu poziciju.\n\nPrimjeri:\n  15        (15 sekundi)\n  15.5      (15 i po sekundi)\n  1:30      (1 minuta i 30 sekundi)\n  0:15      (15 sekundi)",
                "Neispravan unos", MessageBoxButton.OK, MessageBoxImage.Warning);
            SecondsBox.Focus();
            SecondsBox.SelectAll();
        }

        private static string SecondsToMmSs(double seconds)
        {
            int m = (int)(seconds / 60);
            double s = seconds % 60;
            return $"{m}:{s:00.##}";
        }

        private static bool TryParseMmSs(string text, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0].Trim(), out int min) &&
                double.TryParse(parts[1].Trim().Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double sec))
            {
                seconds = min * 60.0 + sec;
                return true;
            }
            return false;
        }
    }
}
