using System.Windows;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class NumericDialog : Window
    {
        // Language helper
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public int SelectedNumber { get; private set; }
        private readonly int _maxValue;

        public NumericDialog(int maxValue)
        {
            InitializeComponent();
            _maxValue = maxValue;
            lblInstruction.Text = $"Pozicija (1 - {maxValue}):";
            txtNumber.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtNumber.Text, out int num) && num >= 1 && num <= _maxValue)
            {
                SelectedNumber = num;
                DialogResult = true;
                Close();
            }
            else
            {
                WpfMessageBox.Show(LF("nd_enter_number", _maxValue), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                txtNumber.Focus();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}