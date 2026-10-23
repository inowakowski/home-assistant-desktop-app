using HADA.Core.Abstractions;
using HADA.Core.Unix;

namespace HADA.Platform.MacOS;

/// <summary>
/// The Mac's dark look, as System Settings → Appearance switches it: read from the global defaults, switched through
/// System Events, which macOS asks the user to allow HADA to control the first time.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
public sealed class MacDarkMode(Func<string, string[], ToolResult?>? run = null) : IDarkMode
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    public bool CanSet => true;

    // Without AppleInterfaceStyle, which defaults then says does not exist, the Mac looks light.
    public bool? Read() => _run("defaults", ["read", "-g", "AppleInterfaceStyle"]) switch
    {
        null => null,
        { Succeeded: true } result => result.Output.Trim() == "Dark",
        _ => false,
    };

    public string? Set(bool dark)
    {
        var result = _run(
            "osascript",
            ["-e", $"tell application \"System Events\" to tell appearance preferences to set dark mode to {(dark ? "true" : "false")}"]);
        return result is { Succeeded: true } ? null : Tools.Describe("osascript", result);
    }
}

/// <summary>
/// Signs out as the Apple menu does, without its question whether to: the login window is told to log out, and apps
/// with unsaved work can still hold it up.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
public sealed class MacSignOut(Func<string, string[], ToolResult?>? run = null) : ISignOut
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    public bool CanSignOut => true;

    public string? SignOut()
    {
        var result = _run("osascript", ["-e", "tell application \"loginwindow\" to «event aevtrlgo»"]);
        return result is { Succeeded: true } ? null : Tools.Describe("osascript", result);
    }
}
