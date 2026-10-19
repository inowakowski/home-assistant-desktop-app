# Adding entities in code

For a value a PowerShell command can print, a [custom sensor](../features/custom-entities.md) is enough and needs no build. Write code when the value needs a Windows API, has to be reported the moment it changes, or should ship with HADA.

## A sensor

**1. Decide where it is read, and where it runs.**

| It is | Put the class in | Registered in |
|---|---|---|
| The same on every system, and read from something each system can tell (CPU, memory, battery, disks) | `src/HADA.Core/Sensors`, reading through an interface such as `ISystemReadings`; each system's project implements the interface | `SystemEntities.AddSystemEntities` in `src/HADA.Core/Sensors/SystemEntities.cs` |
| Only on Windows, or needing a Windows API | `src/HADA.Platform.Windows/Sensors` | `WindowsPlatform.AddWindowsServiceEntities` in `src/HADA.Platform.Windows/WindowsPlatform.cs` |
| Only on Linux or on macOS | `src/HADA.Platform.Linux` or `src/HADA.Platform.MacOS` | `AddLinuxServiceEntities` in `LinuxSystem.cs`, `AddMacServiceEntities` in `MacSystem.cs` |

That is for a sensor of the **service**: the value is the same for the whole computer and is reported with nobody signed in. A value that belongs to the signed-in user's desktop (windows, audio devices, keyboard and mouse) is a sensor of the **tray app** on Windows, registered in `src/HADA.Tray/Session/SessionServices.cs`, and of the user's session on Linux and macOS, registered in `AddLinuxSessionEntities` (`LinuxSession.cs`) or `AddMacSessionEntities` (`MacSession.cs`). A session entity turns *unavailable* in Home Assistant while the tray app (on Linux and macOS, the app) is not running, or while another user is at the computer.

Windows API calls stay in `HADA.Platform.Windows` (or in members marked `[SupportedOSPlatform("windows")]`) and are reached through an interface; every other project builds for plain `net10.0`, and the build fails (CA1416) on a Windows call anywhere else. See [Architecture](architecture.md).

**2. Add a class.** A sensor derives from `EagerBackgroundService`, not `BackgroundService`: it registers its entity once and then publishes readings. This one reports the free space of the drive HADA runs from, which every system can tell:

```csharp
using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;

namespace HADA.Core.Sensors;

/// <summary>Publishes the free space on the drive HADA runs from.</summary>
public sealed class DiskFreeSensor(IEventBus bus, IEntityRegistry registry) : EagerBackgroundService
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

        var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory)!);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var freeGigabytes = drive.AvailableFreeSpace / 1_000_000_000;
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

**3. Register it** where the table in step 1 says, next to the sensors already there:

```csharp
services.AddHostedService<DiskFreeSensor>();
```

If you register it in `src/HADA.Service/ServiceHost.cs` itself, keep it above `CustomSensorHost`.

**4. Reserve the ID.** Add the ID to `BuiltInEntityIds` in `src/HADA.Core/Entities/BuiltInEntityIds.cs`, as a constant and in `All`, so that a custom sensor cannot take it. The ID is what Home Assistant knows the entity by, so it is the same on every system, whichever of them registers it.

**5. Optional polish** for the window, in `src/HADA.Presentation`:

- an icon: a line in `EntityVisuals.SymbolFor` in `ViewModels/Support.cs`
- a hint under the entity's name: `EntityHint_disk_free` in both `Localization/Strings.resx` and `Localization/Strings.pl.resx`. Where Linux or macOS differ, add `EntityHint_disk_free_Unix` or `EntityHint_disk_free_MacOS`; they are used there instead of the plain one.

**6. Try it.** Run `dotnet test`, then start the service and the tray as described under [Running during development](building.md#running-during-development). The entity appears on the **Overview** page with its value, and in Home Assistant under the device.

Existing sensors to copy from, all in `src/HADA.Core/Sensors` unless said otherwise: `MemoryUsageSensor` (polling through `ISystemReadings`, the shortest), `BatterySensor` (several entities from one reading, registered only when the hardware is there), `MicrophoneMuteSensor` (a session sensor with an attribute), and, in `src/HADA.Platform.Windows/Sensors`, `PowerStateSensor` (reports changes from a Windows notification instead of polling).

## A button, switch or number

These are entities Home Assistant sends commands to. Derive from `CommandHandler` in `src/HADA.Core/Actions`: it registers the entities and hands every command for one of them to `HandleAsync`.

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
- Reserve the ID in `BuiltInEntityIds`, as for a sensor.

## Something that happens

A state that changes is a sensor. Something that happens and is gone, such as a quick action being chosen, is a `DeviceEvent`: publish one to the event bus, and the engines pass it to Home Assistant as described under [MQTT engine](../home-assistant/mqtt.md#topics) and [WebSocket engine](../home-assistant/websocket.md#events-from-the-computer). Its `Name` must be one the engines know (`DeviceEvent.QuickAction`, `DeviceEvent.NotificationAction`); add a constant there for a new kind.
