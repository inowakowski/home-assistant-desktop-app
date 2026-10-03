using System.IO;
using System.Security;
using System.Text.Json;
using HADA.Core;
using Microsoft.Win32;

namespace HADA.Tray;

/// <summary>Where this copy of HADA keeps what it writes for the signed-in user.</summary>
internal static class TrayPaths
{
    /// <summary>
    /// Logs, the dashboard window's browser profile, downloaded updates and notification pictures:
    /// <c>%LocalAppData%\HADA</c> for the installed app, the copy's own <c>data</c> folder for a portable one.
    /// </summary>
    public static string DataFolder { get; } =
        AppInstance.PortableDataFolder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HADA");
}

/// <summary>
/// Choices that belong to one user and need no administrator rights. Read by both the tray app and the window,
/// which are separate processes.
/// </summary>
/// <remarks>
/// The installed app keeps them under <c>HKCU\Software\HADA</c>, next to <see cref="Autostart"/>. A portable copy
/// keeps them in <c>preferences.json</c> in its data folder, so that it leaves nothing behind in the registry and
/// takes its preferences along when the folder is moved.
/// </remarks>
internal static class UserPreferences
{
    public const int DefaultIdleSeconds = 60;
    public const int MinIdleSeconds = 10;
    public const int MaxIdleSeconds = 3600;

    private const string LanguageValue = "Language";
    private const string IdleValue = "IdleSeconds";
    private const string DashboardValue = "DashboardUrl";
    private const string BrowserValue = "NotificationBrowser";

    private static readonly IPreferenceStore Store = AppInstance.PortableDataFolder is { } folder
        ? new JsonPreferenceStore(Path.Combine(folder, "preferences.json"))
        : new RegistryPreferenceStore();

    /// <summary><c>pl</c>, <c>en</c>, or empty to follow Windows' display language.</summary>
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
    /// The Home Assistant address the dashboard window shows; empty for no dashboard window. Only <c>http</c> and
    /// <c>https</c> addresses are kept.
    /// </summary>
    public static string DashboardUrl
    {
        get => Store.ReadText(DashboardValue) is { } url && TryGetDashboardAddress(url, out _) ? url : string.Empty;
        set => Store.Write(DashboardValue, TryGetDashboardAddress(value, out var address) ? address.AbsoluteUri : null);
    }

    /// <summary>
    /// The program file of the browser that addresses of notifications are opened in; empty for the default
    /// browser. See <see cref="Browsers"/>.
    /// </summary>
    public static string NotificationBrowser
    {
        get => Store.ReadText(BrowserValue) ?? string.Empty;
        set => Store.Write(BrowserValue, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public static bool TryGetDashboardAddress(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? address) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out address)
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);
}

/// <summary>Named values, each a text or a whole number. One that cannot be read is simply not set; the defaults then apply.</summary>
internal interface IPreferenceStore
{
    string? ReadText(string name);

    int? ReadNumber(string name);

    /// <param name="value">A string or an int; <see langword="null"/> removes the value.</param>
    void Write(string name, object? value);
}

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

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
