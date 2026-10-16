using System.Globalization;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Sensors;
using HADA.Core.Unix;
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

    public IReadOnlyList<string>? AppsUsing(CaptureDevice device) => device == CaptureDevice.Camera
        ? AppsWithOpen("/proc", path => path.StartsWith("/dev/video", StringComparison.Ordinal))
        : _run("pactl", ["list", "source-outputs"]) is { Succeeded: true } result ? ParseRecordingApps(result.Output) : null;

    /// <summary>
    /// The apps that have a file open, by the name of their program: what the kernel lists in <c>/proc</c> for
    /// each process, the user's own processes being the ones it shows. <see langword="null"/> where there is no
    /// <c>/proc</c>.
    /// </summary>
    public static IReadOnlyList<string>? AppsWithOpen(string procRoot, Func<string, bool> isWanted)
    {
        if (!Directory.Exists(procRoot))
        {
            return null;
        }

        var apps = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var process in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(process), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            try
            {
                foreach (var descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(process, "fd")))
                {
                    if (new FileInfo(descriptor).LinkTarget is { } target && isWanted(target))
                    {
                        apps.Add(Proc.ReadText(Path.Combine(process, "comm"))?.Trim() is { Length: > 0 } name ? name : Path.GetFileName(process));
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process, or one that ended while being looked at.
            }
        }

        return [.. apps];
    }

    /// <summary>
    /// The apps recording, from <c>pactl list source-outputs</c>: a block for each stream that records, its
    /// <c>application.name</c> among the properties.
    /// </summary>
    public static IReadOnlyList<string> ParseRecordingApps(string output)
    {
        var apps = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            const string Name = "application.name = ";
            if (line.StartsWith(Name, StringComparison.Ordinal))
            {
                apps.Add(line[Name.Length..].Trim('"'));
            }
        }

        return [.. apps];
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

        // Whether the camera and the microphone record, and for whom.
        services.AddHostedService<MediaCaptureSensor>();

        // The screens, the window in front, quiet hours, and the backlight.
        services.AddSingleton<LinuxHardware>(_ => new LinuxHardware());
        services.AddSingleton<IDisplayReadings>(provider => new LinuxDisplayReadings(provider.GetRequiredService<LinuxHardware>()));
        services.AddDisplayEntities();
        services.AddHostedService(provider => new DoNotDisturbSensor(
            provider.GetRequiredService<Core.Abstractions.IEventBus>(), provider.GetRequiredService<Core.Abstractions.IEntityRegistry>()));
        services.AddSingleton<IDoNotDisturb>(_ => new LinuxDoNotDisturb());
        services.AddHostedService<DoNotDisturbControl>();
        services.AddSingleton<IScreenBrightness>(provider => new LinuxBacklight(provider.GetRequiredService<LinuxHardware>()));
        services.AddHostedService<ScreenBrightnessControl>();

        // Whether the screens are on, and turning them off and on.
        services.AddSingleton<IDisplayPower>(_ => new LinuxDisplayPower());
        services.AddHostedService<DisplayPowerSensor>();
        services.AddHostedService<DisplayPowerActions>();

        // Notifications, shown by the desktop once the agent has connected to it.
        services.AddSingleton<NotificationPresenter>();
        services.AddHostedService<NotificationAction>();

        // Sound, where the system has a sound server's tool to ask; without one there is nothing to control.
        if (LinuxAudioDevices.ForThisSystem() is { } audio)
        {
            services.AddSingleton<IAudioDevices>(audio);
            services.AddHostedService<AudioVolumeSensor>();
            services.AddHostedService<AudioDeviceSensor>();
            services.AddHostedService<MicrophoneMuteSensor>();
            services.AddHostedService<AudioControl>();
        }

        // Keys pressed for the service's buttons, where the session is an X11 one.
        services.AddSingleton<IKeyboard>(_ => new X11Keyboard());
        services.AddHostedService<KeyPressAction>();

        // What plays, and the player to tell, through MPRIS on the session's bus.
        services.AddSingleton<IMediaSessions>(_ => new LinuxMedia());
        services.AddHostedService<MediaPlaybackSensor>();
        services.AddHostedService<MediaButtons>();
        services.AddHostedService<MediaPlayerControl>();

        return services;
    }
}
