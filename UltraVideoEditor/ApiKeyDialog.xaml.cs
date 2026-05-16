using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class ApiKeyDialog : Window
    {
        // Language helper
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public string ApiKey => txtApiKey.Text.Trim();

        public ApiKeyDialog(string service, string message)
        {
            InitializeComponent();
            Title = LF("akd_service_title", service);

            txtApiKey.ToolTip = message;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtApiKey.Text))
            {
                WpfMessageBox.Show(L("akd_enter_valid_key"), L("warning"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void HelpLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://pixabay.com/api/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(LF("akd_link_error", ex.Message), L("error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}