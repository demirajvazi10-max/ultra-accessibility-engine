using System;
using System.Windows;
using System.Windows.Controls;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class SkiaAnimationDialog : Window
    {
        public string AnimationText { get; private set; } = "";
        public string AnimationStyle { get; private set; } = "FadeIn";
        public string TextColor { get; private set; } = "#FFFFFF";
        public string BgColor { get; private set; } = "#000000";
        public double DurationSeconds { get; private set; } = 5.0;

        public SkiaAnimationDialog()
        {
            InitializeComponent();
            cmbStyle.SelectedIndex = 0;

            cmbStyle.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.F1)
                    ReadStyleDescription();
            };

            txtText.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Return)
                    Create_Click(null, null);
            };
        }

        private void ReadStyleDescription()
        {
            var item = cmbStyle.SelectedItem as ComboBoxItem;
            if (item != null)
                System.Windows.Automation.AutomationProperties.SetName(
                    txtInfo, item.Content.ToString());
            txtInfo.Focus();
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtText.Text))
            {
                WpfMessageBox.Show("Unesi tekst animacije.", "Greska",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                txtText.Focus();
                return;
            }

            AnimationText = txtText.Text.Trim();

            var styleItem = cmbStyle.SelectedItem as ComboBoxItem;
            AnimationStyle = styleItem?.Tag?.ToString() ?? "FadeIn";

            // Čitanje boje teksta iz combo box-a
            var textColorItem = cmbTextColor.SelectedItem as ComboBoxItem;
            TextColor = textColorItem?.Tag?.ToString() ?? "#FFFFFF";

            // Čitanje boje pozadine iz combo box-a
            var bgColorItem = cmbBgColor.SelectedItem as ComboBoxItem;
            BgColor = bgColorItem?.Tag?.ToString() ?? "#000000";

            if (!double.TryParse(txtDuration.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double dur) || dur <= 0)
                dur = 5.0;
            DurationSeconds = dur;

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}