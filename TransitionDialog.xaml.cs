using System.Windows;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class TransitionDialog : Window
    {
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
                WpfMessageBox.Show("Unesi ispravan broj klipa", "Greška", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}