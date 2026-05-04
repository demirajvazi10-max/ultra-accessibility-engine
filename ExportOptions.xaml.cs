using System.Windows;
using System.Windows.Controls;

namespace UltraVideoEditor
{
    public partial class ExportOptions : Window
    {
        public ExportSettingsData Settings { get; private set; }

        public ExportOptions(ExportSettingsData defaultSettings = null)
        {
            InitializeComponent();
            Settings = defaultSettings ?? new ExportSettingsData();
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (Settings.ExportAudioOnly) chkAudioOnly.IsChecked = true;

            switch (Settings.Format)
            {
                case "WebM": cmbFormat.SelectedIndex = 1; break;
                case "AVI": cmbFormat.SelectedIndex = 2; break;
                default: cmbFormat.SelectedIndex = 0; break;
            }

            switch (Settings.Quality)
            {
                case "Low": cmbQuality.SelectedIndex = 0; break;
                case "High": cmbQuality.SelectedIndex = 2; break;
                default: cmbQuality.SelectedIndex = 1; break;
            }
        }

        private void SaveSettings()
        {
            Settings.Format = ((ComboBoxItem)cmbFormat.SelectedItem).Content.ToString();
            string quality = ((ComboBoxItem)cmbQuality.SelectedItem).Content.ToString();
            Settings.Quality = quality == "Nizak" ? "Low" : (quality == "Visok" ? "High" : "Medium");
            Settings.ExportAudioOnly = chkAudioOnly.IsChecked == true;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
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