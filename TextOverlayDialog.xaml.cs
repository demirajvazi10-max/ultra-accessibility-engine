using System.Windows;
using System.Windows.Automation;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class TextOverlayDialog : Window
    {
        public string Text => txtText.Text;
        public string Font => (cmbFont.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString() ?? "Arial";
        public int SelectedFontSize => int.TryParse(cmbFontSize.Text, out int size) ? size : 48;
        public string TextColor
        {
            get
            {
                var selected = cmbTextColor.SelectedItem as System.Windows.Controls.ComboBoxItem;
                return selected?.Tag?.ToString() ?? "#FFFFFF";
            }
        }
        public string Position => (cmbPosition.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString() ?? "Centar";

        public TextOverlayDialog(string title = "Dodaj tekst na sliku")
        {
            InitializeComponent();
            this.Title = title;
            AutomationProperties.SetName(this, title);
            txtText.Focus();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtText.Text))
            {
                WpfMessageBox.Show("Unesite tekst", "Greška", MessageBoxButton.OK, MessageBoxImage.Warning);
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