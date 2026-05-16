using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace UltraVideoEditor
{
    public partial class HelpWindow : Window
    {
        // Language helper
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public HelpWindow()
        {
            InitializeComponent();
            AutomationProperties.SetName(this, L("helpwnd_acc"));

            this.Loaded += (s, e) => { this.Focus(); };
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}