using System.Windows;

namespace UltraVideoEditor
{
    public partial class ResolutionDialog : Window
    {
        public string SelectedResolution { get; private set; } = "1920x1080";

        public ResolutionDialog()
        {
            InitializeComponent();
            cmbResolution.SelectedIndex = 1; // Full HD default
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var item = cmbResolution.SelectedItem as System.Windows.Controls.ComboBoxItem;
            if (item != null && item.Tag != null)
            {
                SelectedResolution = item.Tag.ToString();
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