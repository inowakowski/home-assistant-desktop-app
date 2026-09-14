using System.Diagnostics;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Sensors;

public readonly record struct ForegroundWindowInfo(string Title, string? ProcessName);

public static class ForegroundWindow
{
    /// <summary>Home Assistant rejects states longer than 255 characters.</summary>
    public const int MaxTitleLength = 255;

    /// <summary>
    /// Returns <see langword="null"/> when no window has focus, e.g. while the workstation is locked
    /// or when called from session 0, which has no desktop.
    /// </summary>
    public static unsafe ForegroundWindowInfo? TryRead()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[MaxTitleLength + 1];
        int length;
        fixed (char* chars = buffer)
        {
            length = NativeMethods.GetWindowText(window, chars, buffer.Length);
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return new ForegroundWindowInfo(new string(buffer[..length]), TryGetProcessName(processId));
    }

    private static string? TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process exited between the two calls.
            return null;
        }
    }
}
