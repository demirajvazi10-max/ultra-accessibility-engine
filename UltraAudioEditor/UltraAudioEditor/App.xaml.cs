using System.Windows;

namespace UltraAudioEditor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show($"Neočekivana greška: {ex.Exception.Message}\n\n{ex.Exception.StackTrace}",
                    "Ultra Audio Editor - Greška", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
        }
    }
}
