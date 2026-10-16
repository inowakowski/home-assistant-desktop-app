using System.IO;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using HADA.Core;
using Microsoft.Win32;

namespace HADA.Presentation;

/// <summary>Where this copy of HADA keeps what it writes for the signed-in user.</summary>
/// <summary>
/// Choices that belong to one user and need no administrator rights. Read by both the tray app and the window,
/// which are separate processes.
/// </summary>
/// <remarks>
/// The installed app on Windows keeps them under <c>HKCU\Software\HADA</c>, next to whether it starts with Windows.
/// A portable copy keeps them in <c>preferences.json</c> in its data folder, so that it leaves nothing behind in the
/// registry and takes its preferences along when the folder is moved; on other systems they are in that file in
/// <see cref="AppFolders.UserData"/>.
/// </remarks>
public static class UserPreferences
{
    public const int DefaultIdleSeconds = 60;
    public const int MinIdleSeconds = 10;
    public const int MaxIdleSeconds = 3600;

    private const string LanguageValue = "Language";
    private const string IdleValue = "IdleSeconds";
    private const string DashboardValue = "DashboardUrl";
    private const string DashboardServersValue = "DashboardServers";
    private const string DashboardSelectedValue = "DashboardSelected";
    private const string BrowserValue = "NotificationBrowser";
    private const string SidebarValue = "SidebarCollapsed";
    private const string PreviousWindowValue = "PreviousWindow";
    private const string BackgroundNoticeValue = "BackgroundNoticeShown";

    private static readonly IPreferenceStore Store = OperatingSystem.IsWindows() && !AppInstance.IsPortable
        ? new RegistryPreferenceStore()
        : new JsonPreferenceStore(Path.Combine(AppFolders.UserData, "preferences.json"));

    /// <summary><c>pl</c>, <c>en</c>, or empty to follow the system's display language.</summary>
    public static string Language
    {
        get => Store.ReadText(LanguageValue) is "pl" or "en" ? Store.ReadText(LanguageValue)! : string.Empty;
        set => Store.Write(LanguageValue, value is "pl" or "en" ? value : null);
    }

    /// <summary>How long after the last keyboard or mouse input the user still counts as active.</summary>
    public static int IdleSeconds
    {
        get => Store.ReadNumber(IdleValue) is { } seconds ? Math.Clamp(seconds, MinIdleSeconds, MaxIdleSeconds) : DefaultIdleSeconds;
        set => Store.Write(IdleValue, Math.Clamp(value, MinIdleSeconds, MaxIdleSeconds));
    }

    /// <summary>
    /// An address of the user's own for the dashboard window to show, besides those of the Home Assistants
    /// connected to directly; empty for none. Only <c>http</c> and <c>https</c> addresses are kept.
    /// </summary>
    public static string DashboardUrl
    {
        get => Store.ReadText(DashboardValue) is { } url && Dashboards.TryGetAddress(url, out _) ? url : string.Empty;
        set => Store.Write(DashboardValue, Dashboards.TryGetAddress(value, out var address) ? address.AbsoluteUri : null);
    }

    /// <summary>
    /// The Home Assistants connected to directly, as the settings window last saw them, each with what the user
    /// chose for its dashboard. The window keeps the list up to date; the tray icon's menu only reads it.
    /// </summary>
    public static IReadOnlyList<ServerDashboard> DashboardServers
    {
        get => Dashboards.Parse(Store.ReadText(DashboardServersValue));
        set => Store.Write(DashboardServersValue, value.Count == 0 ? null : Dashboards.Serialize(value));
    }

    /// <summary>The dashboards there are to show: see <see cref="Dashboards.List"/>.</summary>
    public static IReadOnlyList<Dashboard> AllDashboards => Dashboards.List(DashboardServers, DashboardUrl);

    /// <summary>
    /// The id of the dashboard chosen last in the tray icon's menu. Written by the tray app and read by the
    /// dashboard window, which is another process and may be open already.
    /// </summary>
    public static string DashboardSelected
    {
        get => Store.ReadText(DashboardSelectedValue) ?? string.Empty;
        set => Store.Write(DashboardSelectedValue, string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>The dashboard chosen last if it is still there, else the first; <see langword="null"/> when there is none.</summary>
    public static Dashboard? SelectedDashboard
    {
        get
        {
            var all = AllDashboards;
            var selected = DashboardSelected;
            return all.FirstOrDefault(dashboard => dashboard.Id == selected) ?? (all.Count > 0 ? all[0] : null);
        }
    }

    /// <summary>Whether the window's sidebar shows the icons of its pages only.</summary>
    public static bool SidebarCollapsed
    {
        get => Store.ReadNumber(SidebarValue) is 1;
        set => Store.Write(SidebarValue, value ? 1 : null);
    }

    /// <summary>Whether the window has said, the first time it was closed, that HADA goes on running without it.</summary>
    public static bool BackgroundNoticeShown
    {
        get => Store.ReadNumber(BackgroundNoticeValue) is 1;
        set => Store.Write(BackgroundNoticeValue, value ? 1 : null);
    }

    /// <summary>Whether the tray icon opens the previous window rather than the new one, while both come with HADA.</summary>
    public static bool UsesPreviousWindow
    {
        get => Store.ReadNumber(PreviousWindowValue) is 1;
        set => Store.Write(PreviousWindowValue, value ? 1 : null);
    }

    /// <summary>
    /// The program file of the browser that addresses of notifications are opened in; empty for the default
    /// browser. See <see cref="IDesktop.InstalledBrowsers"/>.
    /// </summary>
    public static string NotificationBrowser
    {
        get => Store.ReadText(BrowserValue) ?? string.Empty;
        set => Store.Write(BrowserValue, string.IsNullOrWhiteSpace(value) ? null : value);
    }
}

/// <summary>Named values, each a text or a whole number. One that cannot be read is simply not set; the defaults then apply.</summary>
internal interface IPreferenceStore
{
    string? ReadText(string name);

    int? ReadNumber(string name);

    /// <param name="value">A string or an int; <see langword="null"/> removes the value.</param>
    void Write(string name, object? value);
}

[SupportedOSPlatform("windows")]
internal sealed class RegistryPreferenceStore : IPreferenceStore
{
    private const string KeyPath = @"Software\HADA";

    public string? ReadText(string name) => Read(name) as string;

    public int? ReadNumber(string name) => Read(name) as int?;

    public void Write(string name, object? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
    }

    private static object? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// A small JSON file. The tray app asks for a value every couple of seconds, so the file is read again only when
/// it was written since, e.g. by the window, which is another process.
/// </summary>
internal sealed class JsonPreferenceStore(string path) : IPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Lock _lock = new();
    private Dictionary<string, JsonElement> _values = [];
    private DateTime _loadedVersion;

    public string? ReadText(string name) =>
        Load().TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public int? ReadNumber(string name) =>
        Load().TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    public void Write(string name, object? value)
    {
        lock (_lock)
        {
            var values = new Dictionary<string, JsonElement>(Load(), StringComparer.Ordinal);
            if (value is null)
            {
                values.Remove(name);
            }
            else
            {
                values[name] = JsonSerializer.SerializeToElement(value);
            }

            AppFolders.CreatePrivate(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(values, JsonOptions));
            _values = values;
            _loadedVersion = File.GetLastWriteTimeUtc(path);
        }
    }

    private Dictionary<string, JsonElement> Load()
    {
        lock (_lock)
        {
            try
            {
                // For a file that does not exist this is a fixed date long ago, which never changes either.
                var version = File.GetLastWriteTimeUtc(path);
                if (version != _loadedVersion)
                {
                    _values = File.Exists(path)
                        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? []
                        : [];
                    _loadedVersion = version;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Being written just now, or damaged: keep what was read last.
            }

            return _values;
        }
    }
}
