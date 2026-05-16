using System.Windows;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class SubtitleDialog : Window
    {
        // Language helper
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public string SubtitleText => txtText.Text;

        public SubtitleDialog(TimelineItem clip)
        {
            InitializeComponent();
            this.Title = $"Dodaj titl na: {clip.Name}";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtText.Text))
            {
                WpfMessageBox.Show(L("sd_enter_text"), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
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