using System.Windows;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class NumericDialog : Window
    {
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
                WpfMessageBox.Show($"Unesite broj između 1 i {_maxValue}", "Greška", MessageBoxButton.OK, MessageBoxImage.Warning);
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