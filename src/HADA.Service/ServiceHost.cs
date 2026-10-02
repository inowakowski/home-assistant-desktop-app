using System.Diagnostics;
using HADA.Core;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Logging;
using HADA.Core.Messaging;
using HADA.Engine.Mqtt;
using HADA.Engine.WebSocket;
using HADA.Ipc;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Sensors;
using HADA.Service.CustomSensors;
using HADA.Service.Logging;
using HADA.Service.Settings;
using HADA.Service.Updates;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace HADA.Service;

/// <summary>Puts the service together: configuration, logging, engines, sensors and actions.</summary>
public static class ServiceHost
{
    private const string ProcessName = "HADA.Service";

    /// <summary>
    /// Whether this is a copy started from a console while another HADA service is running, e.g. a development
    /// build next to the installed service. The two would read the same settings, connect to the broker under the
    /// same client id and throw each other out in turns, so the copy must not start. The installed service itself
    /// never steps back: a process that merely carries its name cannot keep it from starting.
    /// </summary>
    public static bool IsUnwantedSecondCopy()
    {
        // A portable copy shares nothing with any other HADA, so it need not ask who else is running.
        if (WindowsServiceHelpers.IsWindowsService() || AppInstance.IsPortable)
        {
            return false;
        }

        var services = Process.GetProcessesByName(ProcessName);
        try
        {
            return services.Any(process => process.Id != Environment.ProcessId);
        }
        finally
        {
            foreach (var process in services)
            {
                process.Dispose();
            }
        }
    }

    /// <param name="settingsStore">Where settings and logs live; <c>%ProgramData%\HADA</c> unless a test says otherwise.</param>
    public static HostApplicationBuilder CreateBuilder(string[] args, SettingsStore? settingsStore = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "HADA");

        if (AppInstance.IsPortable)
        {
            // Everything stays in the copy's own folder, and belongs to the user running it: secrets only that
            // user can read, settings that user may change, and a service that ends with the tray app.
            settingsStore ??= new SettingsStore(AppInstance.PortableDataFolder, protectFolder: false);
            SettingsStore.SecretScope = System.Security.Cryptography.DataProtectionScope.CurrentUser;
            builder.Services.Configure<IpcOptions>(options => options.TrustSameUser = true);
            builder.Services.AddHostedService<PortableLifetime>();
        }

        // Settings saved from the tray's settings window, added last so they override appsettings.json.
        settingsStore ??= new SettingsStore();
        var storedSettings = new StoredSettingsConfigurationSource(settingsStore);
        ((IConfigurationBuilder)builder.Configuration).Add(storedSettings);
        builder.Services.AddSingleton(settingsStore);
        builder.Services.AddSingleton(storedSettings.Provider);

        // Recent log entries, shown in the settings window.
        var logBuffer = new LogBuffer();
        builder.Logging.AddProvider(new InMemoryLoggerProvider(logBuffer));
        builder.Services.AddSingleton(logBuffer);

        // And a file, for what happened while nobody was looking. It lives next to the settings, in the same
        // protected folder. Only the service itself protects a folder that already exists: a copy run from a
        // console by another account would otherwise add that account to it.
        if (WindowsServiceHelpers.IsWindowsService() || !Directory.Exists(settingsStore.FolderPath))
        {
            settingsStore.TryEnsureFolder();
        }

        builder.Logging.AddProvider(new FileLoggerProvider(
            new FileLoggerOptions { FilePath = Path.Combine(settingsStore.FolderPath, "logs", "service.log") }));

        // A sensor that fails must not take the connection to Home Assistant and every other sensor down with it.
        // The host logs the failure; the rest of the service keeps running.
        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            options.ShutdownTimeout = TimeSpan.FromSeconds(15);
        });

        builder.Services.AddSingleton<IEventBus, ChannelEventBus>();
        builder.Services.AddSingleton<IEntityRegistry, EntityRegistry>();

        builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection(MqttOptions.SectionName));
        builder.Services.Configure<HaWebSocketOptions>(builder.Configuration.GetSection(HaWebSocketOptions.SectionName));
        builder.Services.Configure<EntityOptions>(builder.Configuration.GetSection(EntityOptions.SectionName));
        builder.Services.Configure<CustomSensorOptions>(builder.Configuration.GetSection(CustomSensorOptions.SectionName));
        builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));

        builder.Services.AddSingleton<TelemetryCache>();
        builder.Services.AddSingleton<EngineSupervisor>();
        builder.Services.AddSingleton<UpdateChecker>();
        builder.Services.AddSingleton<IServiceControl, ServiceControl>();

        // Hosted services start in this order: everything that listens starts before the sensors and clients that feed it.
        builder.Services.AddHostedService(services => services.GetRequiredService<TelemetryCache>());
        builder.Services.AddHostedService(services => services.GetRequiredService<EngineSupervisor>());
        builder.Services.AddHostedService<IpcServer>();
        builder.Services.AddHostedService<CpuLoadSensor>();
        builder.Services.AddHostedService<MemoryUsageSensor>();
        builder.Services.AddHostedService<BatterySensor>();
        builder.Services.AddHostedService<PowerStateSensor>();
        builder.Services.AddHostedService<SessionLockSensor>();
        builder.Services.AddHostedService<LastBootSensor>();
        builder.Services.AddHostedService<ActiveUserSensor>();
        builder.Services.AddHostedService<NetworkSensor>();
        builder.Services.AddHostedService<DiskUsageSensor>();
        builder.Services.AddHostedService<GpuLoadSensor>();
        builder.Services.AddHostedService<LockScreenAction>();
        builder.Services.AddHostedService<PowerActions>();
        builder.Services.AddHostedService(services => services.GetRequiredService<UpdateChecker>());

        // After the built-in entities, so a custom sensor can never take one of their ids first.
        builder.Services.AddHostedService<CustomSensorHost>();
        return builder;
    }
}
