using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace HADA.Core;

/// <summary>
/// Which copy of HADA this process belongs to: the installed one, or a portable copy that was unpacked somewhere
/// and runs from there without being installed.
/// </summary>
/// <remarks>
/// A portable copy is a folder holding <c>service\</c> and <c>tray\</c> next to a file named
/// <see cref="MarkerFileName"/>. It keeps everything it writes in a <c>data\</c> folder beside them, runs its
/// service as an ordinary process of the signed-in user, and uses names of its own for everything processes find
/// each other by. So it neither sees nor disturbs an installed HADA, or another portable copy, on the same computer.
/// </remarks>
public static class AppInstance
{
    /// <summary>The file whose presence, one folder above the program, makes a copy portable.</summary>
    public const string MarkerFileName = "HADA.portable";

    /// <summary>The folder of the portable copy this process runs from, or <see langword="null"/> for the installed app.</summary>
    /// <remarks>
    /// Never one for a process running as SYSTEM: that is the installed service, and a portable copy's service runs
    /// as the user who started it. Were it otherwise, a file added next to the installed program would move the
    /// service's settings into a folder of no protection, which the service would then read and run commands from.
    /// </remarks>
    public static string? PortableRoot { get; } = RunsAsSystem() ? null : FindPortableRoot(AppContext.BaseDirectory);

    public static bool IsPortable => PortableRoot is not null;

    /// <summary>
    /// Appended to every pipe, mutex and signal name: empty for the installed app, and for a portable copy
    /// something like <c>.P1A2B3C4D</c> that is the same for all its processes and differs between copies.
    /// </summary>
    public static string Suffix { get; } = PortableRoot is null ? string.Empty : SuffixFor(PortableRoot);

    /// <summary>The pipe the service listens on, and the tray app and the window connect to.</summary>
    public static string PipeName => "HADA.Session" + Suffix;

    /// <summary>The <see cref="Hosting.AppSignal"/> a portable copy's tray app sets to ask its service to stop.</summary>
    public static string ServiceStopSignalName => @"Local\HADA.Service.Stop" + Suffix;

    /// <summary>The <see cref="Hosting.AppSignal"/> that asks the tray app of this copy to exit, set by starting it again with <c>--exit</c>.</summary>
    public static string TrayExitSignalName => @"Local\HADA.Tray.Exit" + Suffix;

    /// <summary>
    /// What the window of this copy is known by, the old one and the new one alike: one window per user, and one
    /// for the administrator copy opened with "Unlock editing". Its <see cref="Hosting.AppSignal"/>s add
    /// <c>.Show</c> and <c>.Close</c>.
    /// </summary>
    public static string WindowRoleName(bool elevated) => (elevated ? @"Local\HADA.Window.Admin" : @"Local\HADA.Window") + Suffix;

    /// <summary>Where a portable copy keeps settings, logs and everything else it writes; <see langword="null"/> for the installed app.</summary>
    public static string? PortableDataFolder => PortableRoot is null ? null : Path.Combine(PortableRoot, "data");

    /// <summary>
    /// Returns the folder above <paramref name="programFolder"/> when it holds the marker file. The program
    /// folders, <c>service\</c> and <c>tray\</c>, sit one level below the portable copy's root.
    /// </summary>
    public static string? FindPortableRoot(string programFolder)
    {
        ArgumentNullException.ThrowIfNull(programFolder);

        try
        {
            var root = Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFolder)))?.FullName;
            return root is not null && File.Exists(Path.Combine(root, MarkerFileName)) ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether this process is the Windows system account, as the installed service is.</summary>
    public static bool RunsAsSystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }

    /// <summary>The suffix of the portable copy in <paramref name="root"/>: the same however the path is written.</summary>
    public static string SuffixFor(string root)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        return ".P" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)), 0, 4);
    }
}
