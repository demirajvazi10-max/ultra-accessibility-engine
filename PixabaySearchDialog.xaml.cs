using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Newtonsoft.Json.Linq;
using WpfMessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace UltraVideoEditor
{
    public partial class PixabaySearchDialog : Window
    {
        private string _apiKey;
        private List<PixabayMediaItem> _currentResults;
        private bool _isVideoMode = false;

        public List<string> DownloadedMediaPaths { get; private set; } = new List<string>();

        public PixabaySearchDialog()
        {
            InitializeComponent();
            LoadSavedApiKey();
        }

        private void LoadSavedApiKey()
        {
            var savedKey = GetPixabayApiKey();
            if (!string.IsNullOrEmpty(savedKey))
            {
                _apiKey = savedKey;
                txtApiKey.Password = "••••••••";
                txtApiKey.IsEnabled = false;
                txtSearch.Focus();
            }
        }

        private string GetPixabayApiKey()
        {
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
            string keyFile = Path.Combine(settingsPath, "pixabay_key.bin");
            if (!File.Exists(keyFile)) return null;
            try
            {
                byte[] encrypted = File.ReadAllBytes(keyFile);
                byte[] decrypted = System.Security.Cryptography.ProtectedData.Unprotect(encrypted, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(decrypted);
            }
            catch { return null; }
        }

        private void SavePixabayApiKey(string key)
        {
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
            Directory.CreateDirectory(settingsPath);
            string keyFile = Path.Combine(settingsPath, "pixabay_key.bin");
            byte[] data = System.Text.Encoding.UTF8.GetBytes(key);
            byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            File.WriteAllBytes(keyFile, encrypted);
        }

        private void cmbMediaType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = cmbMediaType.SelectedItem as ComboBoxItem;
            if (selected == null) return;

            _isVideoMode = selected.Tag?.ToString() == "video";

            // Prikaži/sakrij video filtere
            if (videoFilters != null)
                videoFilters.Visibility = _isVideoMode ? Visibility.Visible : Visibility.Collapsed;

            if (cmbColor != null)
                cmbColor.IsEnabled = !_isVideoMode;

            if (!string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                _ = SearchMedia();
            }
        }

        private async void btnSearch_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(txtApiKey.Password))
            {
                _apiKey = txtApiKey.Password;
                SavePixabayApiKey(_apiKey);
                txtApiKey.IsEnabled = false;
                txtApiKey.Password = "••••••••";
            }

            if (string.IsNullOrEmpty(_apiKey))
            {
                WpfMessageBox.Show("Unesite Pixabay API ključ. Dobijte ga besplatno na pixabay.com/api/docs/",
                    "API ključ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                WpfMessageBox.Show("Unesite pojam za pretragu", "Pretraga", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await SearchMedia();
        }

        private async Task SearchMedia()
        {
            try
            {
                lstResults.Items.Clear();
                lstResults.Items.Add("🔍 Pretražujem...");

                string orientation = GetSelectedTag(cmbOrientation, "all");
                string category = GetSelectedTag(cmbCategory, "");
                int minWidth = int.Parse(GetSelectedTag(cmbMinWidth, "0"));
                bool editorsChoice = chkEditorsChoice.IsChecked == true;

                string url;

                if (_isVideoMode)
                {
                    int minDuration = int.Parse(GetSelectedTag(cmbMinDuration, "0"));
                    string videoQuality = GetSelectedTag(cmbVideoQuality, "all");

                    url = $"https://pixabay.com/api/videos/?key={_apiKey}" +
                          $"&q={Uri.EscapeDataString(txtSearch.Text)}" +
                          $"&safesearch=true" +
                          $"&per_page=50" +
                          (minDuration > 0 ? $"&duration={minDuration}" : "") +
                          (editorsChoice ? "&editors_choice=true" : "") +
                          (!string.IsNullOrEmpty(category) ? $"&category={category}" : "");
                }
                else
                {
                    string color = GetSelectedTag(cmbColor, "");

                    url = $"https://pixabay.com/api/?key={_apiKey}" +
                          $"&q={Uri.EscapeDataString(txtSearch.Text)}" +
                          $"&image_type=photo" +
                          $"&orientation={orientation}" +
                          $"&safesearch=true" +
                          $"&per_page=50" +
                          $"&order=popular" +
                          (minWidth > 0 ? $"&min_width={minWidth}" : "") +
                          (editorsChoice ? "&editors_choice=true" : "") +
                          (!string.IsNullOrEmpty(category) ? $"&category={category}" : "") +
                          (!string.IsNullOrEmpty(color) ? $"&colors={color}" : "");
                }

                using (var client = new HttpClient())
                {
                    var response = await client.GetStringAsync(url);
                    var json = JObject.Parse(response);
                    var hits = json["hits"] as JArray;

                    lstResults.Items.Clear();
                    _currentResults = new List<PixabayMediaItem>();

                    if (hits != null && hits.Count > 0)
                    {
                        foreach (var hit in hits)
                        {
                            var item = new PixabayMediaItem();
                            item.Id = (int)hit["id"];
                            item.IsVideo = _isVideoMode;
                            item.Likes = (int)hit["likes"];
                            item.Views = (int)hit["views"];
                            item.Downloads = (int)hit["downloads"];
                            item.User = hit["user"]?.ToString();

                            if (_isVideoMode)
                            {
                                item.Title = hit["tags"]?.ToString() ?? "";
                                var videos = hit["videos"] as JObject;
                                if (videos != null)
                                {
                                    var smallVideo = videos["small"]?["url"]?.ToString();
                                    var mediumVideo = videos["medium"]?["url"]?.ToString();
                                    var largeVideo = videos["large"]?["url"]?.ToString();

                                    item.ThumbnailURL = hit["videos"]?["tiny"]?["thumbnail"]?.ToString() ?? "";
                                    item.DownloadURL = largeVideo ?? mediumVideo ?? smallVideo;
                                    item.Dimensions = $"{hit["width"]}x{hit["height"]}";

                                    if (hit["duration"] != null)
                                    {
                                        double duration = (double)hit["duration"];
                                        item.Duration = TimeSpan.FromSeconds(duration).ToString(@"mm\:ss");
                                    }
                                }
                            }
                            else
                            {
                                item.Title = hit["tags"]?.ToString() ?? "";
                                item.ThumbnailURL = hit["previewURL"]?.ToString();
                                item.DownloadURL = hit["largeImageURL"]?.ToString() ?? hit["webformatURL"]?.ToString();
                                item.Dimensions = $"{hit["imageWidth"]}x{hit["imageHeight"]}";
                                item.Duration = "";
                            }

                            _currentResults.Add(item);
                            lstResults.Items.Add(item);
                        }

                        txtSelectedInfo.Text = $"Pronađeno {hits.Count} rezultata. Selektujte jedan ili više (Ctrl+klik).";
                    }
                    else
                    {
                        lstResults.Items.Add("📭 Nema rezultata. Pokušajte drugi pojam.");
                        txtSelectedInfo.Text = "Nema rezultata za ovu pretragu.";
                    }
                }
            }
            catch (Exception ex)
            {
                lstResults.Items.Clear();
                lstResults.Items.Add($"❌ Greška: {ex.Message}");
                txtSelectedInfo.Text = $"Greška: {ex.Message}";
            }
        }

        private string GetSelectedTag(ComboBox combo, string defaultValue)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag != null)
                return item.Tag.ToString();
            return defaultValue;
        }

        private void lstResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int selectedCount = lstResults.SelectedItems.Count;
            if (selectedCount > 0)
            {
                btnDownload.IsEnabled = true;
                btnDownload.Content = _isVideoMode ? $"📥 PREUZMI VIDEO ({selectedCount})" : $"📥 PREUZMI SLIKE ({selectedCount})";

                if (selectedCount == 1 && lstResults.SelectedItem is PixabayMediaItem item)
                {
                    string type = _isVideoMode ? "🎥 Video" : "📷 Slika";
                    txtSelectedInfo.Text = $"{type}: {item.Title}\n👍 {item.Likes} lajkova | 👁️ {item.Views} pregleda | 📥 {item.Downloads} preuzimanja\n👤 Autor: {item.User}\n📏 {item.Dimensions}";
                    if (!string.IsNullOrEmpty(item.Duration))
                        txtSelectedInfo.Text += $"\n⏱️ Trajanje: {item.Duration}";
                }
                else
                {
                    txtSelectedInfo.Text = $"✅ Odabrano {selectedCount} stavki. Pritisnite dugme za preuzimanje.";
                }
            }
            else
            {
                btnDownload.IsEnabled = false;
                btnDownload.Content = _isVideoMode ? "📥 PREUZMI VIDEO" : "📥 PREUZMI SLIKE";
                txtSelectedInfo.Text = "Selektujte jednu ili više stavki (Ctrl+klik ili Shift+klik).";
            }
        }

        private async void btnDownload_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = lstResults.SelectedItems.Cast<PixabayMediaItem>().ToList();
            if (selectedItems.Count == 0)
            {
                WpfMessageBox.Show("Selektujte jednu ili više stavki za preuzimanje.", "Upozorenje",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                btnDownload.IsEnabled = false;
                btnDownload.Content = $"⏳ Preuzimam {selectedItems.Count} stavki...";

                string projectFolder = GetCurrentProjectFolder();
                DownloadedMediaPaths.Clear();
                int downloaded = 0;

                using (var client = new HttpClient())
                {
                    foreach (var item in selectedItems)
                    {
                        string extension = _isVideoMode ? ".mp4" : ".jpg";
                        string fileName = $"Pixabay_{item.Id}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString().Substring(0, 4)}{extension}";
                        string fullPath = Path.Combine(projectFolder, fileName);

                        var mediaData = await client.GetByteArrayAsync(item.DownloadURL);
                        await File.WriteAllBytesAsync(fullPath, mediaData);
                        DownloadedMediaPaths.Add(fullPath);
                        downloaded++;

                        btnDownload.Content = $"⏳ Preuzeto {downloaded}/{selectedItems.Count}...";
                    }
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Greška pri preuzimanju: {ex.Message}", "Greška",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                btnDownload.IsEnabled = true;
                btnDownload.Content = _isVideoMode ? "📥 PREUZMI VIDEO" : "📥 PREUZMI SLIKE";
            }
        }

        private string GetCurrentProjectFolder()
        {
            if (WpfApplication.Current.MainWindow is MainWindow mainWin)
            {
                return mainWin.GetCurrentProjectFolder();
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void txtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                btnSearch_Click(null, null);
            }
        }
    }

    public class PixabayMediaItem
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string ThumbnailURL { get; set; }
        public string DownloadURL { get; set; }
        public string Dimensions { get; set; }
        public string Duration { get; set; }
        public int Likes { get; set; }
        public int Views { get; set; }
        public int Downloads { get; set; }
        public string User { get; set; }
        public bool IsVideo { get; set; }
    
    
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is Visibility visibility && visibility == Visibility.Visible;
        }
    }
}