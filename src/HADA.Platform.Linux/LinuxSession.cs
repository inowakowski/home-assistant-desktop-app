using System.Globalization;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Sensors;
using Microsoft.Extensions.DependencyInjection;

namespace HADA.Platform.Linux;

/// <summary>What the sensors of the user's session read on Linux, from the desktop over D-Bus.</summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
public sealed class LinuxSessionReadings(Func<string, string[], ToolResult?>? run = null) : ISessionReadings
{
    // The desktops say how long nobody touched anything in different places: GNOME's Mutter in one, KDE and
    // others in the screen saver's. Each is asked until one answers, and then only that one.
    private static readonly string[][] IdleSources =
    [
        ["--user", "call", "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core", "org.gnome.Mutter.IdleMonitor", "GetIdletime"],
        ["--user", "call", "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "GetSessionIdleTime"],
    ];

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private string[]? _idleSource;

    public TimeSpan? ReadIdleTime()
    {
        if (_idleSource is { } known)
        {
            return Idle(known);
        }

        foreach (var source in IdleSources)
        {
            if (Idle(source) is { } idle)
            {
                _idleSource = source;
                return idle;
            }
        }

        return null;
    }

    /// <summary>Reads <c>busctl</c>'s answer, a type letter and a number of milliseconds: <c>t 1234</c> or <c>u 1234</c>.</summary>
    public static TimeSpan? ParseIdle(string output)
    {
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["t" or "u", var number]
            && ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
    }

    private TimeSpan? Idle(string[] source) =>
        _run("busctl", source) is { Succeeded: true } result ? ParseIdle(result.Output) : null;
}

[SupportedOSPlatform("linux")]
public static class LinuxSession
{
    /// <summary>The sensors and actions of the user's session, which the app's agent runs.</summary>
    /// <param name="activeWindow">How long after the last input the user still counts as active.</param>
    public static IServiceCollection AddLinuxSessionEntities(this IServiceCollection services, Func<TimeSpan> activeWindow)
    {
        services.AddSingleton<ISessionReadings>(_ => new LinuxSessionReadings());
        services.AddHostedService(provider => new UserActivitySensor(
            provider.GetRequiredService<Core.Abstractions.IEventBus>(),
            provider.GetRequiredService<Core.Abstractions.IEntityRegistry>(),
            provider.GetRequiredService<ISessionReadings>(),
            activeWindow));

        // Notifications, shown by the desktop once the agent has connected to it.
        services.AddSingleton<NotificationPresenter>();
        services.AddHostedService<NotificationAction>();

        // Sound, where the system has a sound server's tool to ask; without one there is nothing to control.
        if (LinuxAudioDevices.ForThisSystem() is { } audio)
        {
            services.AddSingleton<IAudioDevices>(audio);
            services.AddHostedService<AudioVolumeSensor>();
            services.AddHostedService<AudioControl>();
        }

        return services;
    }
}
