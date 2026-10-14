namespace HADA.Core;

/// <summary>
/// Where HADA keeps what it writes, by the conventions of each system. A portable copy keeps all of it in its own
/// <c>data</c> folder instead, see <see cref="AppInstance.PortableDataFolder"/>.
/// </summary>
/// <remarks>
/// On Windows the installed service runs as SYSTEM and keeps its settings apart from those of the users. On Linux
/// and macOS the service runs as the user, so all of it is the user's.
/// </remarks>
public static class AppFolders
{
    /// <summary>
    /// The service's settings and secrets: <c>%ProgramData%\HADA</c>, <c>~/.config/hada</c> (or
    /// <c>$XDG_CONFIG_HOME/hada</c>), <c>~/Library/Application Support/HADA</c>.
    /// </summary>
    public static string ServiceData => AppInstance.PortableDataFolder ?? Choose(
        windows: () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HADA"),
        linux: () => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "hada"),
        macOS: ApplicationSupport);

    /// <summary>
    /// The service's logs: <c>%ProgramData%\HADA\logs</c>, <c>~/.local/state/hada/logs</c> (or
    /// <c>$XDG_STATE_HOME/hada/logs</c>), <c>~/Library/Logs/HADA</c>.
    /// </summary>
    public static string ServiceLogs => AppInstance.PortableDataFolder is { } portable ? Path.Combine(portable, "logs") : Choose(
        windows: () => Path.Combine(ServiceData, "logs"),
        linux: SharedLogs,
        macOS: SharedLogs);

    /// <summary>
    /// What the tray app and its windows write for one user, such as downloaded updates and the dashboard's browser
    /// profile: <c>%LocalAppData%\HADA</c>, <c>~/.local/share/hada</c> (or <c>$XDG_DATA_HOME/hada</c>),
    /// <c>~/Library/Application Support/HADA</c>.
    /// </summary>
    public static string UserData => AppInstance.PortableDataFolder ?? Choose(
        windows: () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HADA"),
        linux: () => Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "hada"),
        macOS: ApplicationSupport);

    /// <summary>The logs of the tray app and its windows: in <see cref="UserData"/> on Windows, next to the service's elsewhere.</summary>
    public static string UserLogs => AppInstance.PortableDataFolder is { } portable ? Path.Combine(portable, "logs") : Choose(
        windows: () => Path.Combine(UserData, "logs"),
        linux: SharedLogs,
        macOS: SharedLogs);

    /// <summary>Makes a folder that only the user running HADA may enter; on Windows its permissions are inherited.</summary>
    public static DirectoryInfo CreatePrivate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(path)
            : Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string SharedLogs() => OperatingSystem.IsMacOS()
        ? Path.Combine(Home(), "Library", "Logs", "HADA")
        : Path.Combine(Xdg("XDG_STATE_HOME", Path.Combine(".local", "state")), "hada", "logs");

    private static string ApplicationSupport() => Path.Combine(Home(), "Library", "Application Support", "HADA");

    /// <summary>An XDG base folder: the variable when it names an absolute path, as the specification asks, else its default in the home folder.</summary>
    private static string Xdg(string variable, string defaultInHome)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : Path.Combine(Home(), defaultInHome);
    }

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Choose(Func<string> windows, Func<string> linux, Func<string> macOS) =>
        OperatingSystem.IsWindows() ? windows() : OperatingSystem.IsMacOS() ? macOS() : linux();
}
