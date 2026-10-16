namespace HADA.Core.Entities;

/// <summary>
/// What Home Assistant is told about the built-in entities whose sensors each system has its own way of keeping,
/// so they are the same entity everywhere: the same name, kind and icon on Windows as on Linux.
/// </summary>
public static class BuiltInDescriptors
{
    public static EntityDescriptor Lid { get; } = new()
    {
        Id = BuiltInEntityIds.LidOpen,
        Name = "Lid",
        Kind = EntityKind.BinarySensor,
        DeviceClass = "opening",
        Icon = "mdi:laptop",
    };

    public static EntityDescriptor ExternalDisplay { get; } = new()
    {
        Id = BuiltInEntityIds.ExternalDisplay,
        Name = "External display",
        Kind = EntityKind.BinarySensor,
        DeviceClass = "connectivity",
        Icon = "mdi:monitor-multiple",
    };

    public static EntityDescriptor DoNotDisturb { get; } = new()
    {
        Id = BuiltInEntityIds.DoNotDisturb,
        Name = "Do not disturb",
        Kind = EntityKind.BinarySensor,
        Icon = "mdi:bell-off",
    };

    public static EntityDescriptor GpuLoad { get; } = new()
    {
        Id = BuiltInEntityIds.GpuLoad,
        Name = "GPU load",
        Kind = EntityKind.Sensor,
        Icon = "mdi:expansion-card",
        UnitOfMeasurement = "%",
        StateClass = "measurement",
    };

    public static EntityDescriptor ActiveWindow { get; } = new()
    {
        Id = BuiltInEntityIds.ActiveWindow,
        Name = "Active window",
        Kind = EntityKind.Sensor,
        Icon = "mdi:application-outline",
    };

    public static EntityDescriptor Display { get; } = new()
    {
        Id = BuiltInEntityIds.DisplayOn,
        Name = "Display",
        Kind = EntityKind.BinarySensor,
        Icon = "mdi:monitor",
    };

    public static EntityDescriptor TurnOffDisplay { get; } = new()
    {
        Id = BuiltInEntityIds.TurnOffDisplay,
        Name = "Turn off display",
        Kind = EntityKind.Button,
        Icon = "mdi:monitor-off",
    };

    public static EntityDescriptor WakeDisplay { get; } = new()
    {
        Id = BuiltInEntityIds.WakeDisplay,
        Name = "Wake display",
        Kind = EntityKind.Button,
        Icon = "mdi:monitor-shimmer",
    };

    public static EntityDescriptor ScreenBrightness { get; } = new()
    {
        Id = BuiltInEntityIds.ScreenBrightness,
        Name = "Screen brightness",
        Kind = EntityKind.Number,
        Icon = "mdi:brightness-6",
        UnitOfMeasurement = "%",
        Min = 0,
        Max = 100,
        Step = 1,
    };

    public static EntityDescriptor MediaPlayback { get; } = new()
    {
        Id = BuiltInEntityIds.MediaPlayback,
        Name = "Media playback",
        Kind = EntityKind.Sensor,
        Icon = "mdi:music-circle",
    };

    public static EntityDescriptor MediaPlayer { get; } = new()
    {
        Id = BuiltInEntityIds.MediaPlayer,
        Name = "Media player",
        Kind = EntityKind.MediaPlayer,
        Icon = "mdi:play-box-outline",
    };

    public static EntityDescriptor MediaPlayPause { get; } = new()
    {
        Id = BuiltInEntityIds.MediaPlayPause,
        Name = "Media play/pause",
        Kind = EntityKind.Button,
        Icon = "mdi:play-pause",
    };

    public static EntityDescriptor MediaNext { get; } = new()
    {
        Id = BuiltInEntityIds.MediaNext,
        Name = "Media next",
        Kind = EntityKind.Button,
        Icon = "mdi:skip-next",
    };

    public static EntityDescriptor MediaPrevious { get; } = new()
    {
        Id = BuiltInEntityIds.MediaPrevious,
        Name = "Media previous",
        Kind = EntityKind.Button,
        Icon = "mdi:skip-previous",
    };

    public static EntityDescriptor MediaStop { get; } = new()
    {
        Id = BuiltInEntityIds.MediaStop,
        Name = "Media stop",
        Kind = EntityKind.Button,
        Icon = "mdi:stop",
    };
}
