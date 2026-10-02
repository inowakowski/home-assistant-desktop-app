# HADA – Home Assistant Desktop App

HADA runs in the background on Windows. It reports what your PC is doing to [Home Assistant](https://www.home-assistant.io/) and lets Home Assistant control it. A settings window in the tray app shows live status and lets you configure everything.

| Entity | Type | Provided by | Notes |
|---|---|---|---|
| `cpu_load` | sensor (%) | Service | System-wide CPU load, updated every 10 s |
| `memory_usage` | sensor (%) | Service | Physical memory in use |
| `display_on` | binary sensor | Service | On while the screen is on. `display_state` (`on`, `dimmed`, `off`) is an attribute. Reported the moment it changes |
| `session_locked` | binary sensor | Service | On while Windows is locked or showing the sign-in screen |
| `last_boot` | sensor (timestamp) | Service | When Windows was started |
| `battery_level` | sensor (%) | Service | Only on computers with a battery |
| `battery_charging` | binary sensor | Service | Only on computers with a battery |
| `plugged_in` | binary sensor | Service | Only on computers with a battery |
| `lid_open` | binary sensor | Service | Only on computers with a battery. Reported the moment it changes |
| `lock_screen` | button | Service | Locks the interactive session |
| `active_window` | sensor | Tray | Title of the focused window. The process name is an attribute |
| `audio_volume` | sensor (%) | Tray | Default playback device volume. `muted` is an attribute |
| `user_active` | binary sensor | Tray | On when the keyboard or mouse was used in the last 60 seconds |
| `microphone_in_use` | binary sensor | Tray | On while an app uses the microphone. The apps are listed in the `apps` attribute |
| `microphone_muted` | binary sensor | Tray | On while the default microphone is muted in Windows. `level` (input level, %) is an attribute. Muting only inside a call app is not seen |
| `camera_in_use` | binary sensor | Tray | On while an app uses the camera. The apps are listed in the `apps` attribute |
| `external_display` | binary sensor | Tray | On while a monitor other than the built-in one is connected. `displays` and `external_displays` (counts) are attributes. A monitor without power is not seen |

Every entity can be turned off on the **Entities** page. You can add your own without code as [custom sensors](#custom-sensors), or [in code](#adding-entities-in-code).

> **Privacy:** `active_window` sends window titles to Home Assistant. Titles can contain document names, e-mail subjects or web page titles. `microphone_in_use` and `camera_in_use` send the names of the apps using them.

## Custom sensors

On the **Custom sensors** page you define your own sensors:

| Type | Becomes | What you enter |
|---|---|---|
| **Fixed text** | sensor | A value that stays the same until you change it, e.g. the room the computer is in |
| **Program is running** | binary sensor | A process name such as `chrome`. On while at least one such process runs; the count is in the `instances` attribute |
| **PowerShell command** | sensor | A command; whatever it prints becomes the value. Example: `[math]::Round((Get-PSDrive C).Free / 1GB)` with the unit `GB` |
| **Device is connected** | binary sensor | A part of a device's ID, such as `VID_0BDA&PID_8153`, or pick one of the connected USB devices from the list. On while such a device is connected: a USB-C dock, a drive, a headset |

- The **ID** is the entity ID. Leave it empty to derive it from the name (`Gra włączona` → `gra_wlaczona`).
- Set a **unit** only for numbers. Home Assistant then treats the sensor as a measurement and draws a graph.
- A program is looked for, and a command is run, every *n* seconds: at least 2, by default 30.
- PowerShell commands are run by the service with Windows PowerShell 5.1, so as SYSTEM when installed. They cannot see your desktop or the files and settings of your user account. A command must finish within 30 seconds; if it fails or prints nothing, the sensor keeps its last value and the reason appears on the **Logs** page.
- A device is matched against the instance IDs of all connected devices, ignoring case, so any part that identifies it works; Device Manager shows the full ID under **Details → Device instance path**. The check is cheap, so an interval of a few seconds is fine.
- Removing a custom sensor also removes it from Home Assistant.

### Example: is the laptop docked?

A monitor on a smart plug should be powered only while the laptop sits in its USB-C dock with the screen on. Whether the laptop is charging does not say that, since a plain charger charges it too. Neither does `external_display`: once the plug is off, the monitor has no power and Windows no longer sees it. The dock itself does: its own devices, such as its network adapter, are there whenever the cable is plugged in.

1. With the laptop docked, add a custom sensor of the type **Device is connected**, pick a device that belongs to the dock from the list, name it *Docked*, and set it to check every 3 seconds.
2. In Home Assistant, let the plug follow both sensors:

```yaml
automation:
  - alias: Monitor power follows the docked laptop
    triggers:
      - trigger: state
        entity_id:
          - binary_sensor.laptop_docked
          - binary_sensor.laptop_display
    actions:
      - action: >-
          switch.turn_{{ 'on' if is_state('binary_sensor.laptop_docked', 'on')
          and is_state('binary_sensor.laptop_display', 'on') else 'off' }}
        target:
          entity_id: switch.monitor_plug
```

While the laptop is asleep or off, both sensors are *unavailable*, which also turns the plug off.

## Architecture

```
┌──────────── user session ────────────┐          ┌─────────────── session 0 ───────────────┐
│ HADA.Tray (WPF, notification icon)   │  named   │ HADA.Service (Windows service, SYSTEM)  │
│  • Session sensors (window, volume,  │  pipe    │  • System sensors, custom sensors       │
│    activity, microphone, camera)     │ ───────► │  • LockScreenAction                     │
│  • IpcClient                         │          │  • IpcServer (sensors + control API)    │
│                                      │          │  • EngineSupervisor                     │
│  • Settings window                   │ ◄──────► │     • MqttEngine ──────► MQTT broker    │
└──────────────────────────────────────┘          │     • HaWebSocketEngine ► Home Assistant│
                                                  └─────────────────────────────────────────┘
```

- **HADA.Service** runs as a Windows service. It holds the connections to Home Assistant, owns the settings, and runs anything that doesn't need the user's desktop.
- **HADA.Tray** runs in the logged-in user's session. It reads things a service can't see, such as the focused window and the audio device, and streams them to the service over the `HADA.Session` named pipe. Its settings window uses the same pipe to read status and logs and to change settings. The window is the same program started as a second process, which exits when the window is closed: a window costs far more memory than the tray icon and the sensors, and this way that memory is only used while the window is open.
- Sensors, actions and engines talk only through an in-process event bus (`HADA.Core`). Sensors never reference MQTT or WebSocket code.
- There are two **communication engines**. Each stays idle until it is configured, and restarts by itself when its settings change:
  - **MQTT** (recommended). Uses MQTT discovery, so entities appear automatically with unique IDs, a device, and availability tracking through a last will.
  - **WebSocket/REST**. Needs no broker, but has the limitations listed [below](#websocket-engine-limitations).

| Project | Purpose |
|---|---|
| `HADA.Core` | Models, event bus, entity registry and filter, engine abstraction, file logging |
| `HADA.Engine.Mqtt` | MQTT engine (MQTTnet) |
| `HADA.Engine.WebSocket` | Home Assistant WebSocket + REST engine |
| `HADA.Ipc` | Named pipe protocol: sensor stream and control API, server and clients |
| `HADA.Platform.Windows` | Win32 and Core Audio sensors and actions |
| `HADA.Service` | Worker service host, settings storage, engine supervisor |
| `HADA.Tray` | Tray app host and settings window (WPF-UI, Polish and English) |
| `HADA.Tests` | xUnit tests |
| `installer` | WiX project that packs the published apps into an MSI. Built by `scripts\Publish-HADA.ps1`, not by the solution |

## Requirements

- Windows 10 or 11, x64 or ARM64.
- [.NET SDK 10](https://dotnet.microsoft.com/download) for building. The projects target .NET 9, and `global.json` pins SDK 10.0.401 or a newer feature band.
- For running from build output: the .NET 9 runtime. Self-contained builds don't need it.
- Home Assistant with one of these:
  - the [MQTT integration](https://www.home-assistant.io/integrations/mqtt/) and a broker, e.g. the Mosquitto add-on
  - a long-lived access token for an **administrator** account, for the WebSocket engine

## Build and test

```powershell
dotnet build
dotnet test
```

## The settings window

Open it by double-clicking the HADA tray icon, or choose **Open HADA** from its menu. Starting `HADA.Tray.exe` again while it is already running also brings the window up. The window follows the Windows light or dark theme, and shows Polish text when Windows' display language is Polish, English otherwise.

| Page | What it shows |
|---|---|
| **Overview** | Whether the service is running, the state of both connections, whether the tray is connected, the **Start with Windows** switch, and every entity with its latest value |
| **Connections** | MQTT and Home Assistant settings, each with a **Test connection** button |
| **Entities** | A switch per entity to choose what is shared with Home Assistant. Disabled entities are removed from Home Assistant |
| **Custom sensors** | Your own sensors: a fixed text, whether a program is running, or the output of a PowerShell command |
| **Logs** | Recent service log entries, filterable by level, with copy to clipboard |

**Changing settings requires administrator rights.** Anyone signed in can see status and logs, but the pages are read-only until you choose **Unlock editing**. That reopens the window as administrator (a UAC prompt). The service checks this itself, so a non-elevated client cannot save settings or run connection tests.

Saved settings take effect immediately: the affected connection restarts. Passwords and tokens are never shown again. Leave the field empty to keep the saved value, or tick **Remove the saved value** to clear it.

## Configuration

Settings saved in the window are stored in `%ProgramData%\HADA\settings.json`:

- The folder is accessible only to SYSTEM, administrators and the account running the service.
- Passwords and tokens in the file are additionally encrypted with Windows DPAPI.
- Each section saved from the window (MQTT, Home Assistant, entities, custom sensors) replaces the same section of `appsettings.json`.

You can also configure the service without the window, through `appsettings.json` next to `HADA.Service.exe`:

```json
{
  "Mqtt": {
    "Host": "homeassistant.local",
    "Port": 1883,
    "UseTls": false,
    "Username": "hada",
    "DeviceId": "",
    "DeviceName": "",
    "DiscoveryPrefix": "homeassistant",
    "BaseTopic": "hada"
  },
  "HomeAssistant": {
    "BaseUrl": "http://homeassistant.local:8123",
    "DeviceId": "",
    "DeviceName": "",
    "CommandEventType": "hada_command"
  },
  "Entities": {
    "Disabled": [ "active_window" ]
  },
  "CustomSensors": {
    "Items": [
      { "Name": "Game running", "Type": "ProcessRunning", "Value": "game", "IntervalSeconds": 5 }
    ]
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `Mqtt:Host` | *(empty = MQTT engine off)* | Broker host name or IP. The window also accepts pasted addresses such as `mqtt://broker:1883` and splits them into host, port and TLS |
| `Mqtt:Port` | `1883` | Usually `8883` with TLS |
| `Mqtt:UseTls` | `false` | Use TLS for the broker connection |
| `Mqtt:Username` / `Mqtt:Password` | – | Broker credentials. See [Secrets](#secrets) |
| `Mqtt:DiscoveryPrefix` | `homeassistant` | Must match the MQTT integration's discovery prefix |
| `Mqtt:BaseTopic` | `hada` | Root of HADA's state, attributes, command and availability topics |
| `HomeAssistant:BaseUrl` | *(empty = WebSocket engine off)* | Home Assistant URL, `http` or `https` |
| `HomeAssistant:AccessToken` | – | Long-lived token of an administrator. See [Secrets](#secrets) |
| `HomeAssistant:CommandEventType` | `hada_command` | Event type the WebSocket engine listens to for commands |
| `*:DeviceId` | machine name | Used in topics and entity IDs. Lowercased, and anything other than letters and digits becomes `_` (`DESKTOP-01` → `desktop_01`) |
| `*:DeviceName` | machine name | Device and friendly-name prefix shown in Home Assistant |
| `Entities:Disabled` | *(none)* | Entity IDs not shared with Home Assistant |
| `CustomSensors:Items` | *(none)* | [Custom sensors](#custom-sensors): `Name`, `Type` (`Text`, `ProcessRunning` or `PowerShell`), `Value`, and optionally `Id`, `Unit` and `IntervalSeconds` |

> Configure **one** engine. With both configured, every sensor appears in Home Assistant twice.

### Secrets

The settings window is the simplest way to set `Mqtt:Password` and `HomeAssistant:AccessToken`; they are then stored encrypted. When configuring through files, keep them out of `appsettings.json`.

**Development** (`dotnet run` uses the `Development` environment, which loads user secrets):

```powershell
dotnet user-secrets set "Mqtt:Password" "<password>" --project src/HADA.Service
dotnet user-secrets set "HomeAssistant:AccessToken" "<token>" --project src/HADA.Service
```

**Installed service without the window:** put the secrets in `appsettings.Production.json` next to `HADA.Service.exe`, then restrict that file to SYSTEM and Administrators:

```json
{
  "Mqtt": { "Password": "<password>" },
  "HomeAssistant": { "AccessToken": "<token>" }
}
```

```powershell
icacls "C:\Program Files\HADA\service\appsettings.Production.json" /inheritance:r /grant:r "*S-1-5-18:R" "*S-1-5-32-544:F"
```

Avoid service-level environment variables in the registry, because local users can read them.

## Running during development

Start the service and the tray in two terminals:

```powershell
dotnet run --project src/HADA.Service
```

```powershell
dotnet run --project src/HADA.Tray
```

The tray opens its window straight away. The Overview page should show the service as running and the tray as connected.

Tray command-line options:

| Option | Effect |
|---|---|
| `--background` | Start without opening the window, e.g. at sign-in |
| `--autostart` | Marks a start made by Windows at sign-in. The tray exits again if the user turned **Start with Windows** off |
| `--page overview\|connections\|entities\|custom\|logs` | Open the window on a specific page |
| `--settings` | Run as the window, without tray icon or sensors. This is how the tray opens the window, and how **Unlock editing** reopens it as administrator |

## Installing

### With the installer

Build the installers:

```powershell
.\scripts\Publish-HADA.ps1
```

This creates `artifacts\installer\HADA-<version>-x64.msi` and `HADA-<version>-arm64.msi`; either computer can build both. They are self-contained, so the target computer needs no .NET. The GitHub workflow in `.github/workflows/build.yml` builds the same two files and attaches them to each run.

Double-click the installer that matches the computer and follow the three pages. It:

- copies HADA to `C:\Program Files\HADA`
- registers the `HADA` service (LocalSystem, starts with Windows, restarts a minute after a crash) and starts it
- starts the tray app at sign-in for every user, and adds **HADA** to the Start menu. Each user can turn this off with **Start with Windows** on the Overview page; the service itself always starts with Windows
- offers to open the HADA window on the last page, where you choose **Unlock editing** and set up a connection

To update, run a newer installer; it replaces the old version and keeps the settings. To remove HADA, use **Settings → Apps → Installed apps**. Settings and logs in `%ProgramData%\HADA` are left in place; delete that folder to forget them.

For unattended installation: `msiexec /i HADA-<version>-x64.msi /qn`. The tray app then starts at the next sign-in.

> The installers are not code-signed, so Windows SmartScreen may warn before running them. Choose **More info → Run anyway**, or sign them with your own certificate.

### By hand

Steps 1 and 2 write to `C:\Program Files` and register a service, so run them in an **elevated** PowerShell.

#### 1. Publish

Use `win-x64` or `win-arm64` to match the machine:

```powershell
dotnet publish src/HADA.Service -c Release -r win-x64 --self-contained -o "C:\Program Files\HADA\service"
dotnet publish src/HADA.Tray -c Release -r win-x64 --self-contained -o "C:\Program Files\HADA\tray"
```

#### 2. Register the service (elevated PowerShell)

```powershell
sc.exe create HADA binPath= "C:\Program Files\HADA\service\HADA.Service.exe" start= delayed-auto DisplayName= "Home Assistant Desktop App"
sc.exe failure HADA reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start HADA
```

The service runs as LocalSystem. See [Logs](#logs) for where it reports problems.

To remove it:

```powershell
sc.exe stop HADA
sc.exe delete HADA
```

#### 3. Start the tray at sign-in

```powershell
New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "HADA.Tray" -Value '"C:\Program Files\HADA\tray\HADA.Tray.exe" --background --autostart' -PropertyType String -Force
```

Only one tray instance runs per user session. Then open the window from the tray icon and configure a connection.

## Memory use

Measured on Windows 11 ARM64 (private memory):

| Process | Memory |
|---|---|
| Service | about 15 MB |
| Tray app | about 20 MB |
| Settings window, while it is open | about 125 MB |

The window draws with the CPU instead of the graphics card. On some graphics drivers, setting up hardware rendering alone takes more than 200 MB, and these pages have nothing that needs it.

## Logs

| Where | What |
|---|---|
| **Logs** page of the window | Recent service entries of every level, since the service started |
| `%ProgramData%\HADA\logs\service.log` | The service's entries from Information up. Readable by administrators |
| `%LocalAppData%\HADA\logs\tray.log` | The tray app's entries, including errors it otherwise only shows in a message box |
| `%LocalAppData%\HADA\logs\settings-window.log` | The same for the settings window |
| Windows Event Log, Application, source `HADA.Service` | The service's warnings and errors |

Each file is limited to 2 MB; the three previous files are kept as `service.1.log` and so on.

A sensor that fails is logged and stops, without taking the service or the other sensors with it.

## Using it in Home Assistant

### MQTT engine

Entities show up automatically under **Settings → Devices & services → MQTT** as a device named after the PC. The lock screen button can be pressed from the dashboard or used in automations like any other `button` entity.

If the service stops or loses its connection, all of the device's entities turn *unavailable*. The tray app's sensors also turn *unavailable* while the tray app is not running, for example when nobody is signed in, so an automation never acts on a value from an hour ago.

Topics, with `{device}` being `DeviceId`:

| Topic | Content |
|---|---|
| `homeassistant/{sensor\|binary_sensor\|button}/{device}/{entity}/config` | Discovery config (retained; emptied when the entity is disabled or removed) |
| `hada/{device}/availability` | `online` / `offline` (retained, last will) |
| `hada/{device}/{entity}/availability` | `online` / `offline` (retained). `offline` while the entity's source is away, e.g. the tray app's sensors after sign-out |
| `hada/{device}/{entity}/state` | Sensor state (retained). Binary sensors report `on` / `off` |
| `hada/{device}/{entity}/attributes` | Sensor attributes as JSON (retained) |
| `hada/{device}/{entity}/set` | Button command, payload `PRESS` |

### WebSocket engine

Sensors appear as `sensor.{device}_{entity}`, e.g. `sensor.desktop_01_cpu_load`, and binary sensors as `binary_sensor.{device}_{entity}`. Commands are sent by firing the command event, for example from a script:

```yaml
action: event
event_type: hada_command
event_data:
  device_id: desktop_01
  action: lock_screen
```

`device_id` is required, so a single event never targets every PC at once.

#### WebSocket engine limitations

- States are written through the REST API. The entities have no unique ID, so they can't be renamed or managed in the UI.
- Home Assistant forgets these states when it restarts. HADA sends them again once it reconnects.
- On a graceful stop, sensors are set to `unavailable`. After a crash or power loss, the last states remain.
- If Home Assistant rejects the token, the engine stops retrying until its settings change or the service restarts, so repeated failed logins don't get the PC's IP banned.

## Adding entities in code

For a value a PowerShell command can print, a [custom sensor](#custom-sensors) is enough and needs no build. Write code when the value needs a Windows API, has to be reported the moment it changes, or should ship with HADA.

### A sensor

**1. Decide where it runs.**

| Runs in | Choose it when | Registered in |
|---|---|---|
| Service | The value is the same for the whole computer and should be reported with nobody signed in: hardware, power, disks, network | `src/HADA.Service/Program.cs` |
| Tray | The value belongs to the signed-in user's desktop: windows, audio devices, keyboard and mouse, per-user registry | `src/HADA.Tray/App.xaml.cs` |

A tray sensor turns *unavailable* in Home Assistant while the tray app is not running. The tray may register sensors and binary sensors only.

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

**3. Register it** next to the other sensors, in the file from step 1:

```csharp
builder.Services.AddHostedService<DiskFreeSensor>();
```

In the service, keep it above `CustomSensorHost`.

**4. Reserve the ID.** Add `DiskFreeSensor.EntityId` to `BuiltInIds` in `src/HADA.Service/CustomSensors/CustomSensorRules.cs`, so that a custom sensor cannot take the same ID.

**5. Optional polish** for the settings window, in `src/HADA.Tray`:

- an icon: a line in `EntityVisuals.SymbolFor` in `ViewModels/Support.cs`
- a hint under the entity's name: `EntityHint_disk_free` in both `Localization/Strings.resx` and `Localization/Strings.pl.resx`

**6. Try it.** Run `dotnet test`, then start the service and the tray as described under [Running during development](#running-during-development). The entity appears on the **Overview** page with its value, and in Home Assistant under the device.

Existing sensors to copy from: `MemoryUsageSensor` (polling, the shortest), `BatterySensor` (several entities from one reading, registered only when the hardware is there), `PowerStateSensor` (reports changes from a Windows notification instead of polling), `MicrophoneMuteSensor` (a tray sensor with an attribute).

### A button

A button is something Home Assistant can press, such as `lock_screen`. Buttons run in the service only. Register an entity with `Kind = EntityKind.Button`, and react to the commands the engines publish:

```csharp
// Subscribe before registering, so a press arriving right after discovery is not missed.
await using var commands = bus.Subscribe<ActionCommand>();
await registry.RegisterAsync(new EntityDescriptor { Id = EntityId, Name = "Sleep", Kind = EntityKind.Button }, stoppingToken);

await foreach (var command in commands.ReadAllAsync(stoppingToken))
{
    if (command.ActionId == EntityId)
    {
        // Do it.
    }
}
```

`src/HADA.Platform.Windows/Actions/LockScreenAction.cs` is the complete example. Anyone who can press the button in Home Assistant can trigger the action, so think about what it lets them do to the computer.

## Troubleshooting

Start with the **Logs** page, or `%ProgramData%\HADA\logs\service.log`.

| What you see | Likely cause and what to do |
|---|---|
| The log repeats *Disconnected from MQTT broker* every few seconds, or says the connection *keeps dropping right after it is made* | Two HADA services are connected with the same **Device ID**. A broker allows one connection per ID and drops the other. Give every computer its own Device ID, and make sure only one copy of the service runs on each: a development build started with `dotnet run` counts as one |
| The window says the service is not running although it is | Another copy of the service held the `HADA.Session` pipe when this one started. The service takes the pipe over within seconds of the other copy exiting; the log says when |
| The device is in Home Assistant, but entities are missing | Each time it connects, the service logs *Announced N entities to Home Assistant*. If N is what you expect, the reason is on the Home Assistant side: look under **Settings → System → Logs** for `mqtt` entries. If N is too low, the entities are switched off on the **Entities** page or the tray app is not connected |
| Sensors show *unknown* right after they appear | Should not happen from 0.2.0 on, where states are retained. With an older version, wait for the value to change |
| Every sensor appears twice | Both MQTT and the WebSocket engine are configured. Clear the Home Assistant URL, or the broker host |

## Security notes

- **Pipe access:** `HADA.Session` accepts only local interactive users and denies network access. Only SYSTEM, administrators or the service account can create it. Clients refuse to talk to a pipe with any other owner, and connect at identification level, so the service can check who they are but cannot act as them.
- **What the tray may send:** the service accepts only sensors from the tray. The tray can't replace entities the service registered, and can't report values for them.
- **Control API:** any local interactive user can read status, non-secret settings and logs. Saving settings and testing connections require an elevated administrator, because a connection test may send a saved password to the address being tested.
- **Commands:** anyone in Home Assistant who can press the button, publish to the command topic or fire the command event can lock the PC.
- **Custom PowerShell sensors:** their commands run with the service's rights, which is SYSTEM when installed. Only an elevated administrator can define them, in the window or in `appsettings.json`, and an administrator can already run anything as SYSTEM, so this grants nothing new. Still, treat `%ProgramData%\HADA\settings.json` and `appsettings.json` as files that can run code.

## Known limitations

- **Lock screen from the service:** as a service in session 0, the lock action disconnects the active console session, which returns Windows to the lock screen. This hasn't been verified on every Windows edition. Run from a user session, it uses `LockWorkStation`.
- **Several users signed in:** with fast user switching, every signed-in user's tray reports under the same entity IDs.
- **Tray sensors after a crash of the service:** when the tray exits, its sensors turn unavailable. If the service itself is killed while the tray is connected, e.g. by a power cut, and Windows then starts without anyone signing in, they show their last value until the tray connects again.
- **Screen off on Modern Standby devices:** many laptops, most ARM64 ones included, go to sleep within seconds of the screen turning off and drop the network. `display_on` is sent as `off` first, but if the connection is already gone, Home Assistant shows the device as unavailable instead.
- **Microphone and camera:** detection reads the usage history Windows keeps for its privacy settings. Apps that bypass it are not seen, and an app that crashed while using the device may be reported as still using it until Windows restarts.
- **Lists in `appsettings.json`:** a list saved from the window (disabled entities, custom sensors) overrides the file's list entry by entry, so entries beyond the saved list's length still apply. Keep such lists in one place.
