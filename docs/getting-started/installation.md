# Installation

## What you need

- Windows 10 version 1809 or newer, or Windows 11; x64 or ARM64.
- Home Assistant with one of these:
    - the [MQTT integration](https://www.home-assistant.io/integrations/mqtt/) and a broker, e.g. the Mosquitto add-on. This is the recommended way. The `notification` entity needs Home Assistant 2024.5 or newer.
    - a long-lived access token for an **administrator** account, for the WebSocket engine, which needs no broker but can do less.

## With the installer

Download the installer that matches the computer from the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases): `HADA-<version>-x64.msi` for Intel and AMD processors, `HADA-<version>-arm64.msi` for ARM64. Each release also has a `SHA256SUMS.txt` to check the download against. The installers are self-contained, so the computer needs no .NET.

Double-click the installer and follow the three pages. It:

- copies HADA to `C:\Program Files\HADA`
- registers the `HADA` service (LocalSystem, starts with Windows, restarts a minute after a crash) and starts it
- starts the tray app at sign-in for every user, and adds **HADA** to the Start menu. Each user can turn this off with **Start with Windows** on the Settings page; the service itself always starts with Windows
- offers to open the HADA window on the last page, where you choose **Unlock editing** and set up a connection

To update, run a newer installer, or use **Download and install** on the Overview page when it offers a newer version; either replaces the old version and keeps the settings. To remove HADA, use **Settings → Apps → Installed apps**. Settings and logs in `%ProgramData%\HADA` are left in place; delete that folder to forget them.

For unattended installation: `msiexec /i HADA-<version>-x64.msi /qn`. The tray app then starts at the next sign-in.

> The installers are not code-signed, so Windows SmartScreen may warn before running them. Choose **More info → Run anyway**, or sign them with your own certificate.


Next: [First setup](first-setup.md).

## By hand

For when you build HADA yourself and do not want an installer.

Steps 1 and 2 write to `C:\Program Files` and register a service, so run them in an **elevated** PowerShell.

### 1. Publish

Use `win-x64` or `win-arm64` to match the machine:

```powershell
dotnet publish src/HADA.Service -c Release -r win-x64 --self-contained -o "C:\Program Files\HADA\service"
dotnet publish src/HADA.Tray -c Release -r win-x64 --self-contained -o "C:\Program Files\HADA\tray"
```

### 2. Register the service (elevated PowerShell)

```powershell
sc.exe create HADA binPath= "C:\Program Files\HADA\service\HADA.Service.exe" start= delayed-auto DisplayName= "Home Assistant Desktop App"
sc.exe failure HADA reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start HADA
```

The service runs as LocalSystem. See [Logs](../reference/logs.md) for where it reports problems.

To remove it:

```powershell
sc.exe stop HADA
sc.exe delete HADA
```

### 3. Start the tray at sign-in

```powershell
New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "HADA.Tray" -Value '"C:\Program Files\HADA\tray\HADA.Tray.exe" --background --autostart' -PropertyType String -Force
```

Only one tray instance runs per user session. Then open the window from the tray icon and configure a connection.
