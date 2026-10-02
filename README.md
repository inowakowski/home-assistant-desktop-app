# HADA – Home Assistant Desktop App

HADA runs in the background on Windows. It reports what your PC is doing to [Home Assistant](https://www.home-assistant.io/) and lets Home Assistant control it. A settings window in the tray app shows live status and lets you configure everything.

**What it reports**

| Entity | Type | Provided by | Notes |
|---|---|---|---|
| `cpu_load` | sensor (%) | Service | System-wide CPU load, updated every 10 s |
| `memory_usage` | sensor (%) | Service | Physical memory in use |
| `disk_c_usage`, `disk_d_usage`, … | sensor (%) | Service | One per built-in drive: how full it is. `free_gb` and `total_gb` are attributes |
| `ip_address` | sensor | Service | The IPv4 address of the connection Windows routes through. `interface` and `connection_type` (`ethernet`, `wifi`, `other`) are attributes |
| `wifi_network` | sensor | Service | Only on computers with Wi-Fi. The name of the connected network, `not_connected` otherwise. `signal` (%) is an attribute |
| `display_on` | binary sensor | Service | On while the screen is on. `display_state` (`on`, `dimmed`, `off`) is an attribute. Reported the moment it changes |
| `session_locked` | binary sensor | Service | On while Windows is locked or showing the sign-in screen |
| `active_user` | sensor | Service | The account name of whoever is signed in at the computer's own screen; `none` on the sign-in screen |
| `last_boot` | sensor (timestamp) | Service | When Windows was started |
| `battery_level` | sensor (%) | Service | Only on computers with a battery |
| `battery_charging` | binary sensor | Service | Only on computers with a battery |
| `plugged_in` | binary sensor | Service | Only on computers with a battery |
| `lid_open` | binary sensor | Service | Only on computers with a battery. Reported the moment it changes |
| `update_available` | binary sensor | Service | On when a newer HADA was released; the Overview page then offers to download and install it. `installed_version`, `latest_version` and `release_url` are attributes. See [Updates](#updates) |
| `active_window` | sensor | Tray | Title of the focused window. The process name is an attribute |
| `user_active` | binary sensor | Tray | On when the keyboard or mouse was used in the last 60 seconds |
| `audio_volume` | sensor (%) | Tray | Default playback device volume. `muted` is an attribute |
| `audio_device` | sensor | Tray | Name of the default playback device, e.g. to tell headphones from speakers. The default microphone is the `microphone` attribute |
| `media_playback` | sensor | Tray | `playing`, `paused`, `stopped` or `idle`: what Windows' own media controls show. `title`, `artist`, `album` and `app` are attributes |
| `microphone_in_use` | binary sensor | Tray | On while an app uses the microphone. The apps are listed in the `apps` attribute |
| `microphone_muted` | binary sensor | Tray | On while the default microphone is muted in Windows. `level` (input level, %) is an attribute. Muting only inside a call app is not seen |
| `camera_in_use` | binary sensor | Tray | On while an app uses the camera. The apps are listed in the `apps` attribute |
| `do_not_disturb` | binary sensor | Tray | On while Windows holds notifications back. `mode` (`off`, `priority_only`, `alarms_only`) is an attribute |
| `external_display` | binary sensor | Tray | On while a monitor other than the built-in one is connected. `displays` and `external_displays` (counts) are attributes. A monitor without power is not seen |

**What Home Assistant can do**

| Entity | Type | Provided by | Notes |
|---|---|---|---|
| `lock_screen` | button | Service | Locks the interactive session |
| `sleep`, `hibernate`, `shutdown`, `restart` | button | Service | **Off until you switch them on** on the Entities page. Nothing is forced: apps with unsaved work can still object, as when shutting down from the Start menu |
| `turn_off_display`, `wake_display` | button | Tray | Turns the screen off, or back on as moving the mouse would |
| `media_play_pause`, `media_next`, `media_previous`, `media_stop` | button | Tray | Press the multimedia keys; whichever app handles them reacts |
| `volume_level` | number (0–100 %) | Tray | Sets the volume of the default playback device |
| `audio_mute` | switch | Tray | Mutes the default playback device |
| `microphone_mute` | switch | Tray | Mutes the default microphone in Windows |
| `notification` | notify | Tray | A message sent to it is shown as a Windows notification. See [Notifications](#notifications) |

Every entity can be turned off on the **Entities** page. You can add your own without code as [custom sensors and buttons](#custom-sensors-and-buttons), or [in code](#adding-entities-in-code).

> **Privacy:** `active_window` sends window titles to Home Assistant. Titles can contain document names, e-mail subjects or web page titles. `media_playback` sends what you are listening to or watching, `microphone_in_use` and `camera_in_use` the names of the apps using them, `active_user` your account name, and `wifi_network` the name of your network.

## Custom sensors and buttons

On the **Custom entities** page you define your own sensors:

| Type | Becomes | What you enter |
|---|---|---|
| **Fixed text** | sensor | A value that stays the same until you change it, e.g. the room the computer is in |
| **Program is running** | binary sensor | A process name such as `chrome`. On while at least one such process runs; the count is in the `instances` attribute |
| **PowerShell command** | sensor | A command; whatever it prints becomes the value. Example: `[math]::Round((Get-PSDrive C).Free / 1GB)` with the unit `GB` |
| **Device is connected** | binary sensor | A part of a device's ID, such as `VID_0BDA&PID_8153`, or pick one of the connected USB devices from the list. On while such a device is connected: a USB-C dock, a drive, a headset |

And your own buttons, which Home Assistant can press from a dashboard or an automation:

| Type | What you enter | Where it runs |
|---|---|---|
| **Button: PowerShell command** | A command, e.g. `Start-ScheduledTask -TaskName Backup` | In the service, so as SYSTEM when installed, without a desktop. It may take up to 10 minutes; pressing the button again meanwhile is ignored |
| **Button: start a program** | A program with its arguments, a document, or an address: `"C:\Program Files\App\app.exe" --option`, `notepad`, `https://example.com` | On the desktop of the signed-in user, with that user's rights, so the tray app must be running. Put a path that contains spaces in quotes |

What a button does is fixed in settings. Home Assistant can only press it; it cannot send a command of its own.

- The **ID** is the entity ID. Leave it empty to derive it from the name (`Gra włączona` → `gra_wlaczona`).
- Set a **unit** only for numbers. Home Assistant then treats the sensor as a measurement and draws a graph.
- A program is looked for, and a command is run, every *n* seconds: at least 2, by default 30.
- PowerShell commands are run by the service with Windows PowerShell 5.1, so as SYSTEM when installed. They cannot see your desktop or the files and settings of your user account. A command must finish within 30 seconds; if it fails or prints nothing, the sensor keeps its last value and the reason appears on the **Logs** page.
- A device is matched against the instance IDs of all connected devices, ignoring case, so any part that identifies it works; Device Manager shows the full ID under **Details → Device instance path**. The check is cheap, so an interval of a few seconds is fine.
- Removing a custom sensor or button also removes it from Home Assistant.

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

## Notifications

`notification` shows up in Home Assistant as a notify entity (MQTT engine, Home Assistant 2024.5 or newer). A message sent to it appears as a Windows notification from the HADA tray icon, titled *Home Assistant*:

```yaml
action: notify.send_message
target:
  entity_id: notify.laptop_notification
data:
  message: The washing machine is done.
```

To choose the title, publish JSON to the entity's topic instead:

```yaml
action: mqtt.publish
data:
  topic: hada/laptop/notification/set
  payload: '{"title": "Laundry", "message": "The washing machine is done."}'
```

Windows shows at most 63 characters of a title and 255 of a message; longer ones are cut off. While **Do not disturb** is on, Windows puts the notification in the notification centre without showing it. With the WebSocket engine, see [below](#websocket-engine).

## Several users on one computer

Every signed-in user's tray app connects to the service, and they all offer the same entities. Only one of them is reported at a time: the tray of whoever's desktop is on the computer's own screen. `active_user` says who that is. When another user comes to the front, their values take over at once, and commands such as `volume_level` or `notification` go to them. While the sign-in screen is shown, the session entities are *unavailable*. A user connected over Remote Desktop is reported when nobody uses the computer's own screen.

## Updates

Once a day the service asks GitHub whether a newer HADA was released: one request to `api.github.com`, which carries the installed version and nothing else. If there is one, `update_available` turns on and the **Overview** page shows it, with two buttons:

- **What is new** opens the release page in your browser.
- **Download and install** downloads the installer for this computer (x64 or ARM64) to `%LocalAppData%\HADA\updates`, compares it with the checksum published with the release, and starts it. From there it is the ordinary installer: you click through it, and Windows asks for administrator rights. A download that does not match its checksum is deleted and not started.

Nothing is downloaded or installed unless you press that button. It is offered in the window opened from the tray icon, not in the administrator window opened with **Unlock editing**: an installer started from there would run as administrator throughout, and so would the tray app it starts at the end.

Switching `update_available` off on the **Entities** page also stops the daily request.

The check sees only what GitHub shows without signing in. While the repository is private there is nothing to compare with, and `update_available` stays off.

The checksum protects against a damaged or incomplete download. It is not a signature: it comes from the same place as the installer, so it cannot prove who built it.

## Architecture

```
┌──────────── user session ────────────┐          ┌─────────────── session 0 ───────────────┐
│ HADA.Tray (WPF, notification icon)   │  named   │ HADA.Service (Windows service, SYSTEM)  │
│  • Session sensors (window, audio,   │  pipe    │  • System sensors, custom sensors       │
│    media, activity, mic, camera)     │ ───────► │    and buttons                          │
│  • Session actions (volume, media    │ ◄─────── │  • Lock and power actions               │
│    keys, display, notifications)     │ commands │  • IpcServer (sensors + control API)    │
│  • IpcClient                         │          │  • EngineSupervisor                     │
│  • Settings window                   │ ◄──────► │     • MqttEngine ──────► MQTT broker    │
└──────────────────────────────────────┘          │     • HaWebSocketEngine ► Home Assistant│
                                                  └─────────────────────────────────────────┘
```

- **HADA.Service** runs as a Windows service. It holds the connections to Home Assistant, owns the settings, and runs anything that doesn't need the user's desktop.
- **HADA.Tray** runs in the logged-in user's session. It reads things a service can't see, such as the focused window and the audio device, and streams them to the service over the `HADA.Session` named pipe. Commands from Home Assistant for the tray's entities come back over the same pipe. Its settings window uses the same pipe to read status and logs and to change settings. The window is the same program started as a second process, which exits when the window is closed: a window costs far more memory than the tray icon and the sensors, and this way that memory is only used while the window is open.
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
| `HADA.Platform.Windows` | Win32, Core Audio and WLAN sensors and actions |
| `HADA.Service` | Worker service host, settings storage, engine supervisor, custom sensors and buttons, update check |
| `HADA.Tray` | Tray app host, notifications, the media playback sensor (Windows Runtime) and the settings window (WPF-UI, Polish and English) |
| `HADA.Tests` | xUnit tests |
| `installer` | WiX project that packs the published apps into an MSI. Built by `scripts\Publish-HADA.ps1`, not by the solution |

## Requirements

- Windows 10 version 1809 or newer, or Windows 11; x64 or ARM64.
- [.NET SDK 10](https://dotnet.microsoft.com/download) for building. The projects target .NET 9, and `global.json` pins SDK 10.0.401 or a newer feature band.
- For running from build output: the .NET 9 runtime. Self-contained builds don't need it.
- Home Assistant with one of these:
  - the [MQTT integration](https://www.home-assistant.io/integrations/mqtt/) and a broker, e.g. the Mosquitto add-on. The `notification` entity needs Home Assistant 2024.5 or newer
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
| **Overview** | Whether the service is running, the state of both connections, whether the tray is connected, the **Start with Windows** switch, a notice when a newer version is available, and every entity with its latest value |
| **Connections** | MQTT and Home Assistant settings, each with a **Test connection** button |
| **Entities** | A switch per entity to choose what is shared with Home Assistant and what Home Assistant may do. Disabled entities are removed from Home Assistant. The power buttons start switched off |
| **Custom entities** | Your own sensors (a fixed text, whether a program is running or a device is connected, the output of a PowerShell command) and buttons (run a PowerShell command, start a program) |
| **Logs** | Recent service log entries, filterable by level, with copy to clipboard |

**Changing settings requires administrator rights.** Anyone signed in can see status and logs, but the pages are read-only until you choose **Unlock editing**. That reopens the window as administrator (a UAC prompt). The service checks this itself, so a non-elevated client cannot save settings or run connection tests.

Saved settings take effect immediately: the affected connection restarts. Passwords and tokens are never shown again. Leave the field empty to keep the saved value, or tick **Remove the saved value** to clear it.

## Configuration

Settings saved in the window are stored in `%ProgramData%\HADA\settings.json`:

- The folder is accessible only to SYSTEM, administrators and the account running the service.
- Passwords and tokens in the file are additionally encrypted with Windows DPAPI.
- Each section saved from the window (MQTT, Home Assistant, entities, custom sensors and buttons) replaces the same section of `appsettings.json`.

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
    "Disabled": [ "active_window" ],
    "Enabled": [ "shutdown" ]
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
| `Entities:Enabled` | *(none)* | IDs of the entities that are off unless listed here: `sleep`, `hibernate`, `shutdown`, `restart` |
| `CustomSensors:Items` | *(none)* | [Custom sensors and buttons](#custom-sensors-and-buttons): `Name`, `Type` (`Text`, `ProcessRunning`, `PowerShell`, `DeviceConnected`, `CommandButton` or `LaunchButton`), `Value`, and optionally `Id`, `Unit` and `IntervalSeconds` |

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

On a computer where HADA is also installed, stop the installed service first (`sc.exe stop HADA` in an elevated terminal). A service started from a console refuses to start while another one is running: both would read the same settings and connect to the broker under the same ID, each throwing the other out. Exit the installed tray app as well, from its icon.

Tray command-line options:

| Option | Effect |
|---|---|
| `--background` | Start without opening the window, e.g. at sign-in |
| `--autostart` | Marks a start made by Windows at sign-in. The tray exits again if the user turned **Start with Windows** off |
| `--page overview\|connections\|entities\|custom\|logs` | Open the window on a specific page (`custom` is **Custom entities**) |
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

To update, run a newer installer, or use **Download and install** on the Overview page when it offers a newer version; either replaces the old version and keeps the settings. To remove HADA, use **Settings → Apps → Installed apps**. Settings and logs in `%ProgramData%\HADA` are left in place; delete that folder to forget them.

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
| Tray app | 20 to 25 MB |
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

Entities show up automatically under **Settings → Devices & services → MQTT** as a device named after the PC. Buttons, the volume slider and the mute switches can be used from the dashboard or in automations like any other `button`, `number` or `switch` entity:

```yaml
actions:
  - action: number.set_value
    target:
      entity_id: number.laptop_volume_level
    data:
      value: 30
  - action: button.press
    target:
      entity_id: button.laptop_media_play_pause
```

If the service stops or loses its connection, all of the device's entities turn *unavailable*. The tray app's sensors also turn *unavailable* while the tray app is not running, for example when nobody is signed in, so an automation never acts on a value from an hour ago.

Topics, with `{device}` being `DeviceId`:

| Topic | Content |
|---|---|
| `homeassistant/{sensor\|binary_sensor\|button\|switch\|number\|notify}/{device}/{entity}/config` | Discovery config (retained; emptied when the entity is disabled or removed) |
| `hada/{device}/availability` | `online` / `offline` (retained, last will) |
| `hada/{device}/{entity}/availability` | `online` / `offline` (retained). `offline` while the entity's source is away, e.g. the tray app's sensors after sign-out |
| `hada/{device}/{entity}/state` | State (retained). Binary sensors and switches report `on` / `off` |
| `hada/{device}/{entity}/attributes` | Sensor attributes as JSON (retained) |
| `hada/{device}/{entity}/set` | Command: `PRESS` for a button, `on` / `off` for a switch, the value for a number, the message (or JSON with `title` and `message`) for `notification`. Anything else is ignored, as are retained commands |

### WebSocket engine

Sensors appear as `sensor.{device}_{entity}`, e.g. `sensor.desktop_01_cpu_load`, and binary sensors as `binary_sensor.{device}_{entity}`. Commands are sent by firing the command event, for example from a script. `action` is the entity ID; switches and numbers take a `value`, and `notification` a `message` and an optional `title`:

```yaml
- action: event
  event_type: hada_command
  event_data:
    device_id: desktop_01
    action: lock_screen
- action: event
  event_type: hada_command
  event_data:
    device_id: desktop_01
    action: volume_level
    value: 30
- action: event
  event_type: hada_command
  event_data:
    device_id: desktop_01
    action: notification
    title: Laundry
    message: The washing machine is done.
```

`device_id` is required, so a single event never targets every PC at once.

#### WebSocket engine limitations

- States are written through the REST API. The entities have no unique ID, so they can't be renamed or managed in the UI.
- There are no button, switch, number or notify entities to press in the UI. A switch shows up as a binary sensor and a number as a sensor, and everything is controlled through the command event.
- Home Assistant forgets these states when it restarts. HADA sends them again once it reconnects.
- On a graceful stop, sensors are set to `unavailable`. After a crash or power loss, the last states remain.
- If Home Assistant rejects the token, the engine stops retrying until its settings change or the service restarts, so repeated failed logins don't get the PC's IP banned.

## Adding entities in code

For a value a PowerShell command can print, a [custom sensor](#custom-sensors-and-buttons) is enough and needs no build. Write code when the value needs a Windows API, has to be reported the moment it changes, or should ship with HADA.

### A sensor

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

**6. Try it.** Run `dotnet test`, then start the service and the tray as described under [Running during development](#running-during-development). The entity appears on the **Overview** page with its value, and in Home Assistant under the device.

Existing sensors to copy from: `MemoryUsageSensor` (polling, the shortest), `BatterySensor` (several entities from one reading, registered only when the hardware is there), `PowerStateSensor` (reports changes from a Windows notification instead of polling), `MicrophoneMuteSensor` (a tray sensor with an attribute).

### A button, switch or number

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

## Troubleshooting

Start with the **Logs** page, or `%ProgramData%\HADA\logs\service.log`.

| What you see | Likely cause and what to do |
|---|---|
| The log repeats *Disconnected from MQTT broker* every few seconds, or says the connection *keeps dropping right after it is made* | Two HADA services are connected with the same **Device ID**. A broker allows one connection per ID and drops the other. Give every computer its own Device ID. A second copy of the service on the same computer cannot be the cause from 0.4.0 on: one started from a console refuses to start while another is running |
| The window says the service is not running although it is | Another copy of the service held the `HADA.Session` pipe when this one started. The service takes the pipe over within seconds of the other copy exiting; the log says when |
| The device is in Home Assistant, but entities are missing | Each time it connects, the service logs *Announced N entities to Home Assistant*. If N is what you expect, the reason is on the Home Assistant side: look under **Settings → System → Logs** for `mqtt` entries. If N is too low, the entities are switched off on the **Entities** page or the tray app is not connected |
| Sensors show *unknown* right after they appear | Should not happen from 0.2.0 on, where states are retained. With an older version, wait for the value to change |
| A button does nothing | The **Logs** page says why: a tray action was pressed while the tray app is not running in the session in use, or the action itself failed. The power buttons do not exist in Home Assistant until they are switched on on the **Entities** page |
| A notification does not appear | Windows is holding notifications back (**Do not disturb**, see `do_not_disturb`), or notifications are turned off for HADA under **Settings → System → Notifications**. It is then only in the notification centre |
| `wifi_network` says `not_connected` although Wi-Fi is connected | Windows 11 24H2 and newer hide the network name unless location access is allowed: **Settings → Privacy & security → Location** |
| Every sensor appears twice | Both MQTT and the WebSocket engine are configured. Clear the Home Assistant URL, or the broker host |

## Security notes

- **Pipe access:** `HADA.Session` accepts only local interactive users and denies network access. Only SYSTEM, administrators or the service account can create it. Clients refuse to talk to a pipe with any other owner, and connect at identification level, so the service can check who they are but cannot act as them.
- **What the tray may send:** the tray registers its own entities and reports values for those only. It can't replace entities the service registered, and can't report values for them. A local user who runs their own program against the pipe can therefore add entities of their own to the device, but not touch the service's.
- **Control API:** any local interactive user can read status, non-secret settings and logs. Saving settings and testing connections require an elevated administrator, because a connection test may send a saved password to the address being tested.
- **Commands:** anyone in Home Assistant who can press a button, publish to a command topic or fire the command event can do what the enabled entities allow: lock the PC, change the volume, turn the screen off, show a notification, press a custom button. Shutting down, restarting, sleeping and hibernating are possible only after an administrator switched those buttons on. A command carries nothing but a value the entity accepts: `on` or `off`, a number in range, or a message; never a program or command line.
- **Custom buttons:** what a button runs is fixed in settings, which only an elevated administrator can change. PowerShell buttons run with the service's rights (SYSTEM when installed); program buttons run as the signed-in user. The tray starts programs only when the service asks it to, and talks to the service only through a pipe created by SYSTEM, an administrator or the user themself.
- **Updates:** the daily update check, and a download you start yourself, are the only connections HADA makes to anything other than your broker or Home Assistant. The installer is fetched over HTTPS from this project's releases only, and the file cannot be replaced between the checksum check and the installer starting. See [Updates](#updates).
- **Running the service from a console:** a development copy started by an account other than SYSTEM does not change the permissions of an existing `%ProgramData%\HADA`; only the installed service protects that folder. When it creates the folder, or saves settings, it does add its own account, as the service account needs access.
- **Custom PowerShell sensors and buttons:** their commands run with the service's rights, which is SYSTEM when installed. Only an elevated administrator can define them, in the window or in `appsettings.json`, and an administrator can already run anything as SYSTEM, so this grants nothing new. Still, treat `%ProgramData%\HADA\settings.json` and `appsettings.json` as files that can run code.

## Known limitations

- **Lock screen from the service:** as a service in session 0, the lock action disconnects the active console session, which returns Windows to the lock screen. This hasn't been verified on every Windows edition. Run from a user session, it uses `LockWorkStation`.
- **Sleep, hibernate, shut down, restart, turn off display, media keys:** these go through the same Windows calls as the Start menu and a multimedia keyboard, but have not been tried on every kind of computer. On Modern Standby devices *Sleep* and *Turn off display* come to much the same thing. `wake_display` cannot wake a computer that is asleep, because a sleeping computer is not connected.
- **Notifications** are the simple kind: a title and a text, without buttons or images.
- **Do not disturb** is read from a Windows state that is not documented. If a Windows version stops providing it, the entity is not registered.
- **Tray sensors after a crash of the service:** when the tray exits, its sensors turn unavailable. If the service itself is killed while the tray is connected, e.g. by a power cut, and Windows then starts without anyone signing in, they show their last value until the tray connects again.
- **Screen off on Modern Standby devices:** many laptops, most ARM64 ones included, go to sleep within seconds of the screen turning off and drop the network. `display_on` is sent as `off` first, but if the connection is already gone, Home Assistant shows the device as unavailable instead.
- **Microphone and camera:** detection reads the usage history Windows keeps for its privacy settings. Apps that bypass it are not seen, and an app that crashed while using the device may be reported as still using it until Windows restarts.
- **Lists in `appsettings.json`:** a list saved from the window (disabled and enabled entities, custom sensors and buttons) overrides the file's list entry by entry, so entries beyond the saved list's length still apply. Keep such lists in one place.

## License

[MIT](LICENSE). HADA uses [MQTTnet](https://github.com/dotnet/MQTTnet) and [WPF UI](https://github.com/lepoco/wpfui), both under the MIT license, and the .NET runtime it ships with is MIT-licensed too.
