using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Actions;
using HADA.Core.Sensors;
using HADA.Core.Unix;
using Microsoft.Extensions.DependencyInjection;

namespace HADA.Platform.MacOS;

[SupportedOSPlatform("macos")]
public static class MacSession
{
    /// <summary>The sensors and actions of the user's session, which the app's agent runs.</summary>
    /// <param name="activeWindow">How long after the last input the user still counts as active.</param>
    public static IServiceCollection AddMacSessionEntities(this IServiceCollection services, Func<TimeSpan> activeWindow)
    {
        services.AddSingleton<ISessionReadings, MacSessionReadings>();
        services.AddHostedService(provider => new UserActivitySensor(
            provider.GetRequiredService<IEventBus>(),
            provider.GetRequiredService<IEntityRegistry>(),
            provider.GetRequiredService<ISessionReadings>(),
            activeWindow));
        services.AddHostedService<IdleTimeSensor>();

        // Whether the microphone and the camera record, though not for whom.
        services.AddHostedService<MediaCaptureSensor>();

        // The screens, and the app in front; the screens off and on, and the brightness of the Mac's own.
        services.AddSingleton<IDisplayReadings, MacDisplayReadings>();
        services.AddDisplayEntities();
        services.AddSingleton<IDisplayPower, MacDisplayPower>();
        services.AddHostedService<DisplayPowerSensor>();
        services.AddHostedService<DisplayPowerActions>();
        services.AddSingleton<IScreenBrightness, MacScreenBrightness>();
        services.AddHostedService<ScreenBrightnessControl>();

        // The dark look, and signing out.
        services.AddSingleton<IDarkMode>(_ => new MacDarkMode());
        services.AddHostedService<DarkModeControl>();
        services.AddSingleton<ISignOut>(_ => new MacSignOut());
        services.AddHostedService<SignOutAction>();

        // Keys pressed for the service's buttons, and the media keys, once allowed under Accessibility.
        services.AddSingleton<IKeyboard, MacKeyboard>();
        services.AddHostedService<KeyPressAction>();
        services.AddSingleton<IMediaSessions, MacMediaKeys>();
        services.AddHostedService<MediaButtons>();

        // Notifications, shown by Notification Center once the agent has started.
        services.AddSingleton<NotificationPresenter>();
        services.AddHostedService<NotificationAction>();

        // Sound.
        services.AddSingleton<IAudioDevices, MacAudioDevices>();
        services.AddHostedService<AudioVolumeSensor>();
        services.AddHostedService<AudioDeviceSensor>();
        services.AddHostedService<MicrophoneMuteSensor>();
        services.AddHostedService<AudioControl>();
        return services;
    }
}

/// <summary>
/// What the session's sensors read on macOS: the time since the last input, from Core Graphics, and whether the
/// microphone and the camera record, from Core Audio and CoreMediaIO. Which apps record macOS keeps to itself.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacSessionReadings : ISessionReadings
{
    public TimeSpan? ReadIdleTime() => MacNative.ReadIdleTime();

    public IReadOnlyList<string>? AppsUsing(CaptureDevice device) => null;

    public bool? IsInUse(CaptureDevice device) =>
        device == CaptureDevice.Microphone ? MacNative.IsMicrophoneInUse() : MacNative.IsCameraInUse();
}

/// <summary>
/// The screens, from Core Graphics, and the app in front, as <c>NSWorkspace</c> tells every app. The titles of
/// other apps' windows macOS shows only to apps allowed to record the screen, so the app's name stands for them.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacDisplayReadings : IDisplayReadings
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private readonly IntPtr _workspaceClass;

    public MacDisplayReadings()
    {
        // The app has AppKit loaded already; a test run may not.
        _ = NativeLibrary.TryLoad(AppKit, out _);
        _workspaceClass = objc_getClass("NSWorkspace");
    }

    public DisplayCount? ReadDisplays() => MacNative.ReadDisplays() is { } displays ? new DisplayCount(displays.Total, displays.External) : null;

    public bool HasActiveWindow => _workspaceClass != IntPtr.Zero;

    public ActiveWindowInfo? ReadActiveWindow()
    {
        if (_workspaceClass == IntPtr.Zero)
        {
            return null;
        }

        // What AppKit hands out is let go of with the pool, as there is no run loop on this thread to do it.
        var pool = objc_autoreleasePoolPush();
        try
        {
            var workspace = objc_msgSend(_workspaceClass, sel_registerName("sharedWorkspace"));
            var app = workspace == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(workspace, sel_registerName("frontmostApplication"));
            if (app == IntPtr.Zero)
            {
                return null;
            }

            var name = objc_msgSend(app, sel_registerName("localizedName"));
            var text = name == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(name, sel_registerName("UTF8String"));
            return text == IntPtr.Zero ? null : new ActiveWindowInfo(string.Empty, Marshal.PtrToStringUTF8(text));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    [DllImport(ObjC, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);
}

/// <summary>
/// The sound through AppleScript's volume settings, which every Mac has, and the devices' names through Core Audio.
/// AppleScript knows no mute for the input: muting the microphone sets its volume to nothing, and unmuting brings
/// back the volume it had.
/// </summary>
/// <remarks>Read by a sensor and by the controls every second; a reading stays good for a moment, so AppleScript runs once for both.</remarks>
[SupportedOSPlatform("macos")]
public sealed class MacAudioDevices(Func<string, string[], ToolResult?>? run = null) : IAudioDevices
{
    private const int DefaultInputVolume = 75;
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(1500);

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private readonly Lock _lock = new();
    private (DateTime At, VolumeSettings? Settings) _last;
    private int _inputVolumeBeforeMute = DefaultInputVolume;

    public AudioLevel? Read(AudioEndpoint endpoint)
    {
        if (Settings() is not { } settings)
        {
            return null;
        }

        return endpoint == AudioEndpoint.Microphone
            ? settings.InputVolume is { } input ? new AudioLevel(input, IsMuted: input == 0) : null
            : settings.OutputVolume is { } output ? new AudioLevel(output, settings.OutputMuted ?? false) : null;
    }

    public string? ReadName(AudioEndpoint endpoint) => MacNative.ReadAudioDeviceName(input: endpoint == AudioEndpoint.Microphone);

    public bool TrySetVolume(int percent) =>
        Script($"set volume output volume {Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture)}");

    public bool TrySetMute(AudioEndpoint endpoint, bool muted)
    {
        if (endpoint == AudioEndpoint.Speakers)
        {
            return Script($"set volume output muted {(muted ? "true" : "false")}");
        }

        if (muted)
        {
            if (Read(AudioEndpoint.Microphone) is { VolumePercent: > 0 } level)
            {
                _inputVolumeBeforeMute = level.VolumePercent;
            }

            return Script("set volume input volume 0");
        }

        return Script($"set volume input volume {_inputVolumeBeforeMute.ToString(CultureInfo.InvariantCulture)}");
    }

    private VolumeSettings? Settings()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _last.At < Fresh)
            {
                return _last.Settings;
            }

            var settings = _run("osascript", ["-e", "get volume settings"]) is { Succeeded: true } result
                ? MacParsers.ParseVolumeSettings(result.Output)
                : (VolumeSettings?)null;
            _last = (DateTime.UtcNow, settings);
            return settings;
        }
    }

    /// <summary>Whether AppleScript did it; the next reading asks again, to show the new state at once.</summary>
    private bool Script(string command)
    {
        var done = _run("osascript", ["-e", command]) is { Succeeded: true };
        lock (_lock)
        {
            _last = default;
        }

        return done;
    }
}
