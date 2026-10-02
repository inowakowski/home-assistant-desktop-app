using System.Windows;
using System.Windows.Controls;
using HADA.Tray.ViewModels;

namespace HADA.Tray.Views.Pages;

public partial class CustomSensorsPage : Page
{
    public CustomSensorsPage() => InitializeComponent();

    /// <summary>Re-reads the connected devices each time the list is opened, so a device plugged in a moment ago is offered.</summary>
    private void OnDevicePickerOpened(object sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CustomSensorViewModel sensor })
        {
            sensor.RefreshDevices();
        }
    }
}
