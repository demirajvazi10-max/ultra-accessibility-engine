using System.Windows;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class SubtitleDialog : Window
    {
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
                WpfMessageBox.Show("Unesi tekst titla", "Greška", MessageBoxButton.OK, MessageBoxImage.Warning);
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