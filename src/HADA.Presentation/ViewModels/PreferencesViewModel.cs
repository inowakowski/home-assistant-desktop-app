using System.Collections.ObjectModel;
using HADA.Core;
using HADA.Ipc;
using HADA.Presentation.Localization;
using HADA.Presentation.Mvvm;

namespace HADA.Presentation.ViewModels;

public sealed record LanguageOption(string Code, string Label);

/// <param name="Path">The browser's program file; empty for whichever browser is the default.</param>
public sealed record BrowserOption(string Path, string Label);

/// <summary>
/// The dashboard of one Home Assistant connected to directly, on the Settings page: whether the tray icon's menu
/// offers it, and an address to show instead of the server's own. Applied the moment it is changed.
/// </summary>
public sealed class DashboardServerViewModel(ServerDashboard dashboard, Action<ServerDashboard> changed) : ObservableObject
{
    private ServerDashboard _dashboard = dashboard;

    public string Id => _dashboard.Id;

    /// <summary>The server's name, or the host of its address when it has none.</summary>
    public string Name =>
        _dashboard.Name.Length > 0 ? _dashboard.Name
        : Uri.TryCreate(_dashboard.Url, UriKind.Absolute, out var url) ? url.Host
        : _dashboard.Url;

    /// <summary>The address HADA connects to the server at: what is shown unless the user gives another.</summary>
    public string Url => _dashboard.Url;

    public bool Show
    {
        get => _dashboard.Show;
        set => Set(_dashboard with { Show = value });
    }

    /// <summary>Kept only when it is an http or https address; anything else clears it, and the server's own is shown.</summary>
    public string OwnUrl
    {
        get => _dashboard.OwnUrl;
        set => Set(_dashboard with { OwnUrl = Dashboards.TryGetAddress(value, out var address) ? address.AbsoluteUri : string.Empty });
    }

    private void Set(ServerDashboard dashboard)
    {
        if (dashboard == _dashboard)
        {
            // What was typed was not an address: the box goes back to what is kept.
            OnPropertyChanged(nameof(OwnUrl));
            return;
        }

        _dashboard = dashboard;
        changed(dashboard);
        OnPropertyChanged(nameof(Show));
        OnPropertyChanged(nameof(OwnUrl));
    }
}

/// <summary>
/// The Settings page's personal part: choices of the signed-in user, applied the moment they are made and
/// without administrator rights.
/// </summary>
/// <param name="isElevated">
/// Whether this is the administrator copy of the window. An administrator who only confirmed the UAC prompt is
/// still the same account there, and changes these as in the ordinary window. A standard user who typed an
/// administrator's name and password runs it as that administrator, where these switches would change that
/// account's choices instead of the user's, so they are read-only there.
/// </param>
/// <param name="isPortable">Whether this is a portable copy, where starting with Windows means something else.</param>
public sealed class PreferencesViewModel(IDesktop desktop, bool isElevated, bool isPortable = false) : ObservableObject
{
    private readonly string _languageAtStart = UserPreferences.Language;

    public bool CanChange { get; } = !isElevated || desktop.RunsAsSessionUser;

    public string Note => Loc.Get(CanChange ? "Preferences_PersonalDetail" : "Preferences_PersonalElevated");

    /// <summary>"Start with Windows" on Windows; elsewhere HADA starts when the user signs in.</summary>
    public string AutostartTitle { get; } = Loc.Get(OperatingSystem.IsWindows() ? "Autostart_Title" : "Autostart_TitleSignIn");

