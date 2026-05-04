using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace UltraVideoEditor
{
    public partial class AutoArrangeDialog : Window
    {
        public double IntroDuration { get; private set; } = 5;
        public double OutroDuration { get; private set; } = 7;
        public string EffectMode { get; private set; } = "auto";
        public List<string> EffectSequence { get; private set; } = new List<string>();
        public bool AnimateIntro { get; private set; } = true;
        public bool AnimateOutro { get; private set; } = true;

        // Opcije za tekst na slici
        public bool TextOnImageEnabled { get; private set; } = false;
        public string OverlayText { get; private set; } = "";
        public bool UseCounter { get; private set; } = false;
        public string OverlayFont { get; private set; } = "Arial";
        public string OverlayColor { get; private set; } = "#FFFFFF";
        public int OverlayFontSize { get; private set; } = 48;
        public string OverlayPosition { get; private set; } = "Centar";

        // Logo
        public string LogoPath { get; private set; } = "";
        public bool ShowLogo { get; private set; } = false;
        public double LogoDuration { get; private set; } = 5;

        // Tekstovi
        public string IntroText { get; private set; } = "";
        public string OutroText { get; private set; } = "";
        public string ChannelName { get; private set; } = "";

        // Rezolucija
        public string SelectedResolution { get; private set; } = "1920x1080";

        // PROFESIONALNI EFEKTI
        public bool EnableCrossfade { get; private set; } = true;
        public bool EnableKenBurns { get; private set; } = true;
        public bool EnableTransitionSounds { get; private set; } = true;
        public bool EnableAmbientSounds { get; private set; } = true;

        // FREESOUND API KLJUČ
        public string FreesoundApiKey { get; private set; } = "";

        private readonly double _audioDuration;
        private readonly int _imageCount;
        private bool _isInitializing = true;

        public AutoArrangeDialog(double audioDuration, int imageCount)
        {
            InitializeComponent();

            _audioDuration = audioDuration;
            _imageCount = imageCount;

            string dur = TimeSpan.FromSeconds(_audioDuration).ToString(@"mm\:ss");
            txtAudioInfo.Text = $"Audio trajanje: {dur} ({_audioDuration:F1} sekundi) | Slika: {_imageCount}";

            EffectSequence = new List<string> { "ZoomIn", "SlideLeft", "FadeIn", "ZoomOut", "SlideRight" };

            cmbEffectMode.SelectedIndex = 0;
            cmbResolution.SelectedIndex = 1;

            _isInitializing = false;

            AutomationProperties.SetName(this, "Auto rasporedi slike");
        }

        private void btnBrowseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WpfOpenFileDialog { Filter = "Slike|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog() == true)
            {
                txtLogoPath.Text = dialog.FileName;
            }
        }

        private void btnGetKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://freesound.org/apiv2/apply/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Ne mogu otvoriti link: {ex.Message}", "Greška",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void EffectMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (cmbEffectMode.SelectedItem == null) return;

            var selected = cmbEffectMode.SelectedItem as ComboBoxItem;
            if (selected != null && selected.Content != null)
            {
                string mode = selected.Content.ToString();
                if (mode.Contains("Automatski"))
                {
                    EffectMode = "auto";
                    txtEffectSequence.IsEnabled = false;
                }
                else if (mode.Contains("Ručno"))
                {
                    EffectMode = "manual";
                    txtEffectSequence.IsEnabled = true;
                }
                else if (mode.Contains("Nasumično"))
                {
                    EffectMode = "random";
                    txtEffectSequence.IsEnabled = false;
                }
            }
        }

        private void ChkTextOnImage_Checked(object sender, RoutedEventArgs e)
        {
            bool isEnabled = chkTextOnImage.IsChecked == true;
            txtOverlayText.IsEnabled = isEnabled;
            chkUseCounter.IsEnabled = isEnabled;
            cmbOverlayFont.IsEnabled = isEnabled;
            cmbOverlayColor.IsEnabled = isEnabled;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // Intro i outro trajanje
            if (double.TryParse(txtIntroDuration.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double intro))
            {
                IntroDuration = intro;
            }

            if (double.TryParse(txtOutroDuration.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double outro))
            {
                OutroDuration = outro;
            }

            if (IntroDuration + OutroDuration >= _audioDuration)
            {
                WpfMessageBox.Show($"Najavni i odjavni tekst zajedno ({IntroDuration + OutroDuration}s) su duži od audio fajla ({_audioDuration:F1}s). Smanjite trajanje.",
                    "Greška", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Efekti
            if (EffectMode == "manual")
            {
                var rawList = txtEffectSequence.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                EffectSequence = rawList.Select(e => e.Trim()).ToList();
                if (EffectSequence.Count == 0)
                {
                    EffectSequence = new List<string> { "ZoomIn", "SlideLeft", "FadeIn" };
                }
            }

            // Tekst na slici
            TextOnImageEnabled = chkTextOnImage.IsChecked == true;
            OverlayText = txtOverlayText.Text;
            UseCounter = chkUseCounter.IsChecked == true;
            OverlayFont = (cmbOverlayFont.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Arial";
            OverlayColor = (cmbOverlayColor.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FFFFFF";

            // Logo
            LogoPath = txtLogoPath.Text;
            ShowLogo = chkShowLogo.IsChecked == true;
            LogoDuration = double.TryParse(txtLogoDuration.Text, out double logoDur) ? logoDur : 5;

            // Tekstovi
            IntroText = txtIntroText.Text;
            OutroText = txtOutroText.Text;
            ChannelName = txtChannelName.Text;

            // Rezolucija
            var resItem = cmbResolution.SelectedItem as ComboBoxItem;
            if (resItem != null && resItem.Tag != null)
            {
                SelectedResolution = resItem.Tag.ToString();
            }

            AnimateIntro = chkAnimateIntro.IsChecked == true;
            AnimateOutro = chkAnimateOutro.IsChecked == true;

            // PROFESIONALNI EFEKTI
            EnableCrossfade = chkCrossfade.IsChecked == true;
            EnableKenBurns = chkKenBurns.IsChecked == true;
            EnableTransitionSounds = chkTransitionSounds.IsChecked == true;
            EnableAmbientSounds = chkAmbientSounds.IsChecked == true;

            // FREESOUND API KLJUČ - uzmi iz PasswordBox-a
            if (txtFreesoundKey != null)
            {
                FreesoundApiKey = txtFreesoundKey.Password.Trim();
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