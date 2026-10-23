using System.Collections.Frozen;

namespace HADA.Core.Entities;

/// <summary>
/// The ids of the entities HADA brings along. They are what Home Assistant knows an entity by, so they are the same
/// on every operating system; which of them a computer actually registers depends on the system and its hardware.
/// </summary>
public static class BuiltInEntityIds
{
    public const string CpuLoad = "cpu_load";
    public const string MemoryUsage = "memory_usage";
    public const string BatteryLevel = "battery_level";
    public const string BatteryCharging = "battery_charging";
    public const string PluggedIn = "plugged_in";
    public const string DisplayOn = "display_on";
    public const string LidOpen = "lid_open";
    public const string SessionLocked = "session_locked";
    public const string LastBoot = "last_boot";
    public const string LockScreen = "lock_screen";
    public const string ActiveWindow = "active_window";
    public const string AudioVolume = "audio_volume";
    public const string UserActive = "user_active";
    public const string MicrophoneInUse = "microphone_in_use";
    public const string CameraInUse = "camera_in_use";
    public const string MicrophoneMuted = "microphone_muted";
    public const string ExternalDisplay = "external_display";
    public const string GpuLoad = "gpu_load";
    public const string IpAddress = "ip_address";
    public const string WifiNetwork = "wifi_network";
    public const string ActiveUser = "active_user";
    public const string AudioDevice = "audio_device";
    public const string DoNotDisturb = "do_not_disturb";
    public const string Sleep = "sleep";
    public const string Hibernate = "hibernate";
    public const string Shutdown = "shutdown";
    public const string Restart = "restart";
    public const string TurnOffDisplay = "turn_off_display";
    public const string WakeDisplay = "wake_display";
    public const string MediaPlayPause = "media_play_pause";
    public const string MediaNext = "media_next";
    public const string MediaPrevious = "media_previous";
    public const string MediaStop = "media_stop";
    public const string VolumeLevel = "volume_level";
    public const string AudioMute = "audio_mute";
    public const string MicrophoneMute = "microphone_mute";
    public const string Notification = "notification";
    public const string MediaPlayback = "media_playback";
    public const string MediaPlayer = "media_player";
    public const string UpdateAvailable = "update_available";
    public const string RestartRequired = "restart_required";
    public const string ScreenBrightness = "screen_brightness";
    public const string SilenceNotifications = "silence_notifications";
    public const string IdleTime = "idle_time";
    public const string NetworkDownload = "network_download";
    public const string NetworkUpload = "network_upload";
    public const string KeepAwake = "keep_awake";

    private const string DiskPrefix = "disk_";
    private const string DiskSuffix = "_usage";
    private static readonly System.Buffers.SearchValues<char> DiskKeyCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_");

    /// <summary>Every fixed id, including those of entities absent on this computer.</summary>
    public static FrozenSet<string> All { get; } = new[]
    {
        CpuLoad,
        MemoryUsage,
        BatteryLevel,
        BatteryCharging,
        PluggedIn,
        DisplayOn,
        LidOpen,
        SessionLocked,
        LastBoot,
        LockScreen,
        ActiveWindow,
        AudioVolume,
        UserActive,
        MicrophoneInUse,
        CameraInUse,
        MicrophoneMuted,
        ExternalDisplay,
        GpuLoad,
        IpAddress,
        WifiNetwork,
        ActiveUser,
        AudioDevice,
        DoNotDisturb,
        Sleep,
        Hibernate,
        Shutdown,
        Restart,
        TurnOffDisplay,
        WakeDisplay,
        MediaPlayPause,
        MediaNext,
        MediaPrevious,
        MediaStop,
        VolumeLevel,
        AudioMute,
        MicrophoneMute,
        Notification,
        MediaPlayback,
        MediaPlayer,
        UpdateAvailable,
        RestartRequired,
        ScreenBrightness,
        SilenceNotifications,
        IdleTime,
        NetworkDownload,
        NetworkUpload,
        KeepAwake,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The id of a disk's usage sensor, from its key: a drive letter on Windows, <c>disk_c_usage</c>; a name made of
    /// the mount point elsewhere, <c>disk_root_usage</c>, <c>disk_home_usage</c>.
    /// </summary>
    public static string DiskUsage(string key) => $"{DiskPrefix}{key.ToLowerInvariant()}{DiskSuffix}";

    /// <summary>Whether an id is that of a disk's usage sensor, which have no fixed list.</summary>
    public static bool IsDiskUsage(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length <= DiskPrefix.Length + DiskSuffix.Length
            || !id.StartsWith(DiskPrefix, StringComparison.Ordinal)
            || !id.EndsWith(DiskSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        // A key starts with a letter and has letters, digits and underscores after it.
        var key = id.AsSpan(DiskPrefix.Length, id.Length - DiskPrefix.Length - DiskSuffix.Length);
        return key[0] is >= 'a' and <= 'z' && !key.ContainsAnyExcept(DiskKeyCharacters);
    }

    /// <summary>Whether an id belongs to a built-in entity, so nothing the user defines may take it.</summary>
    public static bool Contains(string id) => All.Contains(id) || IsDiskUsage(id);
}
