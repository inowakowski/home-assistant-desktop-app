using System.Collections.ObjectModel;
using System.Windows.Input;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Presentation.Localization;
using HADA.Presentation.Mvvm;

namespace HADA.Presentation.ViewModels;

/// <summary>What the Connections page shows: the list, one server, or adding one.</summary>
public enum ConnectionsScreen
{
    List,
    Mqtt,
    HomeAssistant,
    Add,
}

/// <summary>One way of connecting, offered when a connection is added, with what it gives and needs in plain words.</summary>
public sealed class WayOptionViewModel(string id, Action<WayOptionViewModel> pick) : ObservableObject
{
    private bool _isSelected;

    public string Id { get; } = id;

    public string Title { get; } = Loc.Get($"Add_Way_{id}_Title");

    public string Badge { get; } = Loc.Get($"Add_Way_{id}_Badge");

    public string Gives { get; } = Loc.Get($"Add_Way_{id}_Gives");

    public string Needs { get; } = Loc.Format("Add_Needs", Loc.Get($"Add_Way_{id}_Needs"));

    public ICommand PickCommand => new RelayCommand(() => pick(this));

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// The Connections page: every server in one list, grouped by the way it connects; one server's settings on a page
/// of their own; and adding a server in three steps: how, where, and a check. Edits go into the same settings as
/// everywhere else in the window, and are saved together.
/// </summary>
public sealed class ConnectionsViewModel : ObservableObject
{
    private const string WayIntegration = "integration";
    private const string WayDirect = "direct";
    private const string WayMqtt = "mqtt";

    private readonly OverviewViewModel _overview;
    private readonly RelayCommand _addCommand;
    private readonly RelayCommand _nextCommand;
    private readonly AsyncCommand _checkCommand;
    private readonly AsyncCommand _addAnywayCommand;
    private ConnectionsScreen _screen;
    private MqttServerViewModel? _mqtt;
    private HomeAssistantServerViewModel? _homeAssistant;
    private WayOptionViewModel? _way;
    private string? _waiting;
    private int _step = 1;
    private bool _checkFailed;
    private bool _sendEntities;
    private bool _added;
    private string _stateText = string.Empty;
    private string _stateDetail = string.Empty;
    private StatusKind _stateKind;

    public ConnectionsViewModel(SettingsViewModel settings, OverviewViewModel overview)
    {
        Settings = settings;
        _overview = overview;
        Ways = [new(WayIntegration, Pick), new(WayDirect, Pick), new(WayMqtt, Pick)];

        _addCommand = new RelayCommand(StartAdding, () => Settings.CanEdit && !Settings.IsBusy);
        BackCommand = new RelayCommand(ShowList);
        DeleteCommand = new RelayCommand(Delete, () => Settings.CanEdit && !Settings.IsBusy);
        TestCommand = new AsyncCommand(TestAsync, () => Settings.CanEdit && !Settings.IsBusy);
        _nextCommand = new RelayCommand(Next, () => _way is not null);
        StepBackCommand = new RelayCommand(StepBack);
        CancelCommand = new RelayCommand(CancelAdding);
        _checkCommand = new AsyncCommand(() => AddAsync(test: true), () => Settings.CanEdit && !Settings.IsBusy);
        _addAnywayCommand = new AsyncCommand(() => AddAsync(test: false), () => Settings.CanEdit && !Settings.IsBusy);
        FinishCommand = new RelayCommand(ShowList);

        settings.SnapshotApplied += (_, _) =>
        {
            if (_waiting is { } section)
            {
                _waiting = null;
                Show(section);
            }
            else
            {
                Refresh();
            }
        };
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.IsDirty))
            {
                OnPropertyChanged(nameof(ShowsSaveBar));
            }

            if (e.PropertyName is nameof(SettingsViewModel.CanEdit) or nameof(SettingsViewModel.IsBusy))
            {
                _addCommand.RaiseCanExecuteChanged();
                _checkCommand.RaiseCanExecuteChanged();
                _addAnywayCommand.RaiseCanExecuteChanged();
                ((RelayCommand)DeleteCommand).RaiseCanExecuteChanged();
                ((AsyncCommand)TestCommand).RaiseCanExecuteChanged();
            }
        };
        overview.StatesChanged += (_, _) => Refresh();
    }

    public SettingsViewModel Settings { get; }

    /// <summary>The servers, one group per way of connecting; a way without servers has no group.</summary>
    public ObservableCollection<ConnectionGroupViewModel> Groups { get; } = [];

    public bool HasNoConnections => Groups.Count == 0;

    public ConnectionsScreen Screen
    {
        get => _screen;
        private set
        {
            if (SetProperty(ref _screen, value))
            {
                OnPropertyChanged(nameof(IsList));
                OnPropertyChanged(nameof(IsMqtt));
                OnPropertyChanged(nameof(IsHomeAssistant));
                OnPropertyChanged(nameof(IsDetail));
                OnPropertyChanged(nameof(IsAdding));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(ShowsSaveBar));
            }
        }
    }

    /// <summary>Whether to offer saving the changes: not while adding, which saves the new connection itself.</summary>
    public bool ShowsSaveBar => Settings.IsDirty && !IsAdding;

    public bool IsList => Screen == ConnectionsScreen.List;

    public bool IsMqtt => Screen == ConnectionsScreen.Mqtt;

    public bool IsHomeAssistant => Screen == ConnectionsScreen.HomeAssistant;

    public bool IsDetail => IsMqtt || IsHomeAssistant;

    public bool IsAdding => Screen == ConnectionsScreen.Add;

    /// <summary>The page's title: the server's name on its own page.</summary>
    public string Title => Screen switch
    {
        ConnectionsScreen.Mqtt => Mqtt?.DisplayName ?? string.Empty,
        ConnectionsScreen.HomeAssistant => HomeAssistant?.DisplayName ?? string.Empty,
        ConnectionsScreen.Add => Loc.Get("Add_Title"),
        _ => Loc.Get("Nav_Connections"),
    };

    /// <summary>The MQTT server whose page is open, or that is being added.</summary>
    public MqttServerViewModel? Mqtt
    {
        get => _mqtt;
        private set => SetProperty(ref _mqtt, value);
    }

    /// <summary>The Home Assistant whose page is open, or that is being added.</summary>
    public HomeAssistantServerViewModel? HomeAssistant
    {
        get => _homeAssistant;
        private set => SetProperty(ref _homeAssistant, value);
    }

    /// <summary>How the open server is doing, as the service last told.</summary>
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string StateDetail
    {
        get => _stateDetail;
        private set
        {
            if (SetProperty(ref _stateDetail, value))
            {
                OnPropertyChanged(nameof(HasStateDetail));
            }
        }
    }

    public bool HasStateDetail => StateDetail.Length > 0;

    public StatusKind StateKind
    {
        get => _stateKind;
        private set => SetProperty(ref _stateKind, value);
    }

    /// <summary>How the open Home Assistant is used: with the integration, as a mobile app, for notifications only.</summary>
    public string Way => Screen == ConnectionsScreen.Mqtt ? Loc.Get("Card_Mqtt") : HomeAssistant is { } server ? WayOf(server) : string.Empty;

    public ICommand AddCommand => _addCommand;

    public ICommand BackCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand TestCommand { get; }

    // Adding a connection.
    public IReadOnlyList<WayOptionViewModel> Ways { get; }

    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsStep1));
                OnPropertyChanged(nameof(IsStep2));
                OnPropertyChanged(nameof(IsStep3));
            }
        }
    }

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public bool IsStep3 => Step == 3;

    public bool AddsMqtt => _way?.Id == WayMqtt;

    public bool AddsHomeAssistant => _way is { Id: WayIntegration or WayDirect };

    /// <summary>
    /// Whether to ask if this Home Assistant should get the computer's entities too. Asked when MQTT sends them
    /// already: Home Assistant would otherwise have them twice.
    /// </summary>
    public bool AsksAboutEntities => AddsHomeAssistant && Settings.ConfiguredMqttServers.Any();

    public bool SendEntities
    {
        get => _sendEntities;
        set
        {
            if (SetProperty(ref _sendEntities, value) && HomeAssistant is { } server && IsAdding)
            {
                server.SensorMode = value ? SensorModeOf(_way) : HomeAssistantSensorMode.Off;
            }
        }
    }

    /// <summary>The check in the second step failed; the connection can still be added, and HADA keeps trying.</summary>
    public bool CheckFailed
    {
        get => _checkFailed;
        private set => SetProperty(ref _checkFailed, value);
    }

    /// <summary>What was added and what it gives, for the last step.</summary>
    public IReadOnlyList<string> Results { get; private set; } = [];

    /// <summary>The last step's heading: added and working, or added and still trying.</summary>
    public string ResultTitle => Loc.Get(CheckFailed ? "Add_DoneTrying" : "Add_DoneWorking");

    public ICommand NextCommand => _nextCommand;

    public ICommand StepBackCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand CheckCommand => _checkCommand;

    public ICommand AddAnywayCommand => _addAnywayCommand;

    public ICommand FinishCommand { get; }

    /// <summary>
    /// Opens what a link names: <c>mqtt=</c> or <c>ha=</c> and a server's id for its page, <c>add</c> for adding one,
    /// anything else for the list.
    /// </summary>
    public void Show(string? section)
    {
        // Opened before the settings were read, as when the window starts on a server: open it once they are.
        if (!Settings.IsLoaded && !string.IsNullOrEmpty(section))
        {
            _waiting = section;
            ShowList();
            return;
        }

        // Whichever screen this opens, a connection begun and not added goes.
        Leave();
        var (kind, id) = section?.Split('=', 2) is [var k, var i] ? (k, i) : (section ?? string.Empty, string.Empty);
        if (kind == "mqtt" && Settings.ConfiguredMqttServers.FirstOrDefault(server => server.Id == id) is { } mqtt)
        {
            OpenMqtt(mqtt);
        }
        else if (kind == "ha" && Settings.ConfiguredHomeAssistantServers.FirstOrDefault(server => server.Id == id) is { } homeAssistant)
        {
            OpenHomeAssistant(homeAssistant);
        }
        else if (kind == "add" && Settings.CanEdit)
        {
            StartAdding();
        }
        else
        {
            ShowList();
        }
    }

    /// <summary>Called as the page is left: a connection begun in the wizard and not added is taken back.</summary>
    public void Leave()
    {
        _waiting = null;
        if (IsAdding)
        {
            DropUnfinished();
        }
    }

    /// <summary>Brings the list and the open server's state up to date with the settings and the service.</summary>
    public void Refresh()
    {
        var groups = new List<(ConnectionWay Way, List<(string Id, string Name, string Detail)> Servers)>();
        var mqtt = Settings.ConfiguredMqttServers.Select(server => (server.Id, server.DisplayName, MqttAddress.Parse(server.Host).Host)).ToList();
        if (mqtt.Count > 0)
        {
            groups.Add((ConnectionWay.Mqtt, mqtt));
        }

        var homeAssistants = Settings.ConfiguredHomeAssistantServers
            .Select(server => (server.Id, server.DisplayName, $"{server.Url.Trim()} · {WayOf(server)}"))
            .ToList();
        if (homeAssistants.Count > 0)
        {
            groups.Add((ConnectionWay.HomeAssistant, homeAssistants));
        }

        CollectionSync.Sync(
            Groups,
            groups,
            group => group.Way.ToString(),
            group => group.Key,
            key => new ConnectionGroupViewModel(Enum.Parse<ConnectionWay>(key)),
            (group, item) => CollectionSync.Sync(
                group.Rows,
                item.Servers,
                server => server.Id,
                row => row.Id,
                id => new ConnectionRowViewModel(id, () => Show($"{(group.IsMqtt ? "mqtt" : "ha")}={id}")),
                (row, server) =>
                {
                    var (text, kind, _) = StateOf(group.Way, server.Id);
                    row.Set(server.Name, server.Detail, text, kind);
                }));
        OnPropertyChanged(nameof(HasNoConnections));

        if (Screen is ConnectionsScreen.Mqtt && Mqtt is { } open)
        {
            ShowState(ConnectionWay.Mqtt, open.Id);
        }
        else if (Screen is ConnectionsScreen.HomeAssistant && HomeAssistant is { } openHomeAssistant)
        {
            ShowState(ConnectionWay.HomeAssistant, openHomeAssistant.Id);
        }
        else if (Screen is ConnectionsScreen.Add && IsStep3)
        {
            if (Mqtt is { } added)
            {
                ShowState(ConnectionWay.Mqtt, added.Id);
            }
            else if (HomeAssistant is { } addedHomeAssistant)
            {
                ShowState(ConnectionWay.HomeAssistant, addedHomeAssistant.Id);
            }
        }
    }

    private static string WayOf(HomeAssistantServerViewModel server) => Loc.Get(server.SensorMode switch
    {
        HomeAssistantSensorMode.Integration => "Connections_WayIntegration",
        HomeAssistantSensorMode.Entities => "Connections_WayEntities",
        HomeAssistantSensorMode.States => "Connections_WayStates",
        _ => server.Notifications ? "Connections_WayNotifications" : "Connections_WayNothing",
    });

    private static HomeAssistantSensorMode SensorModeOf(WayOptionViewModel? way) =>
        way?.Id == WayIntegration ? HomeAssistantSensorMode.Integration : HomeAssistantSensorMode.Entities;

    private (string Text, StatusKind Kind, string Detail) StateOf(ConnectionWay way, string id) =>
        _overview.StateOf(way, id)
        ?? (Settings.IsSaved(id) ? ("—", StatusKind.Neutral, string.Empty) : (Loc.Get("Connections_NotSaved"), StatusKind.Neutral, string.Empty));

    private void ShowState(ConnectionWay way, string id)
    {
        (StateText, StateKind, StateDetail) = StateOf(way, id);
    }

    private void ShowList()
    {
        Screen = ConnectionsScreen.List;
        Mqtt = null;
        HomeAssistant = null;
        Refresh();
    }

    private void OpenMqtt(MqttServerViewModel server)
    {
        Settings.SelectedMqttServer = server;
        HomeAssistant = null;
        Mqtt = server;
        Screen = ConnectionsScreen.Mqtt;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Way));
        ShowState(ConnectionWay.Mqtt, server.Id);
    }

    private void OpenHomeAssistant(HomeAssistantServerViewModel server)
    {
        Settings.SelectedHomeAssistantServer = server;
        Mqtt = null;
        HomeAssistant = server;
        Screen = ConnectionsScreen.HomeAssistant;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Way));
        ShowState(ConnectionWay.HomeAssistant, server.Id);
    }

    private Task TestAsync() => Mqtt is { } mqtt ? Settings.TestAsync(mqtt)
        : HomeAssistant is { } homeAssistant ? Settings.TestAsync(homeAssistant)
        : Task.CompletedTask;

    /// <summary>Takes the open server out of the settings, back on the list; saving removes it from the service.</summary>
    private void Delete()
    {
        if (Mqtt is { } mqtt)
        {
            Settings.Remove(mqtt);
        }
        else if (HomeAssistant is { } homeAssistant)
        {
            Settings.Remove(homeAssistant);
        }

        ShowList();
    }

    private void StartAdding()
    {
        Mqtt = null;
        HomeAssistant = null;
        Pick(null);
        CheckFailed = false;
        _added = false;
        Step = 1;
        Screen = ConnectionsScreen.Add;
    }

    private void Pick(WayOptionViewModel? way)
    {
        _way = way;
        foreach (var option in Ways)
        {
            option.IsSelected = option == way;
        }

        OnPropertyChanged(nameof(AddsMqtt));
        OnPropertyChanged(nameof(AddsHomeAssistant));
        _nextCommand.RaiseCanExecuteChanged();
    }

    /// <summary>From the way to the address: the new server is made here, ready to type into.</summary>
    private void Next()
    {
        if (_way is null)
        {
            return;
        }

        DropUnfinished();
        if (_way.Id == WayMqtt)
        {
            Mqtt = Settings.StartMqttServer();
        }
        else
        {
            _sendEntities = !Settings.ConfiguredMqttServers.Any();
            OnPropertyChanged(nameof(SendEntities));
            HomeAssistant = Settings.StartHomeAssistantServer(_sendEntities ? SensorModeOf(_way) : HomeAssistantSensorMode.Off);
        }

        OnPropertyChanged(nameof(AsksAboutEntities));
        CheckFailed = false;
        Settings.MqttTest.Close();
        Settings.HomeAssistantTest.Close();
        Step = 2;
    }

    private void StepBack()
    {
        DropUnfinished();
        Step = 1;
    }

    private void CancelAdding()
    {
        DropUnfinished();
        ShowList();
    }

    /// <summary>Takes back a server that was begun and not added, so it is not saved with something else.</summary>
    private void DropUnfinished()
    {
        if (_added)
        {
            return;
        }

        if (Mqtt is { } mqtt && !Settings.IsSaved(mqtt.Id))
        {
            Settings.Remove(mqtt);
        }

        if (HomeAssistant is { } homeAssistant && !Settings.IsSaved(homeAssistant.Id))
        {
            Settings.Remove(homeAssistant);
        }

        Mqtt = null;
        HomeAssistant = null;
    }

    /// <summary>Checks the new server and saves it; with <paramref name="test"/> off, saves it whatever the check said.</summary>
    private async Task AddAsync(bool test)
    {
        // What saving would be refused for is said here, next to the buttons, before connecting at all.
        if (Settings.ProblemBeforeSaving() is { } problem)
        {
            (Mqtt is not null ? Settings.MqttTest : Settings.HomeAssistantTest).Show(Loc.Get("Validation_Title"), problem, Severity.Warning);
            CheckFailed = false;
            return;
        }

        if (test)
        {
            var works = Mqtt is { } mqtt ? await Settings.TestAsync(mqtt)
                : HomeAssistant is { } homeAssistant && await Settings.TestAsync(homeAssistant);
            if (!works)
            {
                CheckFailed = true;
                return;
            }

            CheckFailed = false;
        }

        if (!await Settings.SaveChangesAsync())
        {
            return;
        }

        // The last step says it was added, and how the connection is doing.
        Settings.Feedback.Close();
        _added = true;
        // A Home Assistant that gets no entities, as MQTT sends them already, gives notifications only.
        var results = HomeAssistant?.SensorMode == HomeAssistantSensorMode.Off ? "off" : _way?.Id;
        Results = [.. Enumerable.Range(1, 3).Select(line => Loc.TryGet($"Add_Way_{results}_Result{line}")).OfType<string>()];
        OnPropertyChanged(nameof(Results));
        OnPropertyChanged(nameof(ResultTitle));

        // Saving made new view models of the servers; follow the one that was added.
        if (Mqtt is { } addedMqtt)
        {
            Mqtt = Settings.MqttServers.FirstOrDefault(server => server.Id == addedMqtt.Id);
        }

        if (HomeAssistant is { } addedHomeAssistant)
        {
            HomeAssistant = Settings.HomeAssistantServers.FirstOrDefault(server => server.Id == addedHomeAssistant.Id);
        }

        Step = 3;
        Refresh();
    }
}
