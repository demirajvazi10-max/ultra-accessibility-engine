using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class ApiKeyDialog : Window
    {
        public string ApiKey => txtApiKey.Text.Trim();

        public ApiKeyDialog(string service, string message)
        {
            InitializeComponent();
            Title = $"🔑 Unos {service} API ključa";

            txtApiKey.ToolTip = message;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtApiKey.Text))
            {
                WpfMessageBox.Show("Molimo unesite važeći API ključ.", "Upozorenje",
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
                WpfMessageBox.Show($"Ne mogu otvoriti link: {ex.Message}", "Greška",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}