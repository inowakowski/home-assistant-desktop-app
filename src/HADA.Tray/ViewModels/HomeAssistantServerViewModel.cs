using System.Runtime.CompilerServices;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;

namespace HADA.Tray.ViewModels;

public sealed record SensorModeOption(HomeAssistantSensorMode Mode, string Label);

/// <summary>
/// One Home Assistant connected to directly, on the Connections page. Its access token is never shown: an empty
/// field keeps the saved one.
/// </summary>
public sealed class HomeAssistantServerViewModel : ObservableObject
{
    public const string DefaultCommandEventType = "hada_command";

    private readonly Action _changed;
    private string _name;
    private string _url;
    private string _accessToken = string.Empty;
    private bool _clearAccessToken;
    private string _deviceId;
    private string _deviceName;
    private string _commandEventType;
    private bool _notifications;
    private HomeAssistantSensorMode _sensorMode;

    /// <param name="changed">Called after anything was typed, to find out whether there is something to save.</param>
    public HomeAssistantServerViewModel(HomeAssistantSettings settings, bool hasAccessToken, Action changed)
    {
        _changed = changed;
        Id = settings.Id;
        HasAccessToken = hasAccessToken;
        _name = settings.Name;
        _url = settings.BaseUrl;
        _deviceId = settings.DeviceId;
        _deviceName = settings.DeviceName;
        _commandEventType = settings.CommandEventType;
        _notifications = settings.Notifications;
        _sensorMode = settings.SensorMode;
    }

    /// <summary>A server that was just added: nothing typed yet, and an id of its own.</summary>
    /// <param name="sensors">False when Home Assistant gets the sensors through MQTT already, so that it does not get them twice.</param>
    public static HomeAssistantServerViewModel New(Action changed, bool sensors) =>
        new(new HomeAssistantSettings(string.Empty, string.Empty, string.Empty, DefaultCommandEventType)
        {
            Id = MqttSettings.NewId(),
            Notifications = true,
            SensorMode = sensors ? HomeAssistantSensorMode.Entities : HomeAssistantSensorMode.Off,
        },
        hasAccessToken: false,
        changed);

    public string Id { get; }

    public bool HasAccessToken { get; }

    public string AccessTokenPlaceholder => Loc.Get(HasAccessToken ? "Placeholder_SecretSaved" : "Placeholder_SecretNone");

    public string MachineNamePlaceholder { get; } = Loc.Format("Placeholder_MachineName", Environment.MachineName);

    /// <summary>How the server is listed: its name, else its address, else that it is new.</summary>
    public string DisplayName =>
        Name.Trim() is { Length: > 0 } name ? name
        : Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var url) && url.Host.Length > 0 ? url.Host
        : Url.Trim() is { Length: > 0 } typed ? typed
        : Loc.Get("Mqtt_NewServer");

    /// <summary>Nothing typed into it: not a server yet, and not saved.</summary>
    public bool IsBlank => Name.Trim().Length == 0 && Url.Trim().Length == 0;

    public string Name
    {
        get => _name;
        set
        {
            if (SetSetting(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetSetting(ref _url, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string AccessToken { get => _accessToken; set => SetSetting(ref _accessToken, value ?? string.Empty); }

    public bool ClearAccessToken { get => _clearAccessToken; set => SetSetting(ref _clearAccessToken, value); }

    public string DeviceId { get => _deviceId; set => SetSetting(ref _deviceId, value); }

    public string DeviceName { get => _deviceName; set => SetSetting(ref _deviceName, value); }

    public string CommandEventType { get => _commandEventType; set => SetSetting(ref _commandEventType, value); }

    public bool Notifications { get => _notifications; set => SetSetting(ref _notifications, value); }

    public HomeAssistantSensorMode SensorMode { get => _sensorMode; set => SetSetting(ref _sensorMode, value); }

    public IReadOnlyList<SensorModeOption> SensorModes { get; } =
    [
        new(HomeAssistantSensorMode.Off, Loc.Get("Ha_SensorsOff")),
        new(HomeAssistantSensorMode.Entities, Loc.Get("Ha_SensorsEntities")),
        new(HomeAssistantSensorMode.Integration, Loc.Get("Ha_SensorsIntegration")),
        new(HomeAssistantSensorMode.States, Loc.Get("Ha_SensorsStates")),
    ];

    /// <summary>The server as it would be saved.</summary>
    public HomeAssistantServerUpdate ToUpdate() => new(
        new HomeAssistantSettings(Url.Trim(), DeviceId.Trim(), DeviceName.Trim(), CommandEventType.Trim())
        {
            Id = Id,
            Name = Name.Trim(),
            Notifications = Notifications,
            SensorMode = SensorMode,
        },
        SettingsViewModel.SecretUpdateFor(AccessToken, ClearAccessToken));

    /// <param name="urlRequired">For a connection test, which needs somewhere to connect to.</param>
    public string? Validate(bool urlRequired)
    {
        var url = Url.Trim();
        if (urlRequired && url.Length == 0)
        {
            return Loc.Get("Validation_UrlRequired");
        }

        if (url.Length > 0
            && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)))
        {
            return Loc.Get("Validation_Url");
        }

        var eventType = CommandEventType.Trim();
        return eventType.Length == 0 || eventType.Any(char.IsWhiteSpace) ? Loc.Get("Validation_EventType") : null;
    }

    private bool SetSetting<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        _changed();
        return true;
    }
}
