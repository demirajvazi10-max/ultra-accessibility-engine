using System.Windows;
using System.Windows.Automation;

namespace UltraVideoEditor
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Pristupačnost za čitače ekrana
            // NE koristite AutomationProperties.SetName na App klasi
            // Umjesto toga, postavite naslov prozora
        }
    }
}