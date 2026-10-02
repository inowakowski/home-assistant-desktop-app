using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Windows.Input;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Updates;
using HADA.Ipc;
using HADA.Tray.Localization;
using HADA.Tray.Mvvm;
using Wpf.Ui.Controls;

namespace HADA.Tray.ViewModels;

public sealed class OverviewViewModel : ObservableObject
{
    // Downloads have no time limit of their own: the installer is tens of megabytes, and connections differ.
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly AsyncCommand _installUpdateCommand;
    private readonly StatusCardViewModel _service = new(Loc.Get("Card_Service"), SymbolRegular.Server24);
    private readonly StatusCardViewModel _mqtt = new(Loc.Get("Card_Mqtt"), SymbolRegular.Router24);
    private readonly StatusCardViewModel _homeAssistant = new(Loc.Get("Card_HomeAssistant"), SymbolRegular.HomeCheckmark24);
    private readonly StatusCardViewModel _tray = new(Loc.Get("Card_Tray"), SymbolRegular.WindowApps24);
    private bool _hasNoEntities = true;
    private UpdateInfo? _update;
    private DownloadedInstaller? _installer;
    private string? _installerVersion;
    private string _updateDetail;
    private double _updateProgress;
    private bool _isDownloadingUpdate;

    /// <param name="isElevated">Whether this is the administrator copy of the window, opened with "Unlock editing".</param>
    public OverviewViewModel(bool isElevated)
    {
        Cards = [_service, _mqtt, _homeAssistant, _tray];
        CanChangeAutostart = !isElevated;
        AutostartDetail = Loc.Get(isElevated ? "Autostart_Elevated" : "Autostart_Detail");
        OpenUpdateCommand = new RelayCommand(OpenUpdatePage);
        CanInstallUpdate = !isElevated;
        _updateDetail = Loc.Get(isElevated ? "Update_DetailElevated" : "Update_Detail");
        _installUpdateCommand = new AsyncCommand(InstallUpdateAsync, () => CanInstallUpdate && _update is not null);
        SetUnavailable();
    }

    public bool IsUpdateAvailable => _update is not null;

    public string UpdateTitle => _update is { } update ? Loc.Format("Update_Title", update.Version) : string.Empty;

    /// <summary>
    /// An installer started from the administrator window would run as administrator from its first page, and so
    /// would the tray app it starts when it is done. Installing is therefore offered in the ordinary window only,
    /// where Windows asks for administrator rights at the step that needs them.
    /// </summary>
    public bool CanInstallUpdate { get; }

    public string UpdateDetail
    {
        get => _updateDetail;
        private set => SetProperty(ref _updateDetail, value);
    }

