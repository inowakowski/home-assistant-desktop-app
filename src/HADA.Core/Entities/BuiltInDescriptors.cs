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
}
