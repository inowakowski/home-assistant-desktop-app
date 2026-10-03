using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Sensors;
using Microsoft.Extensions.DependencyInjection;

// Everything in this assembly talks to Windows; callers in projects that run elsewhere too must check first.
[assembly: SupportedOSPlatform("windows")]

namespace HADA.Platform.Windows;

/// <summary>What HADA is made of on Windows, for the hosts that put it together.</summary>
public static class WindowsPlatform
{
    /// <summary>
    /// The sensors and actions of the service, which work whether or not anybody is signed in. Those that need a
    /// user's session are the tray app's.
    /// </summary>
    public static IServiceCollection AddWindowsServiceEntities(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceDirectory, WindowsDeviceDirectory>();

        services.AddHostedService<CpuLoadSensor>();
        services.AddHostedService<MemoryUsageSensor>();
        services.AddHostedService<BatterySensor>();
        services.AddHostedService<PowerStateSensor>();
        services.AddHostedService<SessionLockSensor>();
        services.AddHostedService<LastBootSensor>();
        services.AddHostedService<ActiveUserSensor>();
        services.AddHostedService<NetworkSensor>();
        services.AddHostedService<DiskUsageSensor>();
        services.AddHostedService<GpuLoadSensor>();
        services.AddHostedService<LockScreenAction>();
        services.AddHostedService<PowerActions>();
        return services;
    }
}

/// <summary>The devices Windows sees, by their instance ids; see <see cref="PnpDevices"/>.</summary>
public sealed class WindowsDeviceDirectory : IDeviceDirectory
{
    public bool IsPresent(string idFragment) => PnpDevices.IsPresent(idFragment);
}
