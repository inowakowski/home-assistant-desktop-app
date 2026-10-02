# Adding entities in code

For a value a PowerShell command can print, a [custom sensor](../features/custom-entities.md) is enough and needs no build. Write code when the value needs a Windows API, has to be reported the moment it changes, or should ship with HADA.

## A sensor

**1. Decide where it runs.**

| Runs in | Choose it when | Registered in |
|---|---|---|
| Service | The value is the same for the whole computer and should be reported with nobody signed in: hardware, power, disks, network | `src/HADA.Service/ServiceHost.cs` |
| Tray | The value belongs to the signed-in user's desktop: windows, audio devices, keyboard and mouse, per-user registry | `src/HADA.Tray/Session/SessionServices.cs` |

A tray entity turns *unavailable* in Home Assistant while the tray app is not running, or while another user is at the computer.

**2. Add a class** to `src/HADA.Platform.Windows/Sensors`. A sensor is a `BackgroundService` that registers its entity once and then publishes readings:

```csharp
using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>Publishes the free space on the system drive.</summary>
public sealed class DiskFreeSensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string EntityId = "disk_free";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Free disk space",
                Kind = EntityKind.Sensor,
                Icon = "mdi:harddisk",
                UnitOfMeasurement = "GB",
                StateClass = "measurement",
            },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var freeGigabytes = new DriveInfo("C").AvailableFreeSpace / 1_000_000_000;
            await publisher.PublishAsync(
                EntityId, freeGigabytes.ToString(CultureInfo.InvariantCulture), cancellationToken: stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

- `Id` is the entity ID: lowercase letters, digits and underscores, unique on this computer.
- `Kind` is `Sensor` for text and numbers or `BinarySensor` for on/off; a binary sensor publishes `BinaryState.On` or `BinaryState.Off` (`BinaryState.From(bool)`).
- `UnitOfMeasurement`, `StateClass` and `DeviceClass` are passed to Home Assistant as they are. Set a unit only for numbers, and format numbers with `CultureInfo.InvariantCulture`.
- `ChangeOnlyPublisher` sends a reading only when the state or the attributes changed, so polling every second costs nothing in Home Assistant. Pass attributes as its third argument.
- States are text of at most 255 characters.
- A sensor never touches MQTT or the WebSocket API. It publishes to the event bus, and whichever engine is configured delivers the reading.

**3. Register it** next to the other sensors, in the file from step 1. In the service, keep it above `CustomSensorHost`.

```csharp
builder.Services.AddHostedService<DiskFreeSensor>();
```

**4. Reserve the ID.** Add `DiskFreeSensor.EntityId` to `BuiltInIds` in `src/HADA.Service/CustomSensors/CustomSensorRules.cs`, so that a custom sensor cannot take the same ID.

**5. Optional polish** for the settings window, in `src/HADA.Tray`:

- an icon: a line in `EntityVisuals.SymbolFor` in `ViewModels/Support.cs`
- a hint under the entity's name: `EntityHint_disk_free` in both `Localization/Strings.resx` and `Localization/Strings.pl.resx`

**6. Try it.** Run `dotnet test`, then start the service and the tray as described under [Running during development](building.md#running-during-development). The entity appears on the **Overview** page with its value, and in Home Assistant under the device.

Existing sensors to copy from: `MemoryUsageSensor` (polling, the shortest), `BatterySensor` (several entities from one reading, registered only when the hardware is there), `PowerStateSensor` (reports changes from a Windows notification instead of polling), `MicrophoneMuteSensor` (a tray sensor with an attribute).

## A button, switch or number

These are entities Home Assistant sends commands to. Derive from `CommandHandler` in `src/HADA.Platform.Windows/Actions`: it registers the entities and hands every command for one of them to `HandleAsync`.

```csharp
public sealed class EjectAction(IEventBus bus, IEntityRegistry registry, ILogger<EjectAction> logger)
    : CommandHandler(bus, registry, logger)
{
    public const string EntityId = "eject_disc";

    protected override IReadOnlyList<EntityDescriptor> Entities { get; } =
    [
        new() { Id = EntityId, Name = "Eject disc", Kind = EntityKind.Button, Icon = "mdi:eject" },
    ];

    protected override ValueTask HandleAsync(ActionCommand command, CancellationToken cancellationToken)
    {
        // Do it.
        return ValueTask.CompletedTask;
    }
}
```

- `Kind` is `Button` (pressed, no value), `Switch` (`command.Value` is `on` or `off`) or `Number` (`command.Value` is a number in invariant culture, already checked against `Min` and `Max`). Switches and numbers also report their state, like sensors; override `RunAsync` to publish it. `AudioControl` is the complete example.
- Where it runs follows the same rule as for sensors. Commands for tray entities reach the tray through the pipe by themselves.
- Set `EnabledByDefault = false` for anything that should not work until the user switched it on, as `PowerActions` does. Anyone who can press the button in Home Assistant can trigger the action, so think about what it lets them do to the computer.
- Reserve the ID in `BuiltInIds`, as for a sensor.
