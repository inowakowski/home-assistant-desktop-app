using System.Runtime.CompilerServices;
using System.Windows.Input;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;
using Wpf.Ui.Controls;

namespace HADA.Tray.ViewModels;

/// <summary>
/// Editable copy of the service's settings, shared by the Connections and Entities pages so one save covers both.
/// Secrets are never shown: an empty password or token field keeps the saved value.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private const int DefaultMqttPort = 1883;

    private readonly ServiceControlClient _client;
    private readonly HashSet<string> _disabledEntities = new(StringComparer.Ordinal);
    private readonly AsyncCommand _saveCommand;
    private readonly RelayCommand _revertCommand;
    private readonly AsyncCommand _testMqttCommand;
    private readonly AsyncCommand _testHomeAssistantCommand;

    private SettingsSnapshot? _snapshot;
    private bool _isApplying;
    private bool _isBusy;
    private bool _isDirty;
    private bool _isTestingMqtt;
    private bool _isTestingHomeAssistant;

    private string _mqttHost = string.Empty;
    private double? _mqttPort = DefaultMqttPort;
    private bool _mqttUseTls;
    private string _mqttUsername = string.Empty;
    private string _mqttPassword = string.Empty;
    private bool _clearMqttPassword;
    private string _mqttDeviceId = string.Empty;
    private string _mqttDeviceName = string.Empty;
    private string _mqttDiscoveryPrefix = "homeassistant";
    private string _mqttBaseTopic = "hada";
    private string _homeAssistantUrl = string.Empty;
    private string _accessToken = string.Empty;
    private bool _clearAccessToken;
    private string _homeAssistantDeviceId = string.Empty;
    private string _homeAssistantDeviceName = string.Empty;
    private string _commandEventType = "hada_command";

    public SettingsViewModel(ServiceControlClient client, bool isElevated)
    {
        _client = client;
        IsElevated = isElevated;
        _saveCommand = new AsyncCommand(SaveAsync, () => CanEdit && IsDirty && !IsBusy);
        _revertCommand = new RelayCommand(Revert, () => IsDirty && !IsBusy);
        _testMqttCommand = new AsyncCommand(() => TestAsync(ConnectionTarget.Mqtt), () => CanEdit && !IsBusy);
        _testHomeAssistantCommand = new AsyncCommand(() => TestAsync(ConnectionTarget.HomeAssistant), () => CanEdit && !IsBusy);
    }

    public event EventHandler? DisabledEntitiesChanged;

    public bool IsElevated { get; }

    public bool IsLocked => !IsElevated;

    public bool IsLoaded => _snapshot is not null;

    public bool CanEdit => IsElevated && IsLoaded;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsTestingMqtt
    {
        get => _isTestingMqtt;
        private set => SetProperty(ref _isTestingMqtt, value);
    }

    public bool IsTestingHomeAssistant
    {
        get => _isTestingHomeAssistant;
        private set => SetProperty(ref _isTestingHomeAssistant, value);
    }

    public InfoBarViewModel Feedback { get; } = new();

    public InfoBarViewModel MqttTest { get; } = new();

    public InfoBarViewModel HomeAssistantTest { get; } = new();

    public string MachineNamePlaceholder { get; } = Loc.Format("Placeholder_MachineName", Environment.MachineName);

    public bool HasMqttPassword => _snapshot?.HasMqttPassword ?? false;

    public bool HasAccessToken => _snapshot?.HasAccessToken ?? false;

    public string MqttPasswordPlaceholder => Loc.Get(HasMqttPassword ? "Placeholder_SecretSaved" : "Placeholder_SecretNone");

    public string AccessTokenPlaceholder => Loc.Get(HasAccessToken ? "Placeholder_SecretSaved" : "Placeholder_SecretNone");

    public string MqttHost { get => _mqttHost; set => SetSetting(ref _mqttHost, value); }

    public double? MqttPort { get => _mqttPort; set => SetSetting(ref _mqttPort, value); }

    public bool MqttUseTls { get => _mqttUseTls; set => SetSetting(ref _mqttUseTls, value); }

    public string MqttUsername { get => _mqttUsername; set => SetSetting(ref _mqttUsername, value); }

    public string MqttPassword { get => _mqttPassword; set => SetSetting(ref _mqttPassword, value ?? string.Empty); }

    public bool ClearMqttPassword { get => _clearMqttPassword; set => SetSetting(ref _clearMqttPassword, value); }

    public string MqttDeviceId { get => _mqttDeviceId; set => SetSetting(ref _mqttDeviceId, value); }

    public string MqttDeviceName { get => _mqttDeviceName; set => SetSetting(ref _mqttDeviceName, value); }

    public string MqttDiscoveryPrefix { get => _mqttDiscoveryPrefix; set => SetSetting(ref _mqttDiscoveryPrefix, value); }

    public string MqttBaseTopic { get => _mqttBaseTopic; set => SetSetting(ref _mqttBaseTopic, value); }

    public string HomeAssistantUrl { get => _homeAssistantUrl; set => SetSetting(ref _homeAssistantUrl, value); }

    public string AccessToken { get => _accessToken; set => SetSetting(ref _accessToken, value ?? string.Empty); }

    public bool ClearAccessToken { get => _clearAccessToken; set => SetSetting(ref _clearAccessToken, value); }

    public string HomeAssistantDeviceId { get => _homeAssistantDeviceId; set => SetSetting(ref _homeAssistantDeviceId, value); }

    public string HomeAssistantDeviceName { get => _homeAssistantDeviceName; set => SetSetting(ref _homeAssistantDeviceName, value); }

    public string CommandEventType { get => _commandEventType; set => SetSetting(ref _commandEventType, value); }

    public ICommand SaveCommand => _saveCommand;

    public ICommand RevertCommand => _revertCommand;

    public ICommand TestMqttCommand => _testMqttCommand;

    public ICommand TestHomeAssistantCommand => _testHomeAssistantCommand;

    public bool IsEntityDisabled(string entityId) => _disabledEntities.Contains(entityId);

    public void SetEntityDisabled(string entityId, bool disabled)
    {
        var changed = disabled ? _disabledEntities.Add(entityId) : _disabledEntities.Remove(entityId);
        if (changed)
        {
            UpdateDirty();
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            Apply(await _client.GetSettingsAsync());
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            // The overview already shows that the service cannot be reached; loading is retried on the next refresh.
        }
    }

    private async Task SaveAsync()
    {
        if (Validate(target: null) is { } error)
        {
            Feedback.Show(Loc.Get("Validation_Title"), error, InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        Feedback.Close();
        try
        {
            var result = await _client.SaveSettingsAsync(BuildUpdate());
            if (!result.Success)
            {
                Feedback.Show(Loc.Get("Save_FailureTitle"), result.Message ?? string.Empty, InfoBarSeverity.Error);
                return;
            }

            Apply(await _client.GetSettingsAsync());
            Feedback.Show(Loc.Get("Save_SuccessTitle"), Loc.Get("Save_SuccessMessage"), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            Feedback.Show(Loc.Get("Save_FailureTitle"), ServiceErrors.Describe(ex), InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestAsync(ConnectionTarget target)
    {
        var result = target == ConnectionTarget.Mqtt ? MqttTest : HomeAssistantTest;
        if (Validate(target) is { } error)
        {
            result.Show(Loc.Get("Validation_Title"), error, InfoBarSeverity.Warning);
            return;
        }

        SetTesting(target, true);
        IsBusy = true;
        result.Close();
        try
        {
            var outcome = await _client.TestConnectionAsync(target, BuildUpdate());
            result.Show(
                Loc.Get(outcome.Success ? "Test_SuccessTitle" : "Test_FailureTitle"),
                outcome.Message ?? string.Empty,
                outcome.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            result.Show(Loc.Get("Test_FailureTitle"), ServiceErrors.Describe(ex), InfoBarSeverity.Error);
        }
        finally
        {
            SetTesting(target, false);
            IsBusy = false;
        }
    }

    private void Revert()
    {
        if (_snapshot is { } snapshot)
        {
            Apply(snapshot);
        }

        Feedback.Close();
    }

    private void Apply(SettingsSnapshot snapshot)
    {
        _snapshot = snapshot;
        _isApplying = true;
        try
        {
            MqttHost = snapshot.Mqtt.Host;
            MqttPort = snapshot.Mqtt.Port;
            MqttUseTls = snapshot.Mqtt.UseTls;
            MqttUsername = snapshot.Mqtt.Username;
            MqttPassword = string.Empty;
            ClearMqttPassword = false;
            MqttDeviceId = snapshot.Mqtt.DeviceId;
            MqttDeviceName = snapshot.Mqtt.DeviceName;
            MqttDiscoveryPrefix = snapshot.Mqtt.DiscoveryPrefix;
            MqttBaseTopic = snapshot.Mqtt.BaseTopic;
            HomeAssistantUrl = snapshot.HomeAssistant.BaseUrl;
            AccessToken = string.Empty;
            ClearAccessToken = false;
            HomeAssistantDeviceId = snapshot.HomeAssistant.DeviceId;
            HomeAssistantDeviceName = snapshot.HomeAssistant.DeviceName;
            CommandEventType = snapshot.HomeAssistant.CommandEventType;
            _disabledEntities.Clear();
            _disabledEntities.UnionWith(snapshot.DisabledEntities);
        }
        finally
        {
            _isApplying = false;
        }

        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasMqttPassword));
        OnPropertyChanged(nameof(HasAccessToken));
        OnPropertyChanged(nameof(MqttPasswordPlaceholder));
        OnPropertyChanged(nameof(AccessTokenPlaceholder));
        DisabledEntitiesChanged?.Invoke(this, EventArgs.Empty);
        UpdateDirty();
        RefreshCommands();
    }

    private SettingsUpdate BuildUpdate() => new(
        new MqttSettings(
            MqttHost.Trim(),
            (int)Math.Round(MqttPort ?? DefaultMqttPort),
            MqttUseTls,
            MqttUsername.Trim(),
            MqttDeviceId.Trim(),
            MqttDeviceName.Trim(),
            MqttDiscoveryPrefix.Trim().Trim('/'),
            MqttBaseTopic.Trim().Trim('/')),
        SecretUpdateFor(MqttPassword, ClearMqttPassword),
        new HomeAssistantSettings(
            HomeAssistantUrl.Trim(),
            HomeAssistantDeviceId.Trim(),
            HomeAssistantDeviceName.Trim(),
            CommandEventType.Trim()),
        SecretUpdateFor(AccessToken, ClearAccessToken),
        [.. _disabledEntities.Order(StringComparer.Ordinal)]);

    private static SecretUpdate SecretUpdateFor(string typed, bool clear) =>
        typed.Length > 0 ? new SecretUpdate(SecretChange.Replace, typed)
        : clear ? new SecretUpdate(SecretChange.Clear)
        : SecretUpdate.Unchanged;

    /// <param name="target">The connection being tested, or <see langword="null"/> to check everything before saving.</param>
    private string? Validate(ConnectionTarget? target)
    {
        if (target is null or ConnectionTarget.Mqtt)
        {
            var host = MqttHost.Trim();
            if (target == ConnectionTarget.Mqtt && host.Length == 0)
            {
                return Loc.Get("Validation_HostRequired");
            }

            if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                return Loc.Get("Validation_Host");
            }

            if (MqttPort is not { } port || port is < 1 or > 65535 || port != Math.Floor(port))
            {
                return Loc.Get("Validation_Port");
            }

            if (!IsTopicPrefix(MqttDiscoveryPrefix) || !IsTopicPrefix(MqttBaseTopic))
            {
                return Loc.Get("Validation_Topic");
            }
        }

        if (target is null or ConnectionTarget.HomeAssistant)
        {
            var url = HomeAssistantUrl.Trim();
            if (target == ConnectionTarget.HomeAssistant && url.Length == 0)
            {
                return Loc.Get("Validation_UrlRequired");
            }

            if (url.Length > 0
                && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)))
            {
                return Loc.Get("Validation_Url");
            }

            var eventType = CommandEventType.Trim();
            if (eventType.Length == 0 || eventType.Any(char.IsWhiteSpace))
            {
                return Loc.Get("Validation_EventType");
            }
        }

        return null;
    }

    private static bool IsTopicPrefix(string value)
    {
        var trimmed = value.Trim().Trim('/');
        return trimmed.Length > 0 && !trimmed.Any(c => char.IsWhiteSpace(c) || c is '+' or '#');
    }

    private void SetSetting<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            UpdateDirty();
        }
    }

    private void UpdateDirty()
    {
        if (!_isApplying)
        {
            IsDirty = _snapshot is not null && !Matches(BuildUpdate(), _snapshot);
        }
    }

    private static bool Matches(SettingsUpdate update, SettingsSnapshot snapshot) =>
        update.Mqtt == snapshot.Mqtt
        && update.HomeAssistant == snapshot.HomeAssistant
        && update.MqttPassword.Change == SecretChange.Keep
        && update.AccessToken.Change == SecretChange.Keep
        && update.DisabledEntities.SequenceEqual(snapshot.DisabledEntities.Order(StringComparer.Ordinal));

    private void SetTesting(ConnectionTarget target, bool isTesting)
    {
        if (target == ConnectionTarget.Mqtt)
        {
            IsTestingMqtt = isTesting;
        }
        else
        {
            IsTestingHomeAssistant = isTesting;
        }
    }

    private void RefreshCommands()
    {
        _saveCommand.RaiseCanExecuteChanged();
        _revertCommand.RaiseCanExecuteChanged();
        _testMqttCommand.RaiseCanExecuteChanged();
        _testHomeAssistantCommand.RaiseCanExecuteChanged();
    }
}
