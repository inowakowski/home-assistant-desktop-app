using System.Windows.Input;
using System.Windows.Threading;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;
using Wpf.Ui.Controls;

namespace HADA.Tray.ViewModels;

/// <summary>Root of the settings window: polls the service and feeds every page.</summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ServiceControlClient _client;
    private readonly DispatcherTimer _refreshTimer;
    private bool _isRefreshing;
    private bool _isServiceReachable;

    /// <param name="requestElevation">Restarts the window as administrator; returns <see langword="false"/> if the user declined.</param>
    public MainViewModel(ServiceControlClient client, bool isElevated, Func<bool> requestElevation)
    {
        _client = client;
        IsElevated = isElevated;
        WindowTitle = Loc.Get(isElevated ? "App_TitleElevated" : "App_Title");
        Overview = new OverviewViewModel();
        Settings = new SettingsViewModel(client, isElevated);
        Entities = new EntitiesViewModel(Settings);
        Logs = new LogsViewModel(client);
        UnlockCommand = new RelayCommand(
            () =>
            {
                if (!requestElevation())
                {
                    Settings.Feedback.Show(Loc.Get("Settings_LockedTitle"), Loc.Get("Settings_ElevationDeclined"), InfoBarSeverity.Warning);
                }
            },
            () => !isElevated);

        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    public string WindowTitle { get; }

    public bool IsElevated { get; }

    public bool IsServiceReachable
    {
        get => _isServiceReachable;
        private set => SetProperty(ref _isServiceReachable, value);
    }

    public OverviewViewModel Overview { get; }

    public SettingsViewModel Settings { get; }

    public EntitiesViewModel Entities { get; }

    public LogsViewModel Logs { get; }

    public ICommand UnlockCommand { get; }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _refreshTimer.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _refreshTimer.Stop();
        await _client.DisposeAsync();
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
