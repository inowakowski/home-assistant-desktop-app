using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Unix;

namespace HADA.Platform.MacOS;

/// <summary>
/// Whether the Mac's screens are on, from Core Graphics; turned off with <c>pmset</c>, and on again by telling
/// macOS that the user is active, as moving the mouse would.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
[SupportedOSPlatform("macos")]
public sealed class MacDisplayPower(Func<string, string[], ToolResult?>? run = null) : IDisplayPower
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    public bool? ReadDisplayOn() => MacNative.IsMainDisplayAsleep() is { } asleep ? !asleep : null;

    public bool CanSwitch => true;

    public string? TurnOff() => Run("pmset", "displaysleepnow");

    // Asserts that the user is active for a moment, which wakes the screens; it waits out the moment.
    public string? Wake() => Run("caffeinate", "-u", "-t", "2");

    private string? Run(string tool, params string[] arguments)
    {
        var result = _run(tool, arguments);
        return result is { Succeeded: true } ? null : Tools.Describe(tool, result);
    }
}

/// <summary>The brightness of the Mac's own screen; nothing on a Mac without one, as a Mac mini.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacScreenBrightness : IScreenBrightness
{
    public int? ReadPercent() => MacNative.ReadBuiltInBrightness() is { } level ? (int)Math.Round(level * 100) : null;

    public string? SetPercent(int percent) =>
        MacNative.SetBuiltInBrightness(Math.Clamp(percent, 0, 100) / 100f) ? null : "macOS did not set the brightness.";
}
