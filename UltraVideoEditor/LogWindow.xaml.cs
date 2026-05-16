using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

// Ovo rješava Clipboard i KeyEventArgs ambiguity
using WpfClipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace UltraVideoEditor
{
    public partial class LogWindow : Window
    {
        private ObservableCollection<LogEntry> _logEntries = new ObservableCollection<LogEntry>();

        public LogWindow()
        {
            InitializeComponent();
            lstLog.ItemsSource = _logEntries;
            lstLog.KeyDown += LstLog_KeyDown;
            AutomationProperties.SetName(this, "Log prozor");
        }

        public void AddMessage(string message, bool isAnnouncement = false)
        {
            Dispatcher.Invoke(() =>
            {
                var entry = new LogEntry
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Message = message,
                    IsAnnouncement = isAnnouncement
                };
                _logEntries.Add(entry);

                lstLog.ScrollIntoView(entry);
                txtStatus.Text = $"Ukupno poruka: {_logEntries.Count}";
            });
        }

        private void LstLog_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (lstLog.SelectedItem is LogEntry entry)
                {
                    WpfClipboard.SetText($"[{entry.Time}] {entry.Message}");
                    AnnounceToJaws("Kopirana selektovana poruka");
                }
                e.Handled = true;
            }
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            foreach (var entry in _logEntries)
            {
                sb.AppendLine($"[{entry.Time}] {entry.Message}");
            }
            WpfClipboard.SetText(sb.ToString());
            AnnounceToJaws($"Kopirano {_logEntries.Count} poruka");
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _logEntries.Clear();
            AnnounceToJaws("Log obrisan");
            txtStatus.Text = "Log obrisan";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        private void AnnounceToJaws(string message)
        {
            var peer = UIElementAutomationPeer.FromElement(lstLog);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            txtStatus.Text = message;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
            base.OnClosing(e);
        }
    }

    public class LogEntry
    {
        public string Time { get; set; }
        public string Message { get; set; }
        public bool IsAnnouncement { get; set; }
    }
}