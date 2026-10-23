using System.Collections.ObjectModel;
using System.IO;
using HADA.Core.Entities;
using HADA.Ipc;
using HADA.Presentation;
using HADA.Presentation.Localization;
using HADA.Presentation.Mvvm;

namespace HADA.Presentation.ViewModels;

public enum StatusKind
{
    Neutral,
    Success,
    Warning,
    Error,
}

/// <summary>State of a message bar: title, message, severity and whether it is shown.</summary>
public sealed class InfoBarViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _message = string.Empty;
    private Severity _severity = Severity.Informational;
    private bool _isOpen;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public Severity Severity
    {
        get => _severity;
        private set => SetProperty(ref _severity, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public void Show(string title, string message, Severity severity)
    {
        Title = title;
        Message = message;
        Severity = severity;
        IsOpen = true;
    }

    public void Close() => IsOpen = false;
}

public static class ServiceErrors
{
    /// <summary>Failures the window handles itself instead of treating as bugs.</summary>
    public static bool IsExpected(Exception exception) =>
        exception is ServiceUnavailableException or ServiceControlException or TimeoutException
            or InvalidDataException or IOException or ObjectDisposedException;

    public static string Describe(Exception exception) => exception switch
    {
        ServiceUnavailableException => Loc.Get("Error_ServiceUnavailable"),
        ServiceControlException { Error: IpcError.Unauthorized } => Loc.Get("Error_Unauthorized"),
        _ => exception.Message,
    };
}

public static class EntityVisuals
{
    public static Glyph SymbolFor(EntityDescriptor entity) => entity.Id switch
    {
        "cpu_load" => Glyph.DeveloperBoard,
        "active_window" => Glyph.Window,
        "audio_volume" => Glyph.Speaker2,
        "lock_screen" => Glyph.LockClosed,
        "memory_usage" => Glyph.Storage,
        "battery_level" => Glyph.Battery5,
        "battery_charging" => Glyph.BatteryCharge,
        "plugged_in" => Glyph.PlugConnected,
        "display_on" => Glyph.Desktop,
        "lid_open" => Glyph.Laptop,
        "session_locked" => Glyph.LockClosed,
        "last_boot" => Glyph.Clock,
        "user_active" => Glyph.Person,
        "microphone_in_use" => Glyph.Mic,
        "microphone_muted" => Glyph.MicOff,
        "external_display" => Glyph.DesktopMac,
        "camera_in_use" => Glyph.Video,
        "ip_address" => Glyph.Globe,
        "wifi_network" => Glyph.Wifi1,
        "active_user" => Glyph.PersonCircle,
        "audio_device" => Glyph.Headphones,
        "do_not_disturb" => Glyph.AlertOff,
        "media_playback" => Glyph.MusicNote2,
        "media_player" => Glyph.PlayCircle,
        "update_available" => Glyph.ArrowDownload,
        "restart_required" => Glyph.ArrowSync,
        "screen_brightness" => Glyph.BrightnessHigh,
        "gpu_load" => Glyph.DeveloperBoard,
        "sleep" or "hibernate" => Glyph.WeatherMoon,
        "shutdown" => Glyph.Power,
        "restart" => Glyph.ArrowClockwise,
        "turn_off_display" or "wake_display" => Glyph.Desktop,
        "media_play_pause" => Glyph.Play,
        "media_next" => Glyph.Next,
        "media_previous" => Glyph.Previous,
        "media_stop" => Glyph.Stop,
        "volume_level" => Glyph.Speaker2,
        "audio_mute" => Glyph.SpeakerMute,
        "microphone_mute" => Glyph.MicOff,
        "notification" => Glyph.Alert,
        "idle_time" => Glyph.Timer,
        "network_download" => Glyph.ArrowDownload,
        "network_upload" => Glyph.ArrowUpload,
        "keep_awake" => Glyph.DrinkCoffee,
        _ when entity.Id.StartsWith("disk_", StringComparison.Ordinal) && entity.Id.EndsWith("_usage", StringComparison.Ordinal)
            => Glyph.Database,
        _ => entity.Kind switch
        {
            EntityKind.Button => Glyph.Circle,
            EntityKind.BinarySensor or EntityKind.Switch => Glyph.ToggleLeft,
            EntityKind.Number => Glyph.NumberSymbol,
            EntityKind.Notify => Glyph.Alert,
            EntityKind.Trigger => Glyph.Flash,
            _ => Glyph.Pulse,
        },
    };

    public static string KindText(EntityDescriptor entity) => Loc.Get(entity.Kind switch
    {
        EntityKind.Button => "Entity_Button",
        EntityKind.BinarySensor => "Entity_BinarySensor",
        EntityKind.Switch => "Entity_Switch",
        EntityKind.Number => "Entity_Number",
        EntityKind.Notify => "Entity_Notify",
        EntityKind.Trigger => "Entity_Trigger",
        EntityKind.MediaPlayer => "Entity_MediaPlayer",
        _ => "Entity_Sensor",
    });

    /// <summary>The state as shown in the window: on/off in the user's language, numbers with their unit.</summary>
    public static string FormatState(EntityDescriptor entity, string? state)
    {
        if (entity.Kind == EntityKind.MediaPlayer && state is not null)
        {
            // Playing, paused or idle, as it is reported.
            return state;
        }

        if (!entity.Kind.ReportsState() || state is null)
        {
            return "—";
        }

        if (entity.Kind.IsBinary())
        {
            return state == BinaryState.On ? Loc.Get("Common_On") : state == BinaryState.Off ? Loc.Get("Common_Off") : state;
        }

        if (entity.DeviceClass == "timestamp" && DateTimeOffset.TryParse(state, Loc.Culture, out var timestamp))
        {
            return timestamp.ToLocalTime().ToString("g", Loc.Culture);
        }

        return entity.UnitOfMeasurement is { } unit ? $"{state} {unit}" : state;
    }

    /// <summary><c>service</c>, <c>custom</c> or <c>tray:user</c>, as reported by the service.</summary>
    public static string FormatSource(string source) =>
        source == "service" ? Loc.Get("Source_Service")
        : source == "custom" ? Loc.Get("Source_Custom")
        : source.StartsWith("tray:", StringComparison.Ordinal) ? Loc.Format("Source_Tray", source["tray:".Length..])
        : source;

    /// <summary>
    /// What the Entities page says about an entity; where Linux and macOS do it otherwise, what they do, and where
    /// macOS does it otherwise still, what it does.
    /// </summary>
    public static string HintFor(string entityId) =>
        (OperatingSystem.IsMacOS() ? Loc.TryGet($"EntityHint_{entityId}_MacOS") : null)
        ?? (OperatingSystem.IsWindows() ? null : Loc.TryGet($"EntityHint_{entityId}_Unix"))
        ?? Loc.TryGet($"EntityHint_{entityId}")
        ?? (entityId.StartsWith("disk_", StringComparison.Ordinal) ? Loc.TryGet("EntityHint_disk_usage") : null)
        ?? string.Empty;
}

public static class CollectionSync
{
    /// <summary>Updates rows in place, keyed by id, so the list does not flicker or lose scroll position on refresh.</summary>
    public static void Sync<TRow, TItem>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> items,
        Func<TItem, string> itemKey,
        Func<TRow, string> rowKey,
        Func<string, TRow> create,
        Action<TRow, TItem> update)
    {
        var wanted = items.Select(itemKey).ToHashSet(StringComparer.Ordinal);
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(rowKey(rows[i])))
            {
                rows.RemoveAt(i);
            }
        }

        for (var index = 0; index < items.Count; index++)
        {
            var key = itemKey(items[index]);
            var row = rows.FirstOrDefault(candidate => rowKey(candidate) == key);
            if (row is null)
            {
                row = create(key);
                rows.Insert(index, row);
            }
            else if (rows.IndexOf(row) is var current && current != index)
            {
                rows.Move(current, index);
            }

            update(row, items[index]);
        }
    }
}
