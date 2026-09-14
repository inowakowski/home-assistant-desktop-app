# Project: Home Assistant Desktop App (HADA)

Native Windows background integration for Home Assistant (.NET 8/9).
Target Architectures: win-x64, win-arm64.

## Architecture Guidelines

- **Multi-Process Model**: 
  - `WinHA.Service`: Windows Background Service (runs as SYSTEM for power control, persistent connection).
  - `WinHA.Tray`: WPF/WinUI 3 app (runs in user session for active window, audio endpoints, UI).
  - `WinHA.Core`: Shared models, event bus, abstractions.
- **IPC**: Named Pipes using System.IO.Pipes or gRPC over Named Pipes.
- **Communication Engines**:
  - `Engine A`: MQTT (MQTTnet) with HA Discovery and LWT.
  - `Engine B`: Direct HA WebSocket API + REST/Webhooks.
- **Decoupling**: All sensors/actions must communicate via an internal `IEventBus`. Neither sensors nor actions directly reference MQTT or WebSocket implementations.

## Build & Test Commands

- Build solution: `dotnet build`
- Build x64: `dotnet publish -r win-x64 -c Release --self-contained`
- Build ARM64: `dotnet publish -r win-arm64 -c Release --self-contained`
- Run tests: `dotnet test`
