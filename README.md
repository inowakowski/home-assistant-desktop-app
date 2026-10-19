# HADA – Home Assistant Desktop App

HADA runs in the background on Windows, and as an untested preview on Linux and macOS. It reports what your PC is doing to [Home Assistant](https://www.home-assistant.io/) and lets Home Assistant control it. A window opened from the tray icon shows live status and lets you configure everything.

![The Overview page of the HADA window](docs/assets/img/overview.png)

- **Sensors:** screen, session lock, user activity, active window, media playback, microphone and camera in use, volume, audio device, battery, network, Wi-Fi, disks, CPU, memory and more.
- **Controls:** volume and mute, media keys, display off and wake, lock, sleep, hibernate, shut down, restart.
- **Notifications** from Home Assistant, with pictures and buttons.
- **Your own sensors and buttons** without code: whether a program runs or a device is connected, the output of a PowerShell command, a button that runs a command, starts a program or presses keys.
- **Quick actions:** start Home Assistant automations from the tray icon's menu or a keyboard shortcut. A small window for your dashboard.
- A Windows service plus a small tray app: about 15 MB and 20 to 25 MB of memory. Works with nobody signed in.
- MQTT discovery, or a direct connection without a broker, on its own or with the [HADA integration](https://github.com/inowakowski/hada-homeassistant) for Home Assistant. x64 and ARM64, Windows 10 (1809+) and 11. Polish and English.

## Install

Download the installer for your computer from the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases), run it, then open HADA from the tray icon and set up a connection. Or take the [portable version](docs/getting-started/portable.md): a ZIP to unpack and run, without installing and without administrator rights. Step by step: [Installation](docs/getting-started/installation.md) and [First setup](docs/getting-started/first-setup.md). For Linux and macOS, see [Linux and macOS (preview)](docs/getting-started/linux-macos.md).

## Documentation

| | |
|---|---|
| **Getting started** | [Installation](docs/getting-started/installation.md) · [Portable version](docs/getting-started/portable.md) · [Linux and macOS](docs/getting-started/linux-macos.md) · [First setup](docs/getting-started/first-setup.md) · [Updating](docs/getting-started/updating.md) |
| **What it does** | [Sensors](docs/features/sensors.md) · [Controls](docs/features/controls.md) · [Notifications](docs/features/notifications.md) · [Custom sensors and buttons](docs/features/custom-entities.md) · [Several users](docs/features/several-users.md) · [The HADA window](docs/window.md) |
| **Home Assistant** | [Which connection to choose](docs/home-assistant/choosing.md) · [MQTT engine](docs/home-assistant/mqtt.md) · [WebSocket engine](docs/home-assistant/websocket.md) · [Automation examples](docs/home-assistant/examples.md) |
| **Reference** | [Configuration](docs/reference/configuration.md) · [Logs](docs/reference/logs.md) · [Security](docs/reference/security.md) · [Known limitations](docs/reference/limitations.md) · [Troubleshooting](docs/reference/troubleshooting.md) · [FAQ](docs/faq.md) |
| **Development** | [Architecture](docs/development/architecture.md) · [Building and running](docs/development/building.md) · [Adding entities in code](docs/development/adding-entities.md) |
| | [Changelog](docs/changelog.md) |

The same pages build into a website with [MkDocs](docs/development/building.md#building-this-documentation).

## Build

```powershell
dotnet build
dotnet test
.\scripts\Publish-HADA.ps1   # installers and portable ZIPs, into artifacts\installer
```

Needs the [.NET SDK 10](https://dotnet.microsoft.com/download); details in [Building and running](docs/development/building.md).

## License

[MIT](LICENSE). HADA is built on, and ships with, these, whose licenses ask for their notices to be kept:

- [MQTTnet](https://github.com/dotnet/MQTTnet), [Avalonia](https://github.com/AvaloniaUI/Avalonia), [FluentIcons.Avalonia](https://github.com/davidxuang/FluentIcons), [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) and [WPF UI](https://github.com/lepoco/wpfui), under the MIT license
- the Inter typeface, which comes with Avalonia's font package, under the SIL Open Font License 1.1
- the [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) package, under the license in the package, which drives the Edge WebView2 Runtime that Windows brings along
- the .NET runtime, which is MIT-licensed too
