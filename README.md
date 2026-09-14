# HADA – Home Assistant Desktop App

HADA runs in the background on Windows. It reports what your PC is doing to [Home Assistant](https://www.home-assistant.io/) and lets Home Assistant control it.

| Entity | Type | Provided by | Notes |
|---|---|---|---|
| `cpu_load` | sensor (%) | Service | System-wide CPU load, updated every 10 s |
| `lock_screen` | button | Service | Locks the interactive session |
| `active_window` | sensor | Tray | Title of the focused window. The process name is an attribute |
| `audio_volume` | sensor (%) | Tray | Default playback device volume. `muted` is an attribute |

> **Privacy:** `active_window` sends window titles to Home Assistant. Titles can contain document names, e-mail subjects or web page titles.

## Architecture

```
┌──────────── user session ────────────┐          ┌─────────────── session 0 ───────────────┐
│ HADA.Tray (WPF, notification icon)   │  named   │ HADA.Service (Windows service, SYSTEM)  │
│  • ActiveWindowSensor                │  pipe    │  • CpuLoadSensor, LockScreenAction      │
│  • AudioVolumeSensor                 │ ───────► │  • IpcServer                            │
│  • IpcClient                         │          │  • MqttEngine ─────────► MQTT broker    │
└──────────────────────────────────────┘          │  • HaWebSocketEngine ──► Home Assistant │
                                                  └─────────────────────────────────────────┘
```

- **HADA.Service** runs as a Windows service. It holds the connections to Home Assistant and runs anything that doesn't need the user's desktop.
- **HADA.Tray** runs in the logged-in user's session. It reads things a service can't see, such as the focused window and the audio device, and streams them to the service over the `HADA.Session` named pipe.
- Sensors, actions and engines talk only through an in-process event bus (`HADA.Core`). Sensors never reference MQTT or WebSocket code.
- There are two **communication engines**. Each stays idle until its configuration section is filled in:
  - **MQTT** (recommended). Uses MQTT discovery, so entities appear automatically with unique IDs, a device, and availability tracking through a last will.
  - **WebSocket/REST**. Needs no broker, but has the limitations listed [below](#websocket-engine-limitations).

| Project | Purpose |
|---|---|
| `HADA.Core` | Models, event bus, entity registry, engine abstraction |
| `HADA.Engine.Mqtt` | MQTT engine (MQTTnet) |
| `HADA.Engine.WebSocket` | Home Assistant WebSocket + REST engine |
| `HADA.Ipc` | Named pipe protocol, server (service) and client (tray) |
| `HADA.Platform.Windows` | Win32 and Core Audio sensors and actions |
| `HADA.Service` | Worker service host |
| `HADA.Tray` | Tray app host |
| `HADA.Tests` | xUnit tests |

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

## Configuration

The service reads `appsettings.json` from the folder it runs in. Configure at least one engine.

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
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `Mqtt:Host` | *(empty = MQTT engine off)* | Broker host name or IP |
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

> Configure **one** engine. With both configured, every sensor appears in Home Assistant twice.

### Secrets

Keep `Mqtt:Password` and `HomeAssistant:AccessToken` out of `appsettings.json`.

**Development** (`dotnet run` uses the `Development` environment, which loads user secrets):

```powershell
dotnet user-secrets set "Mqtt:Password" "<password>" --project src/HADA.Service
dotnet user-secrets set "HomeAssistant:AccessToken" "<token>" --project src/HADA.Service
```

**Installed service:** put the secrets in `appsettings.Production.json` next to `HADA.Service.exe`, then restrict that file to SYSTEM and Administrators:

```json
{
  "Mqtt": { "Password": "<password>" },
  "HomeAssistant": { "AccessToken": "<token>" }
}
```

```powershell
icacls "C:\Program Files\HADA\service\appsettings.Production.json" /inheritance:r /grant:r "*S-1-5-18:R" "*S-1-5-32-544:F"
```

You can also use environment variables such as `Mqtt__Password`. Avoid service-level environment variables in the registry, because local users can read them.

## Running during development

Start the service and the tray in two terminals:

```powershell
dotnet run --project src/HADA.Service
```

```powershell
dotnet run --project src/HADA.Tray
```

- **Service log:** shows which engines connected and `IPC client 'tray:<user>' connected`.
- **Tray icon:** shows *connected to service* or *waiting for service*. Use its menu to exit.

## Installing

Steps 1 and 2 write to `C:\Program Files` and register a service, so run them in an **elevated** PowerShell.

### 1. Publish

Use `win-x64` or `win-arm64` to match the machine:

```powershell
dotnet publish src/HADA.Service -c Release -r win-x64 --self-contained -o "C:\Program Files\HADA\service"
dotnet publish src/HADA.Tray -c Release -r win-x64 --self-contained -o "C:\Program Files\HADA\tray"
```

Then edit `C:\Program Files\HADA\service\appsettings.json` and add secrets as described above.

### 2. Register the service (elevated PowerShell)

```powershell
sc.exe create HADA binPath= "C:\Program Files\HADA\service\HADA.Service.exe" start= delayed-auto DisplayName= "Home Assistant Desktop App"
sc.exe failure HADA reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start HADA
```

The service runs as LocalSystem. It reads `appsettings.json` from its install folder and writes warnings and errors to the Windows Event Log (Application log, source `HADA.Service`).

To remove it:

```powershell
sc.exe stop HADA
sc.exe delete HADA
```

### 3. Start the tray at sign-in

```powershell
New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "HADA.Tray" -Value '"C:\Program Files\HADA\tray\HADA.Tray.exe"' -PropertyType String -Force
```

Only one tray instance runs per user session.

## Using it in Home Assistant

### MQTT engine

Entities show up automatically under **Settings → Devices & services → MQTT** as a device named after the PC. The lock screen button can be pressed from the dashboard or used in automations like any other `button` entity.

If the service stops or loses its connection, all of the device's entities turn *unavailable*.

Topics, with `{device}` being `DeviceId`:

| Topic | Content |
|---|---|
| `homeassistant/{sensor\|button}/{device}/{entity}/config` | Discovery config (retained) |
| `hada/{device}/availability` | `online` / `offline` (retained, last will) |
| `hada/{device}/{entity}/state` | Sensor state |
| `hada/{device}/{entity}/attributes` | Sensor attributes as JSON |
| `hada/{device}/{entity}/set` | Button command, payload `PRESS` |

### WebSocket engine

Sensors appear as `sensor.{device}_{entity}`, e.g. `sensor.desktop_01_cpu_load`. Commands are sent by firing the command event, for example from a script:

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
- If Home Assistant rejects the token, the engine stops retrying until the service restarts, so repeated failed logins don't get the PC's IP banned.

## Security notes

- **Pipe access:** `HADA.Session` accepts only local interactive users and denies network access. Only SYSTEM, administrators or the service account can create it. The tray also refuses to send data to a pipe with any other owner.
- **What the tray may send:** the service accepts only sensors from the tray. The tray can't replace entities the service registered, and can't report values for them.
- **Commands:** anyone in Home Assistant who can press the button, publish to the command topic or fire the command event can lock the PC.

## Known limitations

- **Lock screen from the service:** as a service in session 0, the lock action disconnects the active console session, which returns Windows to the lock screen. This hasn't been verified on every Windows edition. Run from a user session, it uses `LockWorkStation`.
- **Several users signed in:** with fast user switching, every signed-in user's tray reports under the same entity IDs.
- **Tray exits:** when the tray exits, its sensors keep their last value until it reconnects.
