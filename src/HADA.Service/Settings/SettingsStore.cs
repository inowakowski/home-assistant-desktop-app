using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HADA.Ipc;

namespace HADA.Service.Settings;

/// <summary>Settings saved from the settings window. Each section present overrides the same section of appsettings.json.</summary>
public sealed record StoredSettings
{
    public MqttSettings? Mqtt { get; init; }

    /// <summary>DPAPI-protected (machine scope), base64-encoded.</summary>
    public string? MqttPassword { get; init; }

    public HomeAssistantSettings? HomeAssistant { get; init; }

    /// <summary>DPAPI-protected (machine scope), base64-encoded.</summary>
    public string? AccessToken { get; init; }

    public IReadOnlyList<string>? DisabledEntities { get; init; }

    public IReadOnlyList<string>? EnabledEntities { get; init; }

    public IReadOnlyList<CustomSensorDefinition>? CustomSensors { get; init; }

    public UpdateSettings? Updates { get; init; }
}

/// <summary>
/// Persists <see cref="StoredSettings"/> as JSON, by default in <c>%ProgramData%\HADA\settings.json</c>.
/// The folder is limited to SYSTEM, administrators and the account running the service, and secrets are also
/// encrypted with DPAPI, so a copy of the file is useless on another machine.
/// </summary>
/// <param name="protectFolder">
/// False for a portable copy, whose folder belongs to the user who unpacked it and is left as it is.
/// </param>
public sealed class SettingsStore(string? folderPath = null, bool protectFolder = true)
{
    private static readonly byte[] Entropy = "HADA.Settings.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FolderPath { get; } =
        folderPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HADA");

    public string FilePath => Path.Combine(FolderPath, "settings.json");

    /// <summary>Returns empty settings when the file is missing, unreadable or damaged, so it can never stop the service.</summary>
    public StoredSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<StoredSettings>(stream, JsonOptions) ?? new StoredSettings();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException or UnauthorizedAccessException)
        {
            return new StoredSettings();
        }
    }

    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The account running the service cannot write the folder.</exception>
    public void Save(StoredSettings settings)
    {
        EnsureFolder();

        // Write-then-rename so a crash mid-write never leaves a truncated settings file behind.
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    /// <summary>
    /// Who can decrypt saved secrets: any process on this computer, which the installed service needs because it
    /// runs as SYSTEM, or only the current user, for a portable copy. Set once, before settings are loaded.
    /// </summary>
    public static DataProtectionScope SecretScope { get; set; } = DataProtectionScope.LocalMachine;

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, SecretScope));

    /// <summary>Returns <see langword="null"/> when there is no secret or it was encrypted on another machine.</summary>
    public static string? TryUnprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, SecretScope));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates and protects the folder ahead of the first save, so files written there earlier, such as logs,
    /// are protected too. Returns <see langword="false"/> when the account running the service may not do that.
    /// </summary>
    public bool TryEnsureFolder()
    {
        try
        {
            EnsureFolder();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnsureFolder()
    {
        if (!protectFolder)
        {
            Directory.CreateDirectory(FolderPath);
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier[] owners =
        [
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            identity.User!,
        ];

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in owners.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var folder = new DirectoryInfo(FolderPath);
        if (folder.Exists)
        {
            folder.SetAccessControl(security);
        }
        else
        {
            folder.Create(security);
        }
    }
}
