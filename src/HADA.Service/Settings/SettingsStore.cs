using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using HADA.Engine.Mqtt;
using HADA.Ipc;

namespace HADA.Service.Settings;

/// <summary>Settings saved from the settings window. Each section present overrides the same section of appsettings.json.</summary>
public sealed record StoredSettings
{
    /// <summary>The MQTT servers, each with its own password. An empty list means none, whatever appsettings.json says.</summary>
    public IReadOnlyList<StoredMqttServer>? MqttServers { get; init; }

    /// <summary>The one server that versions before 1.2 saved. Read, and never written again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MqttSettings? Mqtt { get; init; }

    /// <summary>The password of <see cref="Mqtt"/>, protected like <see cref="StoredMqttServer.Password"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MqttPassword { get; init; }

    /// <summary>The servers these settings name, whichever version saved them; <see langword="null"/> when none ever were.</summary>
    public IReadOnlyList<StoredMqttServer>? GetMqttServers() =>
        MqttServers ?? (Mqtt is null ? null : [new StoredMqttServer(Mqtt with { Id = MqttOptions.DefaultId }, MqttPassword)]);

    public HomeAssistantSettings? HomeAssistant { get; init; }

    /// <summary>Encrypted as <see cref="SettingsStore.Protect"/> does it; base64-encoded.</summary>
    public string? AccessToken { get; init; }

    public IReadOnlyList<string>? DisabledEntities { get; init; }

    public IReadOnlyList<string>? EnabledEntities { get; init; }

    public IReadOnlyList<CustomSensorDefinition>? CustomSensors { get; init; }

    public UpdateSettings? Updates { get; init; }
}

/// <summary>What <see cref="SettingsStore.SecureFolder"/> found.</summary>
/// <param name="SetAsidePath">Where a folder that was not trusted was moved to; <see langword="null"/> when it was trusted.</param>
/// <param name="Owner">Who owned the folder that was set aside.</param>
/// <param name="OwnershipFailures">How many of the files in a trusted folder could not be given to the Administrators group.</param>
public sealed record FolderCheck(string? SetAsidePath = null, string? Owner = null, int OwnershipFailures = 0);

/// <param name="Password">Encrypted as <see cref="SettingsStore.Protect"/> does it; base64-encoded.</param>
public sealed record StoredMqttServer(MqttSettings Settings, string? Password);

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

    private static ISecretProtector? _secrets;

    /// <summary>
    /// What encrypts saved secrets. By default any process on this computer can decrypt them, which the installed
    /// service needs; a portable copy sets one that only the current user can. Set once, before settings are loaded.
    /// </summary>
    public static ISecretProtector Secrets
    {
        get => _secrets ??= SecretProtector.ForThisSystem(currentUserOnly: false);
        set => _secrets = value;
    }

    public static string Protect(string secret) => Secrets.Protect(secret);

    /// <summary>Returns <see langword="null"/> when there is no secret or it was encrypted on another machine.</summary>
    public static string? TryUnprotect(string? protectedSecret) => Secrets.TryUnprotect(protectedSecret);

    /// <summary>
    /// Run by the installed service before it reads anything from its folder. Any user may create folders in
    /// ProgramData, so someone could make this one before HADA is installed and own it; an owner may always change
    /// a folder's access rules, whatever the service sets them to, and could then read the secrets in it, or change
    /// what the service runs as SYSTEM. So a folder owned by anyone but the system, the administrators or one of
    /// them is not trusted: it is renamed and set aside, and an empty one made. A trusted one is handed to the
    /// Administrators group, together with what is in it, so that no single account keeps an owner's rights.
    /// </summary>
    /// <param name="isTrustedOwner">Whether the folder's owner may have put things there.</param>
    /// <param name="takeOwnership">False in tests, which cannot give anything to the Administrators group.</param>
    /// <exception cref="InvalidOperationException">An untrusted folder could not be set aside; the service must not start.</exception>
    [SupportedOSPlatform("windows")]
    public FolderCheck SecureFolder(Func<SecurityIdentifier, bool> isTrustedOwner, bool takeOwnership = true)
    {
        var folder = new DirectoryInfo(FolderPath);
        if (!folder.Exists)
        {
            EnsureFolder();
            return new FolderCheck();
        }

        var owner = folder.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        // A link elsewhere is no folder of ours either, whoever made it.
        if (owner is null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || !isTrustedOwner(owner))
        {
            var ownerName = NameOf(owner);
            var setAside = $"{FolderPath}.untrusted-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                Directory.Move(FolderPath, setAside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"The folder {FolderPath} belongs to {ownerName}, not to the system or an administrator, so HADA does not trust "
                    + $"what is in it, and it could not be set aside ({ex.Message}). Rename or delete it as an administrator.",
                    ex);
            }

            EnsureFolder();
            return new FolderCheck(setAside, ownerName);
        }

        EnsureFolder();
        return new FolderCheck(OwnershipFailures: takeOwnership ? GiveToAdministrators(folder) : 0);
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

    /// <summary>Makes the Administrators group the owner of the folder and of everything in it; returns how many could not be.</summary>
    [SupportedOSPlatform("windows")]
    private static int GiveToAdministrators(DirectoryInfo folder)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var failures = 0;

        var folderSecurity = new DirectorySecurity();
        folderSecurity.SetOwner(administrators);
        failures += TrySet(() => folder.SetAccessControl(folderSecurity));

        var everything = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var entry in folder.EnumerateFileSystemInfos("*", everything))
        {
            failures += entry switch
            {
                DirectoryInfo directory => TrySet(() =>
                {
                    var security = new DirectorySecurity();
                    security.SetOwner(administrators);
                    directory.SetAccessControl(security);
                }),
                FileInfo file => TrySet(() =>
                {
                    var security = new FileSecurity();
                    security.SetOwner(administrators);
                    file.SetAccessControl(security);
                }),
                _ => 0,
            };
        }

        return failures;

        static int TrySet(Action set)
        {
            try
            {
                set();
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return 1;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string NameOf(SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            return "nobody known";
        }

        try
        {
            return $"{sid.Translate(typeof(NTAccount)).Value} ({sid.Value})";
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    private void EnsureFolder()
    {
        if (!protectFolder)
        {
            Directory.CreateDirectory(FolderPath);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            EnsureProtectedWindowsFolder();
            return;
        }

        // Elsewhere the folder is the service's own user's, and nobody else's.
        Directory.CreateDirectory(FolderPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private void EnsureProtectedWindowsFolder()
    {
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
