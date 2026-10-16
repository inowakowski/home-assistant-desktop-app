using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Sensors;
using HADA.Core.Unix;
using Microsoft.Extensions.DependencyInjection;

namespace HADA.Platform.MacOS;

/// <summary>What HADA is made of on macOS, for the hosts that put it together.</summary>
[SupportedOSPlatform("macos")]
public static class MacPlatform
{
    /// <summary>The sensors and actions of the service.</summary>
    public static IServiceCollection AddMacServiceEntities(this IServiceCollection services)
    {
        services.AddSingleton<MacNetwork>(_ => new MacNetwork());
        services.AddSingleton<INetworkLocation, MacNetworkLocation>();
        services.AddSingleton<ISystemReadings, MacSystemReadings>();
        services.AddSingleton<IPowerControl, MacPowerControl>();
        services.AddSingleton<IScreenLock, MacScreenLock>();
        services.AddSingleton<IDeviceDirectory>(_ => new MacUsbDevices());
        services.AddSingleton<IHardwareReadings>(_ => new MacHardware());
        services.AddSystemEntities();
        services.AddHardwareEntities();
        return services;
    }
}

/// <summary>
/// What the service's sensors read, from macOS: the kernel's statistics, <c>pmset</c> for the power, and the
/// I/O Registry, where the login window tells who is at the screen.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacSystemReadings(MacNetwork network, Func<string, string[], ToolResult?>? run = null) : ISystemReadings
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    public CpuSample? ReadCpu() => MacNative.ReadCpuTicks() is { } ticks ? new CpuSample(ticks.Busy, ticks.Total) : null;

    public int? ReadMemoryLoadPercent() => MacNative.ReadMemoryLoadPercent();

    public PowerStatus? ReadPower() =>
        _run("pmset", ["-g", "batt"]) is { Succeeded: true } result ? MacParsers.ParsePmsetBattery(result.Output) : null;

    public DateTimeOffset? ReadBootTime() => MacNative.ReadBootTime();

    public string? ReadConsoleUser() => ReadConsole()?.UserName;

    public bool? ReadConsoleLocked() => ReadConsole()?.IsLocked;

    /// <summary>macOS does not say whether updates wait for a restart; the sensor is left out.</summary>
    public string? ReadPendingRestart() => null;

    /// <summary>
    /// The Mac's own disk, <c>/</c>. On APFS the system and the data are volumes of one container and share its free
    /// space, so the one disk tells it all; disks plugged in come and go, and are left out.
    /// </summary>
    public IReadOnlyList<DiskUsage> ReadDisks()
    {
        try
        {
            var root = new DriveInfo("/");
            if (!root.IsReady || root.TotalSize <= 0)
            {
                return [];
            }

            const double Gigabyte = 1024d * 1024 * 1024;
            var used = (int)Math.Round(100d * (root.TotalSize - root.AvailableFreeSpace) / root.TotalSize);
            return [new DiskUsage("root", "/", Math.Clamp(used, 0, 100), Math.Round(root.AvailableFreeSpace / Gigabyte, 1), Math.Round(root.TotalSize / Gigabyte, 1))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public WifiStatus ReadWifi() => network.ReadWifi();

    private ConsoleSession? ReadConsole() =>
        _run("ioreg", ["-a", "-n", "Root", "-d", "1"]) is { Succeeded: true } result ? MacParsers.ParseConsoleUsers(result.Output) : null;
}

/// <summary>
/// Power as the Apple menu does it: sleep through <c>pmset</c>, shut down and restart through the login window,
/// which asks nobody and lets apps with unsaved work object. A Mac has no hibernation to start on demand.
/// </summary>
public sealed class MacPowerControl : IPowerControl
{
    public bool Supports(PowerCommand command) => command != PowerCommand.Hibernate;

    public string? Execute(PowerCommand command)
    {
        var (tool, arguments) = command switch
        {
            PowerCommand.Sleep => ("pmset", new[] { "sleepnow" }),
            PowerCommand.Shutdown => ("osascript", ["-e", "tell application \"loginwindow\" to «event aevtrsdn»"]),
            PowerCommand.Restart => ("osascript", ["-e", "tell application \"loginwindow\" to «event aevtrrst»"]),
            _ => (null, []),
        };
        if (tool is null)
        {
            return "a Mac cannot be hibernated on demand.";
        }

        var result = Tools.Run(tool, arguments);
        return result is { Succeeded: true } ? null : Tools.Describe(tool, result);
    }
}

/// <summary>
/// Locks the screen as Control-Command-Q does, through the login framework the Lock Screen menu item uses; where
/// that is not to be had, the display is put to sleep, which locks it when the Mac asks for the password at once.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacScreenLock : IScreenLock
{
    private const string Login = "/System/Library/PrivateFrameworks/login.framework/Versions/Current/login";

    public string? Lock()
    {
        try
        {
            if (SACLockScreenImmediate() == 0)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // An older or newer macOS without it; the display's sleep is the way left.
        }

        var result = Tools.Run("pmset", "displaysleepnow");
        return result is { Succeeded: true } ? null : Tools.Describe("pmset displaysleepnow", result);
    }

    [DllImport(Login)]
    private static extern int SACLockScreenImmediate();
}
