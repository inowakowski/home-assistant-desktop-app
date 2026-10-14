using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Presentation;
using HADA.Presentation.Localization;
using HADA.Presentation.Mvvm;

namespace HADA.Presentation.ViewModels;

/// <summary>
/// Editable copy of the service's settings, shared by every page that edits them, so one save covers them all.
/// Secrets are never shown: an empty password or token field keeps the saved value.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>As many as the service takes.</summary>
    private const int MaxMqttServers = 8;

    /// <summary>As many as the service takes.</summary>
    private const int MaxHomeAssistantServers = 8;

    private readonly ServiceControlClient _client;
    private readonly HashSet<string> _disabledEntities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabledEntities = new(StringComparer.Ordinal);
    private readonly AsyncCommand _saveCommand;
    private readonly RelayCommand _revertCommand;
    private readonly AsyncCommand _testMqttCommand;
    private readonly AsyncCommand _testHomeAssistantCommand;
    private readonly RelayCommand _addCustomSensorCommand;
    private readonly RelayCommand _addMqttServerCommand;
    private readonly RelayCommand _removeMqttServerCommand;
    private readonly RelayCommand _addHomeAssistantServerCommand;
    private readonly RelayCommand _removeHomeAssistantServerCommand;

    private SettingsSnapshot? _snapshot;
    private bool _isApplying;
    private bool _isBusy;
    private bool _isDirty;
    private bool _isTestingMqtt;
    private bool _isTestingHomeAssistant;

    private MqttServerViewModel? _selectedMqttServer;
    private HomeAssistantServerViewModel? _selectedHomeAssistantServer;
    private bool _checkUpdatesAutomatically = true;
    private bool _includePrereleases;
    private readonly IDesktop _desktop;

    /// <param name="isElevated">
    /// Whether this window may change settings: the administrator window of the installed app, or any window of
    /// a portable copy.
    /// </param>
    public SettingsViewModel(ServiceControlClient client, IDesktop desktop, bool isElevated)
    {
        _client = client;
        _desktop = desktop;
        IsElevated = isElevated;
        _saveCommand = new AsyncCommand(SaveChangesAsync, () => CanEdit && IsDirty && !IsBusy);
        _revertCommand = new RelayCommand(Revert, () => IsDirty && !IsBusy);
        _testMqttCommand = new AsyncCommand(() => TestAsync(ConnectionTarget.Mqtt), () => CanEdit && !IsBusy && SelectedMqttServer is not null);
        _testHomeAssistantCommand = new AsyncCommand(
            () => TestAsync(ConnectionTarget.HomeAssistant), () => CanEdit && !IsBusy && SelectedHomeAssistantServer is not null);
        _addCustomSensorCommand = new RelayCommand(
            () => AddCustomSensor(new CustomSensorDefinition()),
            () => CanEdit && !IsBusy);
        _addMqttServerCommand = new RelayCommand(AddMqttServer, () => CanEdit && !IsBusy && MqttServers.Count < MaxMqttServers);
        _removeMqttServerCommand = new RelayCommand(RemoveMqttServer, () => CanEdit && !IsBusy && MqttServers.Count > 1);
        _addHomeAssistantServerCommand = new RelayCommand(
            AddHomeAssistantServer, () => CanEdit && !IsBusy && HomeAssistantServers.Count < MaxHomeAssistantServers);
        _removeHomeAssistantServerCommand = new RelayCommand(
            RemoveHomeAssistantServer, () => CanEdit && !IsBusy && HomeAssistantServers.Count > 1);
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

    /// <summary>The MQTT servers, one per Home Assistant; there is always at least one to type into.</summary>
    public ObservableCollection<MqttServerViewModel> MqttServers { get; } = [];

    /// <summary>The server the form on the Connections page shows.</summary>
    public MqttServerViewModel? SelectedMqttServer
    {
        get => _selectedMqttServer;
        set
        {
            if (SetProperty(ref _selectedMqttServer, value))
            {
                // The result of testing one server says nothing about another.
                MqttTest.Close();
                RefreshCommands();
            }
        }
    }

    public bool HasSeveralMqttServers => MqttServers.Count > 1;

    public bool HasOneMqttServer => !HasSeveralMqttServers;

    /// <summary>The Home Assistants connected to directly; there is always at least one to type into.</summary>
    public ObservableCollection<HomeAssistantServerViewModel> HomeAssistantServers { get; } = [];

    /// <summary>Raised whenever the settings as they are saved were read: when loaded, after saving, and when changes were discarded.</summary>
    public event EventHandler? SnapshotApplied;

    /// <summary>The MQTT servers as they are saved, not as they are being edited.</summary>
    public IReadOnlyList<MqttSettings> SavedMqttServers =>
        _snapshot is { } snapshot ? [.. snapshot.MqttServers.Select(server => server.Settings)] : [];

    /// <summary>The Home Assistants connected to directly as they are saved, not as they are being edited.</summary>
    public IReadOnlyList<HomeAssistantSettings> SavedHomeAssistantServers =>
        _snapshot is { } snapshot ? [.. snapshot.HomeAssistantServers.Select(server => server.Settings)] : [];

    /// <summary>The Home Assistant the form on the Connections page shows.</summary>
    public HomeAssistantServerViewModel? SelectedHomeAssistantServer
    {
        get => _selectedHomeAssistantServer;
        set
        {
            if (SetProperty(ref _selectedHomeAssistantServer, value))
            {
                // The result of testing one server says nothing about another.
                HomeAssistantTest.Close();
                RefreshCommands();
            }
        }
    }

    public bool HasSeveralHomeAssistantServers => HomeAssistantServers.Count > 1;

    public bool HasOneHomeAssistantServer => !HasSeveralHomeAssistantServers;

    public bool CheckUpdatesAutomatically { get => _checkUpdatesAutomatically; set => SetSetting(ref _checkUpdatesAutomatically, value); }

    public bool IncludePrereleases { get => _includePrereleases; set => SetSetting(ref _includePrereleases, value); }

    public ICommand SaveCommand => _saveCommand;

    public ICommand RevertCommand => _revertCommand;

    public ICommand TestMqttCommand => _testMqttCommand;

    public ICommand TestHomeAssistantCommand => _testHomeAssistantCommand;

    public ICommand AddCustomSensorCommand => _addCustomSensorCommand;

    public ICommand AddMqttServerCommand => _addMqttServerCommand;

    public ICommand RemoveMqttServerCommand => _removeMqttServerCommand;

    public ICommand AddHomeAssistantServerCommand => _addHomeAssistantServerCommand;

    public ICommand RemoveHomeAssistantServerCommand => _removeHomeAssistantServerCommand;

    public ObservableCollection<CustomSensorViewModel> CustomSensors { get; } = [];

    public bool HasNoCustomSensors => CustomSensors.Count == 0;

    /// <param name="enabledByDefault">False for entities that stay off until switched on, such as the shutdown button.</param>
    public bool IsEntityEnabled(string entityId, bool enabledByDefault) =>
        enabledByDefault ? !_disabledEntities.Contains(entityId) : _enabledEntities.Contains(entityId);

    public void SetEntityEnabled(string entityId, bool enabledByDefault, bool enabled)
    {
        // Each kind of entity is listed only where it departs from its default.
        var changed = enabledByDefault
            ? (enabled ? _disabledEntities.Remove(entityId) : _disabledEntities.Add(entityId))
            : (enabled ? _enabledEntities.Add(entityId) : _enabledEntities.Remove(entityId));
        if (changed)
        {
            UpdateDirty();
        }
    }

    /// <summary>Shows the server with that id in the form, e.g. after its card on the overview was clicked.</summary>
    public void ShowMqttServer(string id)
    {
        if (MqttServers.FirstOrDefault(server => server.Id == id) is { } server)
        {
            SelectedMqttServer = server;
        }
    }

    /// <summary>Shows the Home Assistant with that id in the form, e.g. after its card on the overview was clicked.</summary>
    public void ShowHomeAssistantServer(string id)
    {
        if (HomeAssistantServers.FirstOrDefault(server => server.Id == id) is { } server)
        {
            SelectedHomeAssistantServer = server;
        }
    }

    /// <summary>The MQTT servers there are, leaving out the empty one kept to type into.</summary>
    public IEnumerable<MqttServerViewModel> ConfiguredMqttServers => PendingMqttServers();

    /// <summary>The Home Assistants connected to directly, leaving out the empty one kept to type into.</summary>
    public IEnumerable<HomeAssistantServerViewModel> ConfiguredHomeAssistantServers => PendingHomeAssistantServers();

    /// <summary>Whether the server is one the service has, as opposed to one added since and not saved yet.</summary>
    public bool IsSaved(string serverId) =>
        _snapshot is { } snapshot
        && (snapshot.MqttServers.Any(server => server.Settings.Id == serverId)
            || snapshot.HomeAssistantServers.Any(server => server.Settings.Id == serverId));

    /// <summary>A new MQTT server to set up: the empty one kept to type into, else one more.</summary>
    public MqttServerViewModel StartMqttServer()
    {
        var server = MqttServers.FirstOrDefault(candidate => candidate.IsBlank && !IsSaved(candidate.Id));
        if (server is null)
        {
            server = MqttServerViewModel.New(UpdateDirty);
            MqttServers.Add(server);
            OnMqttServersChanged();
        }

        SelectedMqttServer = server;
        return server;
    }

    /// <summary>A new Home Assistant to connect to directly, sending what <paramref name="sensors"/> says.</summary>
    public HomeAssistantServerViewModel StartHomeAssistantServer(HomeAssistantSensorMode sensors)
    {
        var server = HomeAssistantServers.FirstOrDefault(candidate => candidate.IsBlank && !IsSaved(candidate.Id));
        if (server is null)
        {
            server = HomeAssistantServerViewModel.New(UpdateDirty, _desktop, sensors: true);
            HomeAssistantServers.Add(server);
            OnHomeAssistantServersChanged();
        }

        server.SensorMode = sensors;
        SelectedHomeAssistantServer = server;
        return server;
    }

    /// <summary>
    /// Takes a server out of the settings; saving removes it from the service. The last one leaves an empty one
    /// behind to type into, as there always is one.
    /// </summary>
    public void Remove(MqttServerViewModel server)
    {
        if (!MqttServers.Remove(server))
        {
            return;
        }

        if (MqttServers.Count == 0)
        {
            MqttServers.Add(MqttServerViewModel.New(UpdateDirty));
        }

        SelectedMqttServer = MqttServers[0];
        OnMqttServersChanged();
    }

    /// <inheritdoc cref="Remove(MqttServerViewModel)"/>
    public void Remove(HomeAssistantServerViewModel server)
    {
        if (!HomeAssistantServers.Remove(server))
        {
            return;
        }

        if (HomeAssistantServers.Count == 0)
        {
            HomeAssistantServers.Add(HomeAssistantServerViewModel.New(UpdateDirty, _desktop, sensors: !PendingMqttServers().Any()));
        }

        SelectedHomeAssistantServer = HomeAssistantServers[0];
        OnHomeAssistantServersChanged();
    }

    /// <summary>Tries the server as it is typed; the result is shown in <see cref="MqttTest"/>. True when it worked.</summary>
    public Task<bool> TestAsync(MqttServerViewModel server)
    {
        SelectedMqttServer = server;
        return TestAsync(ConnectionTarget.Mqtt);
    }

    /// <summary>Tries the Home Assistant as it is typed; the result is shown in <see cref="HomeAssistantTest"/>. True when it worked.</summary>
    public Task<bool> TestAsync(HomeAssistantServerViewModel server)
    {
        SelectedHomeAssistantServer = server;
        return TestAsync(ConnectionTarget.HomeAssistant);
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

    /// <summary>What saving would be refused for, as said before saving; <see langword="null"/> when nothing.</summary>
    public string? ProblemBeforeSaving() => Validate(target: null);

    /// <summary>Saves what was changed; the outcome is shown in <see cref="Feedback"/>. True when it was saved.</summary>
    public async Task<bool> SaveChangesAsync()
    {
        if (Validate(target: null) is { } error)
        {
            Feedback.Show(Loc.Get("Validation_Title"), error, Severity.Warning);
            return false;
        }

        IsBusy = true;
        Feedback.Close();
        try
        {
            var result = await _client.SaveSettingsAsync(BuildUpdate());
            if (!result.Success)
            {
                Feedback.Show(Loc.Get("Save_FailureTitle"), result.Message ?? string.Empty, Severity.Error);
                return false;
            }

            Apply(await _client.GetSettingsAsync());
            Feedback.Show(Loc.Get("Save_SuccessTitle"), Loc.Get("Save_SuccessMessage"), Severity.Success);
            return true;
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            Feedback.Show(Loc.Get("Save_FailureTitle"), ServiceErrors.Describe(ex), Severity.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> TestAsync(ConnectionTarget target)
    {
        var result = target == ConnectionTarget.Mqtt ? MqttTest : HomeAssistantTest;
        var mqttServer = target == ConnectionTarget.Mqtt ? SelectedMqttServer : null;
        var homeAssistantServer = target == ConnectionTarget.HomeAssistant ? SelectedHomeAssistantServer : null;
        if (Validate(target) is { } error)
        {
            result.Show(Loc.Get("Validation_Title"), error, Severity.Warning);
            return false;
        }

        SetTesting(target, true);
        IsBusy = true;
        result.Close();
        try
        {
            var outcome = await _client.TestConnectionAsync(
                target, BuildUpdate(mqttServer, homeAssistantServer), mqttServer?.Id ?? homeAssistantServer?.Id);
            result.Show(
                Loc.Get(outcome.Success ? "Test_SuccessTitle" : "Test_FailureTitle"),
                outcome.Message ?? string.Empty,
                outcome.Success ? Severity.Success : Severity.Error);
            return outcome.Success;
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            result.Show(Loc.Get("Test_FailureTitle"), ServiceErrors.Describe(ex), Severity.Error);
            return false;
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
            var selected = SelectedMqttServer?.Id;
            MqttServers.Clear();
            foreach (var server in snapshot.MqttServers)
            {
                MqttServers.Add(new MqttServerViewModel(server.Settings, server.HasPassword, UpdateDirty));
            }

            if (MqttServers.Count == 0)
            {
                MqttServers.Add(MqttServerViewModel.New(UpdateDirty));
            }

            SelectedMqttServer = MqttServers.FirstOrDefault(server => server.Id == selected) ?? MqttServers[0];

            var selectedHomeAssistant = SelectedHomeAssistantServer?.Id;
            HomeAssistantServers.Clear();
            foreach (var server in snapshot.HomeAssistantServers)
            {
                HomeAssistantServers.Add(new HomeAssistantServerViewModel(server.Settings, server.HasAccessToken, UpdateDirty, _desktop));
            }

            if (HomeAssistantServers.Count == 0)
            {
                HomeAssistantServers.Add(HomeAssistantServerViewModel.New(UpdateDirty, _desktop, sensors: !PendingMqttServers().Any()));
            }

            SelectedHomeAssistantServer =
                HomeAssistantServers.FirstOrDefault(server => server.Id == selectedHomeAssistant) ?? HomeAssistantServers[0];
            _disabledEntities.Clear();
            _disabledEntities.UnionWith(snapshot.DisabledEntities);
            _enabledEntities.Clear();
            _enabledEntities.UnionWith(snapshot.EnabledEntities);
            CheckUpdatesAutomatically = snapshot.Updates.CheckAutomatically;
            IncludePrereleases = snapshot.Updates.IncludePrereleases;
            CustomSensors.Clear();
            foreach (var sensor in snapshot.CustomSensors)
            {
                AddCustomSensor(sensor);
            }
        }
        finally
        {
            _isApplying = false;
        }

        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasSeveralMqttServers));
        OnPropertyChanged(nameof(HasOneMqttServer));
        OnPropertyChanged(nameof(HasSeveralHomeAssistantServers));
        OnPropertyChanged(nameof(HasOneHomeAssistantServer));
        OnPropertyChanged(nameof(HasNoCustomSensors));
        DisabledEntitiesChanged?.Invoke(this, EventArgs.Empty);
        SnapshotApplied?.Invoke(this, EventArgs.Empty);
        UpdateDirty();
        RefreshCommands();
    }

    /// <param name="alsoBlank">A server to send even if nothing is typed into it yet, because it is the one being tested.</param>
    /// <param name="alsoBlankHomeAssistant">Likewise, a Home Assistant that is being tested.</param>
    private SettingsUpdate BuildUpdate(MqttServerViewModel? alsoBlank = null, HomeAssistantServerViewModel? alsoBlankHomeAssistant = null) => new(
        [.. PendingMqttServers().Union(alsoBlank is null ? [] : [alsoBlank]).Select(server => server.ToUpdate())],
        [
            .. PendingHomeAssistantServers()
                .Union(alsoBlankHomeAssistant is null ? [] : [alsoBlankHomeAssistant])
                .Select(server => server.ToUpdate()),
        ],
        [.. _disabledEntities.Order(StringComparer.Ordinal)],
        [.. PendingCustomSensors()],
        [.. _enabledEntities.Order(StringComparer.Ordinal)],
        new UpdateSettings(CheckUpdatesAutomatically, IncludePrereleases));

    /// <summary>The servers as they would be saved. One that nothing was typed into is not a server yet.</summary>
    private IEnumerable<MqttServerViewModel> PendingMqttServers() => MqttServers.Where(server => !server.IsBlank);

    private void AddMqttServer()
    {
        var server = MqttServerViewModel.New(UpdateDirty);
        MqttServers.Add(server);
        SelectedMqttServer = server;
        OnMqttServersChanged();
    }

    private void RemoveMqttServer()
    {
        if (SelectedMqttServer is not { } server || MqttServers.Count < 2)
        {
            return;
        }

        var index = MqttServers.IndexOf(server);
        MqttServers.Remove(server);
        SelectedMqttServer = MqttServers[Math.Min(index, MqttServers.Count - 1)];
        OnMqttServersChanged();
    }

    private void OnMqttServersChanged()
    {
        OnPropertyChanged(nameof(HasSeveralMqttServers));
        OnPropertyChanged(nameof(HasOneMqttServer));
        UpdateDirty();
        RefreshCommands();
    }

    /// <summary>The Home Assistants as they would be saved. One that nothing was typed into is not a server yet.</summary>
    private IEnumerable<HomeAssistantServerViewModel> PendingHomeAssistantServers() =>
        HomeAssistantServers.Where(server => !server.IsBlank);

    private void AddHomeAssistantServer()
    {
        var server = HomeAssistantServerViewModel.New(UpdateDirty, _desktop, sensors: !PendingMqttServers().Any());
        HomeAssistantServers.Add(server);
        SelectedHomeAssistantServer = server;
        OnHomeAssistantServersChanged();
    }

    private void RemoveHomeAssistantServer()
    {
        if (SelectedHomeAssistantServer is not { } server || HomeAssistantServers.Count < 2)
        {
            return;
        }

        var index = HomeAssistantServers.IndexOf(server);
        HomeAssistantServers.Remove(server);
        SelectedHomeAssistantServer = HomeAssistantServers[Math.Min(index, HomeAssistantServers.Count - 1)];
        OnHomeAssistantServersChanged();
    }

    private void OnHomeAssistantServersChanged()
    {
        OnPropertyChanged(nameof(HasSeveralHomeAssistantServers));
        OnPropertyChanged(nameof(HasOneHomeAssistantServer));
        UpdateDirty();
        RefreshCommands();
    }

    /// <summary>The custom sensors as they would be saved. A row nothing was typed into is not a sensor yet.</summary>
    private IEnumerable<CustomSensorDefinition> PendingCustomSensors() =>
        CustomSensors.Select(sensor => sensor.ToDefinition()).Where(sensor => sensor.Name.Length > 0 || sensor.Value.Length > 0);

    private void AddCustomSensor(CustomSensorDefinition definition)
    {
        CustomSensors.Add(new CustomSensorViewModel(definition, UpdateDirty, RemoveCustomSensor, _desktop));
        OnCustomSensorsChanged();
    }

    private void RemoveCustomSensor(CustomSensorViewModel sensor)
    {
        if (CanEdit && CustomSensors.Remove(sensor))
        {
            OnCustomSensorsChanged();
        }
    }

    private void OnCustomSensorsChanged()
    {
        OnPropertyChanged(nameof(HasNoCustomSensors));
        UpdateDirty();
    }

    internal static SecretUpdate SecretUpdateFor(string typed, bool clear) =>
        typed.Length > 0 ? new SecretUpdate(SecretChange.Replace, typed)
        : clear ? new SecretUpdate(SecretChange.Clear)
        : SecretUpdate.Unchanged;

    /// <param name="target">The connection being tested, or <see langword="null"/> to check everything before saving.</param>
    private string? Validate(ConnectionTarget? target)
    {
        if (target == ConnectionTarget.Mqtt && SelectedMqttServer?.Validate(hostRequired: true) is { } testError)
        {
            return testError;
        }

        if (target is null)
        {
            var servers = PendingMqttServers().ToList();
            foreach (var server in servers)
            {
                if (server.Validate(hostRequired: false) is { } error)
                {
                    // The form shows one server at a time; show the one that is wrong.
                    SelectedMqttServer = server;
                    return servers.Count > 1 ? $"{server.DisplayName}: {error}" : error;
                }
            }

            if (servers.Count > 1 && servers.FirstOrDefault(server => server.Name.Trim().Length == 0) is { } unnamed)
            {
                SelectedMqttServer = unnamed;
                return Loc.Get("Validation_ServerName");
            }

            var twice = servers.GroupBy(server => server.Name.Trim(), StringComparer.CurrentCultureIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (twice is not null)
            {
                return Loc.Format("Validation_ServerNameTwice", twice.Key);
            }

            // The same computer twice on one broker, which the service refuses: the connections would throw each other off.
            var sameBroker = servers
                .Where(server => server.Host.Trim().Length > 0)
                .GroupBy(BrokerOf, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (sameBroker is not null)
            {
                return Loc.Format("Validation_SameBroker", sameBroker.First().DisplayName, sameBroker.Last().DisplayName);
            }
        }

        if (target == ConnectionTarget.HomeAssistant && SelectedHomeAssistantServer?.Validate(urlRequired: true) is { } homeAssistantTestError)
        {
            return homeAssistantTestError;
        }

        if (target is null)
        {
            var servers = PendingHomeAssistantServers().ToList();
            foreach (var server in servers)
            {
                if (server.Validate(urlRequired: false) is { } error)
                {
                    SelectedHomeAssistantServer = server;
                    return servers.Count > 1 ? $"{server.DisplayName}: {error}" : error;
                }
            }

            if (servers.Count > 1 && servers.FirstOrDefault(server => server.Name.Trim().Length == 0) is { } unnamed)
            {
                SelectedHomeAssistantServer = unnamed;
                return Loc.Get("Validation_HaServerName");
            }

            var twice = servers.GroupBy(server => server.Name.Trim(), StringComparer.CurrentCultureIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (twice is not null)
            {
                return Loc.Format("Validation_HaServerNameTwice", twice.Key);
            }

            // The same computer twice in one Home Assistant, which the service refuses: both would write the same entities.
            var sameHome = servers
                .Where(server => server.Url.Trim().Length > 0)
                .GroupBy(server => $"{server.Url.Trim().TrimEnd('/')} {DeviceOf(server.DeviceId)}", StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (sameHome is not null)
            {
                return Loc.Format("Validation_SameHomeAssistant", sameHome.First().DisplayName, sameHome.Last().DisplayName);
            }
        }

        // The service checks custom sensors in full; these two are the mistakes an unfinished row makes.
        if (target is null)
        {
            foreach (var sensor in PendingCustomSensors())
            {
                if (sensor.Name.Length == 0)
                {
                    return Loc.Get("Validation_CustomName");
                }

                // A quick action may do without a shortcut; everything else needs what it is about.
                if (sensor.Value.Length == 0 && sensor.Type != CustomSensorType.QuickAction)
                {
                    return Loc.Format("Validation_CustomValue", sensor.Name);
                }
            }
        }

        return null;
    }

    /// <summary>What the service tells MQTT servers apart by: the broker, as the host field names it, and the device.</summary>
    private static string BrokerOf(MqttServerViewModel server)
    {
        var address = MqttAddress.Parse(server.Host);
        var port = address.Port ?? (int)Math.Round(server.Port ?? MqttServerViewModel.DefaultPort);
        return $"{address.Host} {port} {DeviceOf(server.DeviceId)}";
    }

    // Without an id of its own, the computer is known by its name.
    private static string DeviceOf(string deviceId) => deviceId.Trim() is { Length: > 0 } id ? id : Environment.MachineName;

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
        update.MqttServers.Select(server => server.Settings).SequenceEqual(
            snapshot.MqttServers.Select(server => server.Settings).Where(server => server.Name.Length > 0 || server.Host.Length > 0))
        && update.MqttServers.All(server => server.Password.Change == SecretChange.Keep)
        && update.HomeAssistantServers.Select(server => server.Settings).SequenceEqual(
            snapshot.HomeAssistantServers.Select(server => server.Settings).Where(server => server.Name.Length > 0 || server.BaseUrl.Length > 0))
        && update.HomeAssistantServers.All(server => server.AccessToken.Change == SecretChange.Keep)
        && update.DisabledEntities.SequenceEqual(snapshot.DisabledEntities.Order(StringComparer.Ordinal))
        && update.EnabledEntities.SequenceEqual(snapshot.EnabledEntities.Order(StringComparer.Ordinal))
        && update.Updates == snapshot.Updates
        && update.CustomSensors.SequenceEqual(snapshot.CustomSensors);

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
        _addCustomSensorCommand.RaiseCanExecuteChanged();
        _addMqttServerCommand.RaiseCanExecuteChanged();
        _removeMqttServerCommand.RaiseCanExecuteChanged();
        _addHomeAssistantServerCommand.RaiseCanExecuteChanged();
        _removeHomeAssistantServerCommand.RaiseCanExecuteChanged();
    }
}
