using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace UltraVideoEditor
{
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
            AutomationProperties.SetName(this, "Prozor sa pomoći");

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