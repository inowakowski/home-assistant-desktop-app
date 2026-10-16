using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Sensors;
using HADA.Core.Unix;
using Microsoft.Extensions.DependencyInjection;

namespace HADA.Platform.Linux;

/// <summary>What HADA is made of on Linux, for the hosts that put it together.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxPlatform
{
    /// <summary>The sensors and actions of the service.</summary>
    public static IServiceCollection AddLinuxServiceEntities(this IServiceCollection services)
    {
        services.AddSingleton<LoginSessions>(_ => new LoginSessions());
        services.AddSingleton<LinuxNetwork>(_ => new LinuxNetwork());
        services.AddSingleton<INetworkLocation, LinuxNetworkLocation>();
        services.AddSingleton<ISystemReadings, LinuxSystemReadings>();
        services.AddSingleton<IPowerControl, LinuxPowerControl>();
        services.AddSingleton<IScreenLock, LinuxScreenLock>();
        services.AddSingleton<IDeviceDirectory>(_ => new LinuxUsbDevices());
        services.AddSingleton<LinuxHardware>(_ => new LinuxHardware());
        services.AddSingleton<IHardwareReadings>(provider => provider.GetRequiredService<LinuxHardware>());
        services.AddSystemEntities();
        services.AddHardwareEntities();
        return services;
    }
}

/// <summary>What the service's sensors read, from Linux: <c>/proc</c>, <c>/sys</c> and systemd-logind.</summary>
public sealed class LinuxSystemReadings(LoginSessions sessions, LinuxNetwork? network = null) : ISystemReadings
{
    public CpuSample? ReadCpu() => Proc.ReadCpu();

    public int? ReadMemoryLoadPercent() => Proc.ReadMemoryLoadPercent();

    public PowerStatus? ReadPower() => PowerSupplies.Read();

    public DateTimeOffset? ReadBootTime() => Proc.ReadBootTime();

    public string? ReadConsoleUser()
    {
        var session = sessions.Read(out var known);
        return known ? session?.UserName ?? string.Empty : null;
    }

    public bool? ReadConsoleLocked()
    {
        var session = sessions.Read(out var known);

        // Nobody's session on the screen is as good as locked; where logind cannot be asked, nothing is known.
        return known ? session?.IsLocked ?? true : null;
    }

    public string? ReadPendingRestart() => PendingRestart.Read();

    public IReadOnlyList<DiskUsage> ReadDisks() => MountedDisks.Read();

    public WifiStatus ReadWifi() => (network ?? new LinuxNetwork()).ReadWifi();
}

/// <summary>
/// Power through systemd, as the desktop's own menu does it. Allowed without a password to whoever sits at the
/// computer, so the service runs in the user's session; outside one, systemd asks for an administrator.
/// </summary>
public sealed class LinuxPowerControl : IPowerControl
{
    public string? Execute(PowerCommand command)
    {
        var verb = command switch
        {
            PowerCommand.Sleep => "suspend",
            PowerCommand.Hibernate => "hibernate",
            PowerCommand.Shutdown => "poweroff",
            _ => "reboot",
        };

        var result = Tools.Run("systemctl", verb);
        return result is { Succeeded: true } ? null : Tools.Describe($"systemctl {verb}", result);
    }
}

/// <summary>Locks the session on the computer's screen through systemd-logind, which has the desktop show its lock screen.</summary>
public sealed class LinuxScreenLock(LoginSessions sessions) : IScreenLock
{
    public string? Lock()
    {
        if (sessions.Read(out _) is not { IsGreeter: false } session)
        {
            return "nobody's session is on the screen.";
        }

        var result = Tools.Run("loginctl", "lock-session", session.Id);
        return result is { Succeeded: true } ? null : Tools.Describe("loginctl lock-session", result);
    }
}