    public string AutostartDetail { get; } = Loc.Get(
        !OperatingSystem.IsWindows() ? "Autostart_DetailSession" : isPortable ? "Autostart_DetailPortable" : "Autostart_Detail");

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(string.Empty, Loc.Get("Preferences_LanguageAuto")),
        new("pl", "Polski"),
        new("en", "English"),
    ];

    /// <summary>Whichever browser is the default, and then the installed ones by name.</summary>
    public IReadOnlyList<BrowserOption> Browsers { get; } =
    [
        new(string.Empty, Loc.Get("Preferences_BrowserDefault")),
        .. desktop.InstalledBrowsers().Select(browser => new BrowserOption(browser.Path, browser.Name)),
    ];

    /// <summary>The program file of the browser that addresses of notifications are opened in; empty for the default one.</summary>
    public string NotificationBrowser
    {
        get => Browsers.Any(browser => string.Equals(browser.Path, UserPreferences.NotificationBrowser, StringComparison.OrdinalIgnoreCase))
            ? UserPreferences.NotificationBrowser
            : string.Empty;
        set
        {
            UserPreferences.NotificationBrowser = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public double MinIdleSeconds => UserPreferences.MinIdleSeconds;

    public double MaxIdleSeconds => UserPreferences.MaxIdleSeconds;

    public bool StartWithWindows
    {
        get => desktop.StartsWithSystem;
        set
        {
            desktop.StartsWithSystem = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether to offer going back to the previous window: on Windows, where the tray icon opens one of the two,
    /// while both come with HADA.
    /// </summary>
    public bool OffersWindowChoice { get; } = OperatingSystem.IsWindows();

    /// <summary>
    /// Whether there are dashboards to set up: in a window of their own on Windows, in the browser elsewhere, opened
    /// from the tray icon's menu either way.
    /// </summary>
    public bool OffersDashboard { get; } = true;

    public string DashboardDetail { get; } = Loc.Get(OperatingSystem.IsWindows() ? "Preferences_DashboardDetail" : "Preferences_DashboardDetailBrowser");

    /// <summary>Whether the browser for notifications can be chosen: from those Windows lists as installed.</summary>
    public bool OffersBrowserChoice { get; } = OperatingSystem.IsWindows();

    /// <summary>Whether the tray icon opens the new window; off, it opens the previous one.</summary>
    public bool UsesNewWindow
    {
        get => !UserPreferences.UsesPreviousWindow;
        set
        {
            UserPreferences.UsesPreviousWindow = !value;
            OnPropertyChanged();
        }
    }

    public string Language
    {
        get => UserPreferences.Language;
        set
        {
            UserPreferences.Language = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLanguageChanged));
        }
    }

    /// <summary>The window keeps the language it was opened in; this says a reopening is due.</summary>
    public bool IsLanguageChanged => UserPreferences.Language != _languageAtStart;

    /// <summary>The Home Assistants connected to directly, each with its dashboard.</summary>
    public ObservableCollection<DashboardServerViewModel> DashboardServers { get; } = [];

    public bool HasDashboardServers => DashboardServers.Count > 0;

    /// <summary>
    /// Brings the dashboards up to date with the servers that are saved now; called whenever the settings were
    /// read. An administrator window that runs as another account shows them without writing anything down, as
    /// it is that account's preferences it would write to.
    /// </summary>
    public void SyncDashboards(IEnumerable<HomeAssistantSettings> servers)
    {
        var saved = UserPreferences.DashboardServers;
        var own = UserPreferences.DashboardUrl;
        var synced = Dashboards.Sync(saved, servers.Select(server => (server.Id, server.Name, server.BaseUrl)), ref own);
        if (CanChange && !synced.SequenceEqual(saved))
        {
            UserPreferences.DashboardServers = synced;
            if (own != UserPreferences.DashboardUrl)
            {
                UserPreferences.DashboardUrl = own;
                OnPropertyChanged(nameof(DashboardUrl));
            }
        }

        DashboardServers.Clear();
        foreach (var server in synced)
        {
            DashboardServers.Add(new DashboardServerViewModel(server, SaveDashboard));
        }

        OnPropertyChanged(nameof(HasDashboardServers));
    }

    private static void SaveDashboard(ServerDashboard changed) =>
        UserPreferences.DashboardServers = [.. UserPreferences.DashboardServers.Select(server => server.Id == changed.Id ? changed : server)];

    /// <summary>Kept only when it is an http or https address; anything else clears it.</summary>
    public string DashboardUrl
    {
        get => UserPreferences.DashboardUrl;
        set
        {
            UserPreferences.DashboardUrl = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public double? IdleSeconds
    {
        get => UserPreferences.IdleSeconds;
        set
        {
            // An emptied box is on its way to a new number, not a wish for the default.
            if (value is { } seconds && !double.IsNaN(seconds))
            {
                UserPreferences.IdleSeconds = (int)Math.Round(seconds);
                OnPropertyChanged();
            }
        }
    }
}
