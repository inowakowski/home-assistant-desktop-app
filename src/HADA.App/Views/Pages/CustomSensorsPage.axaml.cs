using Avalonia.Controls;
using HADA.Presentation.ViewModels;

namespace HADA.App.Views.Pages;

public sealed partial class CustomSensorsPage : UserControl
{
    public CustomSensorsPage() => InitializeComponent();

    /// <summary>Reads the connected devices again each time the list is opened, so a device plugged in a moment ago is offered.</summary>
    private void OnDevicePickerOpened(object? sender, EventArgs e)
    {
        if (sender is Control { DataContext: CustomSensorViewModel sensor })
        {
            sensor.RefreshDevices();
        }
    }
}
