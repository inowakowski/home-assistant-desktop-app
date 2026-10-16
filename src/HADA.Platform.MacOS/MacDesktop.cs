using System.Runtime.Versioning;
using System.Security;
using System.Text;
using HADA.Core.Actions;
using HADA.Core.Unix;

namespace HADA.Platform.MacOS;

/// <summary>
/// Shows notifications in Notification Center, through AppleScript's <c>display notification</c>: a title and a
/// text. Pictures, buttons and replacing an earlier one by its tag are not to be had that way.
/// </summary>
public static class MacNotifications
{
    /// <summary>
    /// Shows it. The texts go to AppleScript as arguments, not as part of the script, so nothing in them can be
    /// taken for AppleScript. Returns why it could not be shown, or <see langword="null"/>.
    /// </summary>
    public static string? Show(NotificationRequest request)
    {
        if (request.Clear)
        {
            return null;
        }

        string[] arguments =
        [
            "-e", "on run argv",
            "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
            "-e", "end run",
            request.Title,
            request.Message,
        ];
        var result = Tools.Run("osascript", arguments);
        return result is { Succeeded: true } ? null : Tools.Describe("osascript", result);
    }
}

/// <summary>
/// Whether HADA starts in the background when the user signs in: a launch agent of the user's own, which System
/// Settings lists under Login Items as allowed in the background.
/// </summary>
public static class MacAutostart
{
    public const string Label = "io.github.inowakowski.hada";

    [SupportedOSPlatform("macos")]
    public static bool IsEnabled
    {
        get => File.Exists(AgentPath);
        set
        {
            if (value)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
                File.WriteAllText(AgentPath, Agent(Environment.ProcessPath!), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            else
            {
                File.Delete(AgentPath);
            }
        }
    }

    private static string AgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    /// <summary>
    /// The launch agent for the program at <paramref name="program"/>: inside an app, the app opened the way the
    /// Finder opens it, so macOS knows it as the app; outside one, as when developing, the program itself.
    /// </summary>
    public static string Agent(string program)
    {
        var bundle = BundleOf(program);
        string[] arguments = bundle is null ? [program, "--background"] : ["/usr/bin/open", "-g", "-a", bundle, "--args", "--background"];
        var items = string.Concat(arguments.Select(argument => $"\n        <string>{SecurityElement.Escape(argument)}</string>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>{items}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>ProcessType</key>
                <string>Interactive</string>
            </dict>
            </plist>

            """.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The app a program is part of, <c>/Applications/HADA.app</c> for <c>/Applications/HADA.app/Contents/MacOS/HADA.App</c>.</summary>
    public static string? BundleOf(string program)
    {
        const string Inside = "/Contents/MacOS/";
        var inside = program.LastIndexOf(Inside, StringComparison.Ordinal);
        return inside > 0 && program.IndexOf('/', inside + Inside.Length) < 0 && program[..inside].EndsWith(".app", StringComparison.Ordinal)
            ? program[..inside]
            : null;
    }
}
