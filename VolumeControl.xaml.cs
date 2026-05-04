using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace UltraVideoEditor
{
    public partial class VolumeControl : Window
    {
        public double Volume { get; private set; }

        public VolumeControl(double currentVolume = 100)
        {
            InitializeComponent();
            Volume = currentVolume;
            sldVolume.Value = currentVolume;
            txtVolumeValue.Text = $"{currentVolume:F0}%";
            AutomationProperties.SetName(this, "Podešavanje jačine zvuka");
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Volume = sldVolume.Value;
            txtVolumeValue.Text = $"{Volume:F0}%";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
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