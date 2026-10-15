using HADA.Core.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace HADA.Core.Sensors;

/// <summary>The sensors and actions every system has, read and done through what the system registered for them.</summary>
public static class SystemEntities
{
    /// <summary>
    /// Adds them to the service. The system adds its <see cref="Abstractions.ISystemReadings"/>,
    /// <see cref="Abstractions.IPowerControl"/> and <see cref="Abstractions.IScreenLock"/> first.
    /// </summary>
    public static IServiceCollection AddSystemEntities(this IServiceCollection services)
    {
        services.AddHostedService<CpuLoadSensor>();
        services.AddHostedService<MemoryUsageSensor>();
        services.AddHostedService<BatterySensor>();
        services.AddHostedService<SessionLockSensor>();
        services.AddHostedService<LastBootSensor>();
        services.AddHostedService<ActiveUserSensor>();
        services.AddHostedService<DiskUsageSensor>();
        services.AddHostedService<RestartRequiredSensor>();
        services.AddHostedService<LockScreenAction>();
        services.AddHostedService<PowerActions>();
        return services;
    }
}
