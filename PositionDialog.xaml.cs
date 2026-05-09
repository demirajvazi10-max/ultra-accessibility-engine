using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class PositionDialog : Window
    {
        // Language helper
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        private readonly List<TimelineItem> _allItems;
        private readonly TimelineItem _currentItem;
        private readonly double _audioDuration;

        public double SelectedPosition { get; private set; } = -1;
        public double SelectedDuration { get; private set; } = 5.0;
        public bool AutoPlaceAll { get; private set; } = false;

        public PositionDialog(TimelineItem currentItem, List<TimelineItem> allItems, double audioDuration)
        {
            InitializeComponent();
            _currentItem = currentItem;
            _allItems = allItems;
            _audioDuration = audioDuration;
            PopulateInfo();
        }

        private void PopulateInfo()
        {
            string dur = TimeSpan.FromSeconds(_audioDuration).ToString(@"mm\:ss");
            txtAudioInfo.Text = _audioDuration > 0
                ? string.Format("Audio trajanje: {0} ({1:F0} sekundi)", dur, _audioDuration)
                : "Nema ucitanog audio fajla.";

            txtImageName.Text = "Slika: " + _currentItem.Name;
            txtImageDesc.Text = string.IsNullOrWhiteSpace(_currentItem.AudioDescription)
                ? "(Nema audio opisa)"
                : "Opis: " + _currentItem.AudioDescription;

            double pos = _currentItem.UseFixedPosition && _currentItem.FixedPosition > 0
                ? _currentItem.FixedPosition : _currentItem.Start;

            txtStart.Text = pos.ToString("F1", CultureInfo.InvariantCulture);
            txtEnd.Text = (pos + _currentItem.Duration).ToString("F1", CultureInfo.InvariantCulture);
            txtDuration.Text = _currentItem.Duration.ToString("F1", CultureInfo.InvariantCulture);

            var images = _allItems.Where(i => i.IsImage || i.IsVideo).ToList();
            if (images.Count > 0 && _audioDuration > 0)
            {
                double interval = _audioDuration / images.Count;
                txtAutoInfo.Text = string.Format(
                    "{0} slika, audio {1}.\nRavnomerni raspored: jedna slika svakih {2:F1} sekundi.",
                    images.Count, dur, interval);
            }
            else
            {
                txtAutoInfo.Text = images.Count == 0 ? "Nema slika na timeline-u." : "Nije ucitan audio fajl.";
            }

            RefreshScheduleList();
        }

        private void RefreshScheduleList()
        {
            lstSchedule.Items.Clear();
            var images = _allItems
                .Where(i => i.IsImage || i.IsVideo)
                .OrderBy(i => i.UseFixedPosition && i.FixedPosition > 0 ? i.FixedPosition : i.Start)
                .ToList();

            foreach (var img in images)
            {
                double p = img.UseFixedPosition && img.FixedPosition > 0 ? img.FixedPosition : img.Start;
                string mark = img == _currentItem ? " <-- OVA" : "";
                string desc = string.IsNullOrWhiteSpace(img.AudioDescription) ? ""
                    : " | " + img.AudioDescription.Substring(0, Math.Min(35, img.AudioDescription.Length)) + "...";
                lstSchedule.Items.Add(
                    string.Format("{0} -> {1}  |  {2}{3}{4}",
                        FormatTime(p), FormatTime(p + img.Duration), img.Name, desc, mark));
            }
        }

        private void AutoPlace_Click(object sender, RoutedEventArgs e)
        {
            var images = _allItems.Where(i => i.IsImage || i.IsVideo).ToList();
            if (images.Count == 0) return;

            double total = _audioDuration > 0 ? _audioDuration : images.Sum(i => i.Duration);
            double step = total / images.Count;

            for (int i = 0; i < images.Count; i++)
            {
                images[i].FixedPosition = Math.Round(i * step, 2);
                images[i].Duration = Math.Round(step, 2);
                images[i].UseFixedPosition = true;
            }
            AutoPlaceAll = true;
            RefreshScheduleList();
            txtAutoInfo.Text = string.Format(
                "Primenjen ravnomerni raspored: {0} slika, svaka po {1:F1} sekundi.", images.Count, step);
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            double start = -1;

            // Čitanje početka iz txtStart
            if (double.TryParse(txtStart.Text.Replace(',', '.'),
                                NumberStyles.Any, CultureInfo.InvariantCulture, out start))
            {
                SelectedPosition = start;
            }
            else
            {
                WpfMessageBox.Show(L("pd_invalid_start"), L("error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Čitanje trajanja
            if (double.TryParse(txtDuration.Text.Replace(',', '.'),
                                NumberStyles.Any, CultureInfo.InvariantCulture, out double dur) && dur > 0)
            {
                SelectedDuration = dur;
            }
            else
            {
                if (double.TryParse(txtEnd.Text.Replace(',', '.'),
                                    NumberStyles.Any, CultureInfo.InvariantCulture, out double end) && end > start)
                {
                    SelectedDuration = end - start;
                }
                else
                {
                    SelectedDuration = 5.0;
                }
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string FormatTime(double seconds)
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");
        }
    }
}