# HADA – Home Assistant Desktop App

HADA runs in the background on Windows. It reports what your PC is doing to [Home Assistant](https://www.home-assistant.io/) and lets Home Assistant control it. A window opened from the tray icon shows live status and lets you configure everything.

![The Overview page of the HADA window](docs/assets/img/overview.png)

- **Sensors:** screen, session lock, user activity, active window, media playback, microphone and camera in use, volume, audio device, battery, network, Wi-Fi, disks, CPU, memory and more.
- **Controls:** volume and mute, media keys, display off and wake, lock, sleep, hibernate, shut down, restart.
- **Notifications** from Home Assistant, shown as Windows notifications.
- **Your own sensors and buttons** without code: whether a program runs or a device is connected, the output of a PowerShell command, a button that runs a command or starts a program.
- A Windows service plus a small tray app: about 15 MB and 20 to 25 MB of memory. Works with nobody signed in.
- MQTT discovery, or a direct WebSocket connection. x64 and ARM64, Windows 10 (1809+) and 11. Polish and English.

Every version so far is a preview: settings and MQTT topics may still change before 1.0.

## Install

Download the installer for your computer from the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases), run it, then open HADA from the tray icon and set up a connection. Step by step: [Installation](docs/getting-started/installation.md) and [First setup](docs/getting-started/first-setup.md).

## Documentation

| | |
|---|---|
| **Getting started** | [Installation](docs/getting-started/installation.md) · [First setup](docs/getting-started/first-setup.md) · [Updating](docs/getting-started/updating.md) |
| **What it does** | [Sensors](docs/features/sensors.md) · [Controls](docs/features/controls.md) · [Notifications](docs/features/notifications.md) · [Custom sensors and buttons](docs/features/custom-entities.md) · [Several users](docs/features/several-users.md) · [The HADA window](docs/window.md) |
| **Home Assistant** | [MQTT engine](docs/home-assistant/mqtt.md) · [WebSocket engine](docs/home-assistant/websocket.md) · [Automation examples](docs/home-assistant/examples.md) |
| **Reference** | [Configuration](docs/reference/configuration.md) · [Logs](docs/reference/logs.md) · [Security](docs/reference/security.md) · [Known limitations](docs/reference/limitations.md) · [Troubleshooting](docs/reference/troubleshooting.md) · [FAQ](docs/faq.md) |
| **Development** | [Architecture](docs/development/architecture.md) · [Building and running](docs/development/building.md) · [Adding entities in code](docs/development/adding-entities.md) |
| | [Changelog](docs/changelog.md) |

The same pages build into a website with [MkDocs](docs/development/building.md#building-this-documentation).

## Build

```powershell
dotnet build
dotnet test
.\scripts\Publish-HADA.ps1   # both installers, into artifacts\installer
```

Needs the [.NET SDK 10](https://dotnet.microsoft.com/download); details in [Building and running](docs/development/building.md).

## License

[MIT](LICENSE). HADA uses [MQTTnet](https://github.com/dotnet/MQTTnet) and [WPF UI](https://github.com/lepoco/wpfui), both under the MIT license, and the .NET runtime it ships with is MIT-licensed too.
