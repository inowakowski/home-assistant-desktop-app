using System.Windows.Input;
using HADA.Ipc;
using HADA.Platform.Windows.Sensors;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;

namespace HADA.Tray.ViewModels;

public sealed record CustomSensorTypeOption(CustomSensorType Type, string Label);

/// <summary>A connected USB device offered for a "device is connected" sensor.</summary>
public sealed record UsbDeviceOption(string MatchId, string Name)
{
    /// <summary>Names alone are often generic ("USB hub"), so the id is shown too.</summary>
    public string Label => $"{Name}  ·  {MatchId}";
}

/// <summary>One editable custom sensor on the Custom sensors page. Edits the pending settings, not the service directly.</summary>
public sealed class CustomSensorViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _id;
    private string _name;
    private CustomSensorType _type;
    private string _value;
    private string _unit;
    private double? _intervalSeconds;
    private IReadOnlyList<UsbDeviceOption>? _connectedDevices;

    /// <param name="changed">Called after every edit, so the owner can update its unsaved-changes state.</param>
    public CustomSensorViewModel(CustomSensorDefinition definition, Action changed, Action<CustomSensorViewModel> remove)
    {
        _changed = changed;
        _id = definition.Id;
        _name = definition.Name;
        _type = definition.Type;
        _value = definition.Value;
        _unit = definition.Unit;
        _intervalSeconds = definition.IntervalSeconds;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public static IReadOnlyList<CustomSensorTypeOption> TypeOptions { get; } =
    [
        new(CustomSensorType.Text, Loc.Get("Custom_TypeText")),
        new(CustomSensorType.ProcessRunning, Loc.Get("Custom_TypeProcess")),
        new(CustomSensorType.PowerShell, Loc.Get("Custom_TypePowerShell")),
        new(CustomSensorType.DeviceConnected, Loc.Get("Custom_TypeDevice")),
        new(CustomSensorType.CommandButton, Loc.Get("Custom_TypeCommandButton")),
        new(CustomSensorType.LaunchButton, Loc.Get("Custom_TypeLaunchButton")),
        new(CustomSensorType.KeysButton, Loc.Get("Custom_TypeKeysButton")),
        new(CustomSensorType.QuickAction, Loc.Get("Custom_TypeQuickAction")),
    ];

    public double MinInterval => CustomSensorDefinition.MinIntervalSeconds;

    public double MaxInterval => CustomSensorDefinition.MaxIntervalSeconds;

    public ICommand RemoveCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                OnPropertyChanged(nameof(IdPlaceholder));
            }
        }
    }

    /// <summary>Empty means "derive it from the name", which the placeholder previews.</summary>
    public string Id
    {
        get => _id;
        set => Set(ref _id, value);
    }

    public string IdPlaceholder =>
        CustomSensorDefinition.ToId(Name) is { Length: > 0 } derived ? derived : Loc.Get("Custom_IdAuto");

    public CustomSensorType Type
    {
        get => _type;
        set
        {
            if (Set(ref _type, value))
            {
                OnPropertyChanged(nameof(ValueLabel));
                OnPropertyChanged(nameof(ValuePlaceholder));
                OnPropertyChanged(nameof(ShowsUnit));
                OnPropertyChanged(nameof(ShowsInterval));
                OnPropertyChanged(nameof(ShowsDevicePicker));
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
            {
                OnPropertyChanged(nameof(SelectedDevice));
            }
        }
    }

    public bool ShowsDevicePicker => Type == CustomSensorType.DeviceConnected;

    /// <summary>USB devices connected right now; read when first shown and again by <see cref="RefreshDevices"/>.</summary>
    public IReadOnlyList<UsbDeviceOption> ConnectedDevices =>
        _connectedDevices ??= [.. PnpDevices.ConnectedUsbDevices().Select(device => new UsbDeviceOption(device.MatchId, device.Name))];

    /// <summary>Picking a device fills in its id, and its name when the sensor has none yet.</summary>
    public UsbDeviceOption? SelectedDevice
    {
        get => ConnectedDevices.FirstOrDefault(device => string.Equals(device.MatchId, Value.Trim(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            Value = value.MatchId;
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = value.Name;
            }
        }
    }

    /// <summary>Called when the list is opened, so a device plugged in a moment ago is in it.</summary>
    public void RefreshDevices()
    {
        _connectedDevices = null;
        OnPropertyChanged(nameof(ConnectedDevices));
        OnPropertyChanged(nameof(SelectedDevice));
    }

    public string Unit
    {
        get => _unit;
        set => Set(ref _unit, value);
    }

    public double? IntervalSeconds
    {
        get => _intervalSeconds;
        set => Set(ref _intervalSeconds, value);
    }

    public string ValueLabel => Loc.Get(Type switch
    {
        CustomSensorType.ProcessRunning => "Custom_ValueProcess",
        CustomSensorType.PowerShell => "Custom_ValuePowerShell",
        CustomSensorType.DeviceConnected => "Custom_ValueDevice",
        CustomSensorType.CommandButton => "Custom_ValueCommandButton",
        CustomSensorType.LaunchButton => "Custom_ValueLaunchButton",
        CustomSensorType.KeysButton => "Custom_ValueKeysButton",
        CustomSensorType.QuickAction => "Custom_ValueQuickAction",
        _ => "Custom_ValueText",
    });

    public string ValuePlaceholder => Type switch
    {
        CustomSensorType.ProcessRunning => "chrome",
        CustomSensorType.PowerShell => "(Get-Process).Count",
        CustomSensorType.DeviceConnected => "VID_0BDA&PID_8153",
        CustomSensorType.CommandButton => "Restart-Service Spooler",
        CustomSensorType.KeysButton => "Ctrl+Shift+M",
        CustomSensorType.QuickAction => "Ctrl+Alt+L",
        CustomSensorType.LaunchButton => "\"C:\\Program Files\\App\\app.exe\" --option",
        _ => string.Empty,
    };

    /// <summary>A process is running or not, a device connected or not; there is nothing to measure.</summary>
    public bool ShowsUnit => Type is CustomSensorType.Text or CustomSensorType.PowerShell;

    /// <summary>A fixed text is sent once, and a button waits to be pressed; there is nothing to repeat.</summary>
    public bool ShowsInterval =>
        Type is CustomSensorType.ProcessRunning or CustomSensorType.PowerShell or CustomSensorType.DeviceConnected;

    public CustomSensorDefinition ToDefinition() => new CustomSensorDefinition
    {
        Id = Id,
        Name = Name,
        Type = Type,
        Value = Value,
        Unit = Unit,
        IntervalSeconds = (int)Math.Round(IntervalSeconds ?? CustomSensorDefinition.DefaultIntervalSeconds),
    }.Normalize();

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        _changed();
        return true;
    }
}
