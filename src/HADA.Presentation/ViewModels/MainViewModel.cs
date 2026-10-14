using System.Windows.Input;
using HADA.Ipc;
using HADA.Presentation;
using HADA.Presentation.Localization;
using HADA.Presentation.Mvvm;

namespace HADA.Presentation.ViewModels;

/// <summary>Root of the settings window: polls the service and feeds every page.</summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ServiceControlClient _client;
    private readonly CancellationTokenSource _stopping = new();
    private bool _isRefreshing;
    private bool _isServiceReachable;

    /// <param name="desktop">What comes from the system and the toolkit the window is shown with.</param>
    /// <param name="requestElevation">Restarts the window as administrator; returns <see langword="false"/> if the user declined.</param>
    /// <param name="isElevated">Whether this is the administrator copy of the window, opened with "Unlock editing".</param>
    /// <param name="isPortable">
    /// Whether this is a portable copy, whose settings belong to the user: they can be changed without an
    /// administrator window, and it is updated by replacing its folders, not by an installer.
    /// </param>
    public MainViewModel(ServiceControlClient client, IDesktop desktop, bool isElevated, bool isPortable, Func<bool> requestElevation)
    {
        _client = client;
        IsElevated = isElevated;
        WindowTitle = Loc.Get(isElevated ? "App_TitleElevated" : "App_Title");
        Overview = new OverviewViewModel(target => NavigationRequested?.Invoke(target));
        Updates = new UpdateViewModel(client, desktop, isElevated, isPortable);
        Preferences = new PreferencesViewModel(desktop, isElevated, isPortable);
        Settings = new SettingsViewModel(client, desktop, isElevated || isPortable);

        // Each Home Assistant connected to directly has a dashboard, a personal setting that follows the servers.
        Settings.SnapshotApplied += (_, _) => Preferences.SyncDashboards(Settings.SavedHomeAssistantServers);
        Entities = new EntitiesViewModel(Settings);
        Connections = new ConnectionsViewModel(Settings, Overview);
        Logs = new LogsViewModel(client, desktop);
        UnlockCommand = new RelayCommand(
            () =>
            {
                if (!requestElevation())
                {
                    Settings.Feedback.Show(Loc.Get("Settings_LockedTitle"), Loc.Get("Settings_ElevationDeclined"), Severity.Warning);
                }
            },
            () => !isElevated && !isPortable);
    }

    public string WindowTitle { get; }

    public bool IsElevated { get; }

    public bool IsServiceReachable
    {
        get => _isServiceReachable;
        private set => SetProperty(ref _isServiceReachable, value);
    }

    /// <summary>
    /// Asks the window to show a page, optionally at one of its sections: <c>connections</c>,
    /// <c>connections#ha</c>, <c>connections#ha=</c> or <c>connections#mqtt=</c> and a server's id, <c>entities</c>,
    /// <c>logs</c>.
    /// </summary>
    public event Action<string>? NavigationRequested;

    public OverviewViewModel Overview { get; }

    public UpdateViewModel Updates { get; }

    public PreferencesViewModel Preferences { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>The Connections page as the window for every system shows it: a list, a page per server, and adding one.</summary>
    public ConnectionsViewModel Connections { get; }

    public EntitiesViewModel Entities { get; }

    public LogsViewModel Logs { get; }

    public ICommand UnlockCommand { get; }

    /// <summary>Refreshes now and every two seconds after, on the thread this is called on, until disposed.</summary>
    public async Task StartAsync()
    {
        await RefreshAsync();
        _ = RefreshPeriodicallyAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _stopping.Dispose();
        await _client.DisposeAsync();
    }

    private async Task RefreshPeriodicallyAsync(CancellationToken cancellationToken)
    {
        // Awaited where it was started, so every refresh runs on the window's thread, as a toolkit's timer would.
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var status = await _client.GetStatusAsync();
            IsServiceReachable = true;
            Overview.Update(status);
            Updates.Update(status);
            Entities.Update(status);
            Logs.OnServiceStarted(status.StartedAt);

            // Also covers the service starting after the window was opened.
            if (!Settings.IsLoaded)
            {
                await Settings.LoadAsync();
            }

            await Logs.RefreshAsync();
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            IsServiceReachable = false;
            Overview.SetUnavailable();
        }
        finally
        {
            _isRefreshing = false;
        }
    }
}
