using System.Runtime.CompilerServices;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;

namespace HADA.Tray.ViewModels;

/// <summary>
/// One MQTT server on the Connections page, which is one Home Assistant. Its password is never shown: an empty
/// field keeps the saved one.
/// </summary>
public sealed class MqttServerViewModel : ObservableObject
{
    public const int DefaultPort = 1883;

    private readonly Action _changed;
    private string _name;
    private string _host;
    private double? _port;
    private bool _useTls;
    private string _username;
    private string _password = string.Empty;
    private bool _clearPassword;
    private string _deviceId;
    private string _deviceName;
    private string _discoveryPrefix;
    private string _baseTopic;

    /// <param name="changed">Called after anything was typed, to find out whether there is something to save.</param>
    public MqttServerViewModel(MqttSettings settings, bool hasPassword, Action changed)
    {
        _changed = changed;
        Id = settings.Id;
        HasPassword = hasPassword;
        _name = settings.Name;
        _host = settings.Host;
        _port = settings.Port;
        _useTls = settings.UseTls;
        _username = settings.Username;
        _deviceId = settings.DeviceId;
        _deviceName = settings.DeviceName;
        _discoveryPrefix = settings.DiscoveryPrefix;
        _baseTopic = settings.BaseTopic;
    }

    /// <summary>A server that was just added: nothing typed yet, and an id of its own.</summary>
    public static MqttServerViewModel New(Action changed) =>
        new(new MqttSettings(string.Empty, DefaultPort, false, string.Empty, string.Empty, string.Empty, "homeassistant", "hada")
        {
            Id = MqttSettings.NewId(),
        },
        hasPassword: false,
        changed);

    public string Id { get; }

    public bool HasPassword { get; }

    public string PasswordPlaceholder => Loc.Get(HasPassword ? "Placeholder_SecretSaved" : "Placeholder_SecretNone");

    public string MachineNamePlaceholder { get; } = Loc.Format("Placeholder_MachineName", Environment.MachineName);

    /// <summary>How the server is listed: its name, else its address, else that it is new.</summary>
    public string DisplayName =>
        Name.Trim() is { Length: > 0 } name ? name
        : MqttAddress.Parse(Host).Host is { Length: > 0 } host ? host
        : Loc.Get("Mqtt_NewServer");

    /// <summary>Nothing typed into it: not a server yet, and not saved.</summary>
    public bool IsBlank => Name.Trim().Length == 0 && MqttAddress.Parse(Host).Host.Length == 0;

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

    public string Host
    {
        get => _host;
        set
        {
            if (SetSetting(ref _host, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public double? Port { get => _port; set => SetSetting(ref _port, value); }

    public bool UseTls { get => _useTls; set => SetSetting(ref _useTls, value); }

    public string Username { get => _username; set => SetSetting(ref _username, value); }

    public string Password { get => _password; set => SetSetting(ref _password, value ?? string.Empty); }

    public bool ClearPassword { get => _clearPassword; set => SetSetting(ref _clearPassword, value); }

    public string DeviceId { get => _deviceId; set => SetSetting(ref _deviceId, value); }

    public string DeviceName { get => _deviceName; set => SetSetting(ref _deviceName, value); }

    public string DiscoveryPrefix { get => _discoveryPrefix; set => SetSetting(ref _discoveryPrefix, value); }

    public string BaseTopic { get => _baseTopic; set => SetSetting(ref _baseTopic, value); }

    /// <summary>The server as it would be saved.</summary>
    public MqttServerUpdate ToUpdate() => new(
        // A pasted "mqtt://broker:1883" is split into host and port, as the service will store it.
        MqttAddress.Apply(new MqttSettings(
            Host,
            (int)Math.Round(Port ?? DefaultPort),
            UseTls,
            Username.Trim(),
            DeviceId.Trim(),
            DeviceName.Trim(),
            DiscoveryPrefix.Trim().Trim('/'),
            BaseTopic.Trim().Trim('/'))
        {
            Id = Id,
            Name = Name.Trim(),
        }),
        SettingsViewModel.SecretUpdateFor(Password, ClearPassword));

    /// <param name="hostRequired">For a connection test, which needs somewhere to connect to.</param>
    public string? Validate(bool hostRequired)
    {
        var host = MqttAddress.Parse(Host).Host;
        if (hostRequired && host.Length == 0)
        {
            return Loc.Get("Validation_HostRequired");
        }

        if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return Loc.Get("Validation_Host");
        }

        if (Port is not { } port || port is < 1 or > 65535 || port != Math.Floor(port))
        {
            return Loc.Get("Validation_Port");
        }

        return IsTopicPrefix(DiscoveryPrefix) && IsTopicPrefix(BaseTopic) ? null : Loc.Get("Validation_Topic");
    }

    private static bool IsTopicPrefix(string value)
    {
        var trimmed = value.Trim().Trim('/');
        return trimmed.Length > 0 && !trimmed.Any(c => char.IsWhiteSpace(c) || c is '+' or '#');
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
