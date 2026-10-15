using System.Runtime.Versioning;
using System.Text;

namespace HADA.Platform.Linux;

/// <summary>
/// Whether HADA starts in the background when the user signs in: an entry in the user's own autostart folder,
/// which GNOME, KDE and the other desktops that follow freedesktop.org read.
/// </summary>
public static class LinuxAutostart
{
    public const string FileName = "hada.desktop";

    [SupportedOSPlatform("linux")]
    public static bool IsEnabled
    {
        get => File.Exists(EntryPath);
        set
        {
            if (value)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
                File.WriteAllText(EntryPath, Entry(Command), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            else
            {
                File.Delete(EntryPath);
            }
        }
    }

    private static string EntryPath => Path.Combine(ConfigHome, "autostart", FileName);

    private static string ConfigHome
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value)
                ? value
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
    }

    // An AppImage runs from a mount that changes with each start; the image itself is where it stays.
    private static string Command => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? image : Environment.ProcessPath!;

    /// <summary>The autostart entry that runs <paramref name="program"/> without its window; lines end in LF, as desktop entries must.</summary>
    public static string Entry(string program) => string.Join(
        '\n',
        "[Desktop Entry]",
        "Type=Application",
        "Name=HADA",
        "Comment=Home Assistant Desktop App",
        $"Exec={Quote(program)} --background",
        "Icon=hada",
        "Terminal=false",
        "NoDisplay=true",
        "X-GNOME-Autostart-enabled=true",
        string.Empty);

    /// <summary>A program's path as the desktop entry specification wants it in <c>Exec</c>: quoted, with its special characters escaped.</summary>
    public static string Quote(string program)
    {
        var escaped = new StringBuilder();
        foreach (var character in program)
        {
            if (character is '"' or '`' or '$' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        // Within a desktop entry's string a backslash is escaped once more.
        return "\"" + escaped.ToString().Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
    }
}
