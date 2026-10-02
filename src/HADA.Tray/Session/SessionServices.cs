using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Ipc;
using HADA.Platform.Windows.Actions;
using HADA.Platform.Windows.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HADA.Tray.Session;

/// <summary>Everything the tray app runs in the user's session, apart from its icon.</summary>
public static class SessionServices
{
    public static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        // A sensor or action that fails must not take the others and the tray icon down with it.
        services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        services.AddSingleton<IEventBus, ChannelEventBus>();
        services.AddSingleton<IEntityRegistry, EntityRegistry>();
        services.Configure<IpcOptions>(options => options.ClientName = $"tray:{Environment.UserName}");

        // The IPC client starts first so it is subscribed before the sensors publish anything.
        services.AddSingleton<IpcClient>();
        services.AddHostedService(provider => provider.GetRequiredService<IpcClient>());

        // What Home Assistant gets to know about this session.
        services.AddHostedService<ActiveWindowSensor>();
        services.AddHostedService<AudioVolumeSensor>();
        services.AddHostedService(provider => new UserActivitySensor(
            provider.GetRequiredService<IEventBus>(),
            provider.GetRequiredService<IEntityRegistry>(),
            () => TimeSpan.FromSeconds(UserPreferences.IdleSeconds)));
        services.AddHostedService<MediaCaptureSensor>();
        services.AddHostedService<MicrophoneMuteSensor>();
        services.AddHostedService<ExternalDisplaySensor>();
        services.AddHostedService<AudioDeviceSensor>();
        services.AddHostedService<DoNotDisturbSensor>();
        services.AddHostedService<MediaPlaybackSensor>();

        // What Home Assistant can do in it.
        services.AddSingleton<NotificationPresenter>();
        services.AddHostedService<AudioControl>();
        services.AddHostedService<MediaKeyActions>();
        services.AddHostedService<DisplayActions>();
        services.AddHostedService<NotificationAction>();
        services.AddHostedService<LaunchAction>();
        services.AddHostedService<KeyPressAction>();

        // What the user can tell Home Assistant from here.
        services.AddSingleton<QuickActions>();
        services.AddHostedService(provider => provider.GetRequiredService<QuickActions>());
        return services;
    }
}
