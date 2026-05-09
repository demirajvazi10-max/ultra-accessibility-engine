using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class DurationDialog : Window
    {
        // Language helper
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public double Duration { get; private set; }

        public DurationDialog(double currentDuration = 5.0)
        {
            InitializeComponent();
            Duration = currentDuration;
            txtDuration.Text = currentDuration.ToString(CultureInfo.InvariantCulture);

            txtDuration.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Ok_Click(null, null);
                }
                else if (e.Key == Key.Escape)
                {
                    Cancel_Click(null, null);
                }
            };

            Loaded += (s, e) => txtDuration.Focus();

            AutomationProperties.SetName(this, "Dijalog za trajanje");
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(txtDuration.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            {
                if (result > 0 && result <= 3600)
                {
                    Duration = result;
                    DialogResult = true;
                    Close();
                }
                else
                {
                    WpfMessageBox.Show(L("dd_duration_range"), L("error_title"),
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtDuration.Focus();
                }
            }
            else
            {
                WpfMessageBox.Show(L("dd_invalid_number"), L("error_title"),
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                txtDuration.Focus();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}