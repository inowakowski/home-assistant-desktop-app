using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Windows.Input;
using HADA.Core.Updates;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;

namespace HADA.Tray.ViewModels;

/// <summary>
/// What the window knows about newer versions: shown as a notice on the Overview page and in full on the
/// Settings page. Looking for one is the service's job; downloading and starting the installer is done here,
/// by the user who asked for it.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    // Downloads have no time limit of their own: the installer is tens of megabytes, and connections differ.
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ServiceControlClient _client;
    private readonly AsyncCommand _installCommand;
    private readonly AsyncCommand _checkNowCommand;
    private readonly string _idleDetail;
    private UpdateInfo? _update;
    private UpdateCheckResult? _lastCheck;
    private DownloadedInstaller? _installer;
    private string? _installerVersion;
    private string _detail;
    private string _installedVersion = "—";
    private string? _checkError;
    private double _progress;
    private bool _isDownloading;
    private bool _isChecking;

    /// <param name="isElevated">Whether this is the administrator copy of the window, opened with "Unlock editing".</param>
    /// <param name="isPortable">Whether this is a portable copy, which no installer can update.</param>
    public UpdateViewModel(ServiceControlClient client, bool isElevated, bool isPortable = false)
    {
        _client = client;
        CanInstall = !isElevated && !isPortable;
        _idleDetail = Loc.Get(isPortable ? "Update_DetailPortable" : isElevated ? "Update_DetailElevated" : "Update_Detail");
        _detail = _idleDetail;
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage);
        _installCommand = new AsyncCommand(InstallAsync, () => CanInstall && _update is not null);
        _checkNowCommand = new AsyncCommand(CheckNowAsync);
    }

    public bool IsAvailable => _update is not null;

    public string Title => _update is { } update ? Loc.Format("Update_Title", update.Version) : string.Empty;

    /// <summary>
    /// An installer started from the administrator window would run as administrator from its first page, and so
    /// would the tray app it starts when it is done. Installing is therefore offered in the ordinary window only,
    /// where Windows asks for administrator rights at the step that needs them.
    /// </summary>
    public bool CanInstall { get; }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>Fraction of the installer downloaded, 0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
            {
                OnPropertyChanged(nameof(CheckStatus));
            }
        }
    }

    public string InstalledVersionText => Loc.Format("Preferences_InstalledVersion", _installedVersion);

    /// <summary>How the last look for a newer version went, in words.</summary>
    public string CheckStatus =>
        IsChecking ? Loc.Get("Update_Checking")
        : _checkError is { } error ? Loc.Format("Update_CheckFailed", error)
        : _lastCheck switch
        {
            null => Loc.Get("Update_NotChecked"),
            { Outcome: UpdateCheckOutcome.UpToDate } check => Loc.Format("Update_UpToDate", check.CheckedAt.LocalDateTime),
            { Outcome: UpdateCheckOutcome.UpdateAvailable } check => Loc.Format("Update_Found", check.LatestVersion, check.CheckedAt.LocalDateTime),
            { Outcome: UpdateCheckOutcome.ReleasesHidden } check => Loc.Format("Update_Hidden", check.CheckedAt.LocalDateTime),
            var check => Loc.Format("Update_CheckFailed", check.Message),
        };

    public StatusKind CheckKind =>
        _checkError is not null ? StatusKind.Error
        : _lastCheck?.Outcome switch
        {
            UpdateCheckOutcome.UpToDate => StatusKind.Success,
            UpdateCheckOutcome.UpdateAvailable => StatusKind.Warning,
            UpdateCheckOutcome.Failed => StatusKind.Error,
            _ => StatusKind.Neutral,
        };

    public ICommand OpenReleasePageCommand { get; }

    public ICommand InstallCommand => _installCommand;

    public ICommand CheckNowCommand => _checkNowCommand;

    public void Update(ServiceStatus status)
    {
        var version = status.Version.Split('+')[0];
        if (_installedVersion != version)
        {
            _installedVersion = version;
            OnPropertyChanged(nameof(InstalledVersionText));
        }

        Apply(status.Update, status.LastUpdateCheck);
    }

    private void Apply(UpdateInfo? update, UpdateCheckResult? lastCheck)
    {
        if (_update != update)
        {
            _update = update;
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(Title));
            _installCommand.RaiseCanExecuteChanged();
        }

        if (_lastCheck != lastCheck)
        {
            _lastCheck = lastCheck;
            _checkError = null;
            OnPropertyChanged(nameof(CheckStatus));
            OnPropertyChanged(nameof(CheckKind));
        }
    }

    private async Task CheckNowAsync()
    {
        IsChecking = true;
        try
        {
            var result = await _client.CheckForUpdateAsync();
            Apply(
                result.Outcome switch
                {
                    UpdateCheckOutcome.UpdateAvailable => new UpdateInfo(result.LatestVersion!, result.Url!),
                    UpdateCheckOutcome.Failed => _update,
                    _ => null,
                },
                result);
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex) || ex is ServiceControlException)
        {
            _checkError = ServiceErrors.Describe(ex);
            OnPropertyChanged(nameof(CheckKind));
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task InstallAsync()
    {
        if (_update is not { } update)
        {
            return;
        }

        try
        {
            // Asked again after the installer was cancelled: what was downloaded and checked is still there.
            if (_installer is null || _installerVersion != update.Version)
            {
                _installer?.Dispose();
                _installer = null;

                IsDownloading = true;
                Progress = 0;
                Detail = Loc.Format("Update_Downloading", 0);
                var progress = new Progress<double>(fraction =>
                {
                    // Reported for every block read; the text need only change with the whole percent.
                    if (IsDownloading && Math.Floor(fraction * 100) > Math.Floor(Progress * 100))
                    {
                        Progress = fraction;
                        Detail = Loc.Format("Update_Downloading", Math.Floor(fraction * 100));
                    }
                });

                _installer = await UpdateDownloader.DownloadAsync(
                    Http,
                    HadaReleases.Downloads,
                    update.Version,
                    RuntimeInformation.OSArchitecture,
                    Path.Combine(TrayPaths.DataFolder, "updates"),
                    progress);
                _installerVersion = update.Version;
            }

            IsDownloading = false;
            Process.Start(new ProcessStartInfo(_installer.Path) { UseShellExecute = true })?.Dispose();
            Detail = Loc.Get("Update_Started");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            IsDownloading = false;
            Detail = Loc.Format("Update_Failed", ex.Message);
        }
    }

    private void OpenReleasePage()
    {
        // The address came from the service, which got it from GitHub; open nothing but HADA's own release pages.
        if (_update is not { } update
            || !update.Url.StartsWith(HadaReleases.Site.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(update.Url, UriKind.Absolute, out var page))
        {
            return;
        }

        try
        {
            // Through Explorer, so the browser runs as the user even when this window runs as administrator.
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { page.AbsoluteUri }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing to open it with; the version is in the title for the user to look up.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("HADA", typeof(UpdateViewModel).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return http;
    }
}
