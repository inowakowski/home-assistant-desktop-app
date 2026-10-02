using System.Windows.Controls;
using System.Windows.Threading;

namespace HADA.Tray.Views.Pages;

public partial class ConnectionsPage : Page
{
    public ConnectionsPage() => InitializeComponent();

    /// <summary>Scrolls to one of the two connections: <c>ha</c> for Home Assistant, anything else for MQTT.</summary>
    public void ShowSection(string section) =>

        // After layout: right after navigating here the page may not have been measured yet.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => (section == "ha" ? HomeAssistantCard : MqttCard).BringIntoView());
}
