# Architecture

```
┌──────────── user session ────────────┐          ┌─────────────── session 0 ───────────────┐
│ HADA.Tray (notification icon)        │  named   │ HADA.Service (Windows service, SYSTEM)  │
│  • Session sensors (window, audio,   │  pipe    │  • System sensors, custom sensors       │
│    media, activity, mic, camera)     │ ───────► │    and buttons                          │
│  • Session actions (volume, media    │ ◄─────── │  • Lock and power actions               │
│    keys, display, notifications)     │ commands │  • IpcServer (sensors + control API)    │
│  • IpcClient                         │          │  • EngineSupervisor                     │
│  • Opens the window (HADA.App)       │ ◄──────► │     • MqttEngine ──────► MQTT broker    │
└──────────────────────────────────────┘          │     • HaWebSocketEngine ► Home Assistant│
                                                  └─────────────────────────────────────────┘
```

- **HADA.Service** runs as a Windows service (on Linux and macOS, as a process of the user, started by the app). It holds the connections to Home Assistant, owns the settings, and runs anything that doesn't need the user's desktop.
- **HADA.Tray** runs in the logged-in user's session on Windows. It reads things a service can't see, such as the focused window and the audio device, and streams them to the service over the `HADA.Session` named pipe. Commands from Home Assistant for the tray's entities come back over the same pipe.
- **HADA.App** is the window, drawn with Avalonia, the same on Windows, Linux and macOS. The tray icon starts it as a process of its own, which exits when the window is closed: a window costs far more memory than the tray icon and the sensors, and this way that memory is only used while the window is open. It uses the same pipe to read status and logs and to change settings. On Windows the tray app also still has a window of its own (WPF), which the **New window (preview)** switch on the Settings page brings back. On Linux and macOS `HADA.App` is also the agent: it holds the tray icon or menu bar icon and the sensors of the user's session, starts the service as a child process, and has no administrator window to unlock, since the service runs as the user.
- Sensors, actions and engines talk only through an in-process event bus (`HADA.Core`). Sensors never reference MQTT or WebSocket code.
- There are two kinds of **communication engines**. Each stays idle until it is configured, and restarts by itself when its settings change:
  - **MQTT** (recommended). Uses MQTT discovery, so entities appear automatically with unique IDs, a device, and availability tracking through a last will. There is one engine per MQTT server, so one per Home Assistant, all fed from the same bus; the `EngineSupervisor` matches them to the servers in the settings by their ids.
  - **WebSocket/REST**. Needs no broker, but has the limitations listed [below](../home-assistant/websocket.md#limitations).

| Project | Purpose |
|---|---|
| `HADA.Core` | Models, event bus, entity registry and filter, engine abstraction, file logging, and the sensors and actions every system has (`Sensors`, `Actions`), which read and do through interfaces such as `ISystemReadings` or `IPowerControl`. `Unix` holds what Linux and macOS share |
| `HADA.Engine.Mqtt` | MQTT engine (MQTTnet) |
| `HADA.Engine.WebSocket` | Home Assistant WebSocket + REST engine |
| `HADA.Ipc` | Named pipe protocol: sensor stream and control API, server and clients |
| `HADA.Platform.Windows` | Win32, Core Audio and WLAN readings, sensors and actions for Windows. The only project besides `HADA.Tray` that may call Windows APIs |
| `HADA.Platform.Linux`, `HADA.Platform.MacOS` | The same for Linux (`/proc`, D-Bus, X11, `systemd-logind`) and for macOS (kernel statistics, `pmset`, CoreAudio, CoreGraphics, `osascript`) |
| `HADA.Service` | Worker service host, settings storage, engine supervisor, custom sensors and buttons, update check |
| `HADA.Presentation` | View models, strings (`Strings.resx`, `Strings.pl.resx`) and user preferences of the window, with no UI toolkit; what they need from the system or the toolkit goes through `IDesktop` |
| `HADA.App` | The window (Avalonia), and on Linux and macOS the agent as well |
| `HADA.Tray` | The tray app on Windows (WPF): notifications, the media playback sensor (Windows Runtime), the dashboard window and the previous window (WPF-UI). The only project that targets `net10.0-windows`; every other one, tests included, targets plain `net10.0` |
| `HADA.Tests` | xUnit tests, which run on Windows, Ubuntu and macOS in CI. A test of something only Windows has is marked for Windows |
| `installer` | WiX project that packs the published apps into an MSI. Built by `scripts\Publish-HADA.ps1`, not by the solution. The packages for Linux and macOS are built by `scripts/package-linux.sh` and `scripts/package-macos.sh`, in CI |

## Installed and portable

The same two programs run in two ways, told apart by `AppInstance` in `HADA.Core`: a file named `HADA.portable` one folder above the program makes it a [portable copy](../getting-started/portable.md). Everything processes find each other by (the pipe, the single-instance mutexes, the stop and exit events) carries a suffix derived from the copy's folder, empty for the installed app, so copies never meet. In a portable copy the tray app starts `HADA.Service.exe` as a child process and stops it on exit, settings and logs go to the copy's `data` folder, and per-user preferences go to a JSON file there instead of the registry.
