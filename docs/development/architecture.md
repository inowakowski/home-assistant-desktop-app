# Architecture

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
- There are two kinds of **communication engines**. Each stays idle until it is configured, and restarts by itself when its settings change:
  - **MQTT** (recommended). Uses MQTT discovery, so entities appear automatically with unique IDs, a device, and availability tracking through a last will. There is one engine per MQTT server, so one per Home Assistant, all fed from the same bus; the `EngineSupervisor` matches them to the servers in the settings by their ids.
  - **WebSocket/REST**. Needs no broker, but has the limitations listed [below](../home-assistant/websocket.md#limitations).

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

## Installed and portable

The same two programs run in two ways, told apart by `AppInstance` in `HADA.Core`: a file named `HADA.portable` one folder above the program makes it a [portable copy](../getting-started/portable.md). Everything processes find each other by (the pipe, the single-instance mutexes, the stop and exit events) carries a suffix derived from the copy's folder, empty for the installed app, so copies never meet. In a portable copy the tray app starts `HADA.Service.exe` as a child process and stops it on exit, settings and logs go to the copy's `data` folder, and per-user preferences go to a JSON file there instead of the registry.
