using System.Windows;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class TransitionDialog : Window
    {
        // Language helper
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public int ClipIndex { get; private set; }

        public TransitionDialog(int totalClips)
        {
            InitializeComponent();
            txtMaxClips.Text = totalClips.ToString();
            txtClipNumber.Text = "1";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtClipNumber.Text, out int clipNum) && clipNum >= 1)
            {
                ClipIndex = clipNum;
                DialogResult = true;
                Close();
            }
            else
            {
                WpfMessageBox.Show(L("td_invalid_clip"), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}