using System.Text.Json;
using HADA.Engine.WebSocket;

namespace HADA.Service.Settings;

/// <summary>
/// Keeps what Home Assistant's <c>mobile_app</c> integration gave this computer, per Home Assistant, in
/// <c>mobile_app.json</c> beside the settings. The webhook ids are encrypted like the other secrets. A file of its
/// own, since saving the settings file makes the engines restart.
/// </summary>
public sealed partial class MobileAppRegistrationStore(SettingsStore settings, ILogger<MobileAppRegistrationStore> logger)
    : IMobileAppRegistrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();

    public string FilePath => Path.Combine(settings.FolderPath, "mobile_app.json");

    public MobileAppRegistration? Load(string serverId)
    {
        lock (_lock)
        {
            return ReadAll().TryGetValue(serverId, out var stored) && SettingsStore.TryUnprotect(stored.WebhookId) is { Length: > 0 } webhookId
                ? new MobileAppRegistration(webhookId, stored.BaseUrl, stored.DeviceId)
                : null;
        }
    }

    public void Save(string serverId, MobileAppRegistration? registration)
    {
        lock (_lock)
        {
            var all = ReadAll();
            if (registration is null)
            {
                if (!all.Remove(serverId))
                {
                    return;
                }
            }
            else
            {
                all[serverId] = new Stored(SettingsStore.Protect(registration.WebhookId), registration.BaseUrl, registration.DeviceId);
            }

            WriteAll(all);
        }
    }

    /// <summary>Forgets the registrations of servers that are no longer in the settings.</summary>
    public void KeepOnly(IReadOnlyCollection<string> serverIds)
    {
        lock (_lock)
        {
            var all = ReadAll();
            var gone = all.Keys.Where(id => !serverIds.Contains(id)).ToList();
            if (gone.Count == 0)
            {
                return;
            }

            foreach (var id in gone)
            {
                all.Remove(id);
            }

            WriteAll(all);
        }
    }

    /// <summary>Empty when the file is missing, unreadable or damaged; the computer then registers again.</summary>
    private Dictionary<string, Stored> ReadAll()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, Stored>>(stream, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private void WriteAll(Dictionary<string, Stored> all)
    {
        try
        {
            if (!settings.TryEnsureFolder())
            {
                LogSaveFailed(logger, FilePath, "the folder cannot be created");
                return;
            }

            // Write-then-rename, as for the settings file.
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(all, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: without the file the computer registers again next time, and shows up twice in Home Assistant.
            LogSaveFailed(logger, FilePath, ex.Message);
        }
    }

    /// <param name="WebhookId">Encrypted as <see cref="SettingsStore.Protect"/> does it; base64-encoded.</param>
    private sealed record Stored(string WebhookId, string BaseUrl, string DeviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mobile_app registration could not be saved to {Path} ({Reason}); the computer may register with Home Assistant again and appear there twice.")]
    private static partial void LogSaveFailed(ILogger logger, string path, string reason);
}