    /// <summary>Fraction of the installer downloaded, 0 to 1.</summary>
    public double UpdateProgress
    {
        get => _updateProgress;
        private set => SetProperty(ref _updateProgress, value);
    }

    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set => SetProperty(ref _isDownloadingUpdate, value);
    }

    public ICommand OpenUpdateCommand { get; }

    public ICommand InstallUpdateCommand => _installUpdateCommand;

    public IReadOnlyList<StatusCardViewModel> Cards { get; }

    /// <summary>
    /// Autostart is a per-user choice. The administrator window may run under another account, where the switch
    /// would change that account's choice instead of the user's, so it is read-only there.
    /// </summary>
    public bool CanChangeAutostart { get; }

    public string AutostartDetail { get; }

    public bool StartWithWindows
    {
        get => Autostart.IsEnabled;
        set
        {
            Autostart.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<EntityRowViewModel> Entities { get; } = [];

    public bool HasNoEntities
    {
        get => _hasNoEntities;
        private set => SetProperty(ref _hasNoEntities, value);
    }

    public void Update(ServiceStatus status)
    {
        _service.Set(
            Loc.Get("Status_Running"),
            StatusKind.Success,
            Loc.Format("Status_Version", status.Version.Split('+')[0], status.StartedAt.LocalDateTime));
        UpdateEngine(_mqtt, status.Engines.FirstOrDefault(engine => engine.Name == "mqtt"));
        UpdateEngine(_homeAssistant, status.Engines.FirstOrDefault(engine => engine.Name == "websocket"));

        var trayCount = status.SensorClients.Count;
        _tray.Set(
            trayCount > 0 ? Loc.Format("Tray_ClientsConnected", trayCount) : Loc.Get("Tray_NoClients"),
            trayCount > 0 ? StatusKind.Success : StatusKind.Warning,
            Loc.Get("Tray_ClientsDetail"));

        CollectionSync.Sync(
            Entities,
            status.Entities,
            entity => entity.Entity.Id,
            row => row.Id,
            id => new EntityRowViewModel(id),
            (row, entity) => row.Update(entity));
        HasNoEntities = Entities.Count == 0;

        if (_update != status.Update)
        {
            _update = status.Update;
            OnPropertyChanged(nameof(IsUpdateAvailable));
            OnPropertyChanged(nameof(UpdateTitle));
            _installUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task InstallUpdateAsync()
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

                IsDownloadingUpdate = true;
                UpdateProgress = 0;
                UpdateDetail = Loc.Format("Update_Downloading", 0);
                var progress = new Progress<double>(fraction =>
                {
                    // Reported for every block read; the text need only change with the whole percent.
                    if (IsDownloadingUpdate && Math.Floor(fraction * 100) > Math.Floor(UpdateProgress * 100))
                    {
                        UpdateProgress = fraction;
                        UpdateDetail = Loc.Format("Update_Downloading", Math.Floor(fraction * 100));
                    }
                });

                _installer = await UpdateDownloader.DownloadAsync(
                    Http,
                    HadaReleases.Downloads,
                    update.Version,
                    RuntimeInformation.OSArchitecture,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HADA", "updates"),
                    progress);
                _installerVersion = update.Version;
            }

            IsDownloadingUpdate = false;
            Process.Start(new ProcessStartInfo(_installer.Path) { UseShellExecute = true })?.Dispose();
            UpdateDetail = Loc.Get("Update_Started");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            IsDownloadingUpdate = false;
            UpdateDetail = Loc.Format("Update_Failed", ex.Message);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HADA", typeof(OverviewViewModel).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return http;
    }

    private void OpenUpdatePage()
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
            // Nothing to open it with; the address is in the title for the user to type.
        }
    }

    public void SetUnavailable()
    {
        _service.Set(Loc.Get("Status_NotRunning"), StatusKind.Error, Loc.Get("Status_NotRunningDetail"));
        foreach (var card in new[] { _mqtt, _homeAssistant, _tray })
        {
            card.Set("—", StatusKind.Neutral);
        }
    }

    private static void UpdateEngine(StatusCardViewModel card, EngineStatus? engine)
    {
        if (engine is null || !engine.IsConfigured)
        {
            card.Set(Loc.Get("State_NotConfigured"), StatusKind.Neutral, Loc.Get("State_NotConfiguredDetail"));
            return;
        }

        switch (engine.State)
        {
            case EngineConnectionState.Connected:
                card.Set(Loc.Get("State_Connected"), StatusKind.Success);
                break;
            case EngineConnectionState.Connecting:
                card.Set(Loc.Get("State_Connecting"), StatusKind.Warning);
                break;
            case EngineConnectionState.Faulted:
                card.Set(Loc.Get("State_Faulted"), StatusKind.Error, Loc.Get("State_FaultedDetail"));
                break;
            default:
                card.Set(Loc.Get("State_Disconnected"), StatusKind.Warning);
                break;
        }
    }
}

public sealed class StatusCardViewModel(string title, SymbolRegular symbol) : ObservableObject
{
    private string _text = string.Empty;
    private string _detail = string.Empty;
    private StatusKind _kind;

    public string Title { get; } = title;

    public SymbolRegular Symbol { get; } = symbol;

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public StatusKind Kind
    {
        get => _kind;
        private set => SetProperty(ref _kind, value);
    }

    public void Set(string text, StatusKind kind, string detail = "")
    {
        Text = text;
        Kind = kind;
        Detail = detail;
    }
}

public sealed class EntityRowViewModel(string id) : ObservableObject
{
    private string _name = string.Empty;
    private SymbolRegular _symbol;
    private string _caption = string.Empty;
    private string _valueText = string.Empty;
    private string _sourceText = string.Empty;
    private string _updatedText = string.Empty;
    private bool _isEnabled = true;

    public string Id { get; } = id;

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public SymbolRegular Symbol
    {
        get => _symbol;
        private set => SetProperty(ref _symbol, value);
    }

    public string Caption
    {
        get => _caption;
        private set => SetProperty(ref _caption, value);
    }

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetProperty(ref _sourceText, value);
    }

    public string UpdatedText
    {
        get => _updatedText;
        private set => SetProperty(ref _updatedText, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    public void Update(EntityStatus status)
    {
        var entity = status.Entity;
        Name = entity.Name;
        Symbol = EntityVisuals.SymbolFor(entity);
        IsEnabled = status.IsEnabled;
        Caption = $"{entity.Id} · {(status.IsEnabled ? EntityVisuals.KindText(entity) : Loc.Get("Entity_Disabled"))}";
        ValueText = status.IsAvailable ? EntityVisuals.FormatState(entity, status.State) : Loc.Get("Entity_Unavailable");
        SourceText = EntityVisuals.FormatSource(status.Source);
        UpdatedText = status.UpdatedAt is { } updated ? updated.ToLocalTime().ToString("T", Loc.Culture) : "—";
    }
}
