using System.Windows.Input;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;

namespace HADA.Tray.ViewModels;

public sealed record CustomSensorTypeOption(CustomSensorType Type, string Label);

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
            }
        }
    }

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
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
        _ => "Custom_ValueText",
    });

    public string ValuePlaceholder => Type switch
    {
        CustomSensorType.ProcessRunning => "chrome",
        CustomSensorType.PowerShell => "(Get-Process).Count",
        _ => string.Empty,
    };

    /// <summary>A process is either running or not; there is nothing to measure.</summary>
    public bool ShowsUnit => Type != CustomSensorType.ProcessRunning;

    /// <summary>A fixed text is sent once; there is nothing to repeat.</summary>
    public bool ShowsInterval => Type != CustomSensorType.Text;

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
