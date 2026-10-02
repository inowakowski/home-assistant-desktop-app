using System.Windows.Controls;
using System.Windows.Threading;
using HADA.Tray.ViewModels;

namespace HADA.Tray.Views.Pages;

public partial class ConnectionsPage : Page
{
    public ConnectionsPage() => InitializeComponent();

    /// <summary>
    /// Scrolls to one of the two connections: <c>ha</c> for Home Assistant, <c>mqtt=</c> and an id for one of the
    /// MQTT servers, anything else for MQTT.
    /// </summary>
    public void ShowSection(string section)
    {
        const string ServerPrefix = "mqtt=";
        if (section.StartsWith(ServerPrefix, StringComparison.Ordinal) && DataContext is MainViewModel main)
        {
            main.Settings.ShowMqttServer(section[ServerPrefix.Length..]);
        }

        // After layout: right after navigating here the page may not have been measured yet.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => (section == "ha" ? HomeAssistantCard : MqttCard).BringIntoView());
    }
}
