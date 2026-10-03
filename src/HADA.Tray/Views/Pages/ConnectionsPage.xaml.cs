using System.Windows.Controls;
using System.Windows.Threading;
using HADA.Tray.ViewModels;

namespace HADA.Tray.Views.Pages;

public partial class ConnectionsPage : Page
{
    public ConnectionsPage() => InitializeComponent();

    /// <summary>
    /// Scrolls to one of the two kinds of connection: <c>ha</c> for Home Assistant, <c>ha=</c> and an id for one
    /// of the Home Assistants, <c>mqtt=</c> and an id for one of the MQTT servers, anything else for MQTT.
    /// </summary>
    public void ShowSection(string section)
    {
        const string ServerPrefix = "mqtt=";
        const string HomeAssistantPrefix = "ha=";
        var isHomeAssistant = section == "ha" || section.StartsWith(HomeAssistantPrefix, StringComparison.Ordinal);
        if (DataContext is MainViewModel main)
        {
            if (section.StartsWith(ServerPrefix, StringComparison.Ordinal))
            {
                main.Settings.ShowMqttServer(section[ServerPrefix.Length..]);
            }
            else if (section.StartsWith(HomeAssistantPrefix, StringComparison.Ordinal))
            {
                main.Settings.ShowHomeAssistantServer(section[HomeAssistantPrefix.Length..]);
            }
        }

        // After layout: right after navigating here the page may not have been measured yet.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => (isHomeAssistant ? HomeAssistantCard : MqttCard).BringIntoView());
    }
}
