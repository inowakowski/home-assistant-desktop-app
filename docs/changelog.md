# Changelog

Installers are on the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases). Versions before 1.0 were previews.

## 1.0.0

The first stable version. It is 0.5.0 with one change:

- Test versions (pre-releases) are no longer offered as updates unless **Offer test versions too** is turned on. An installation that saved its settings with 0.5.0 keeps what it had.

From here on, settings and MQTT topics stay compatible from one stable version to the next.

## 0.5.0

- **Settings page:** the installed version, what the last update check found and a **Check now** button; switches for checking automatically and for counting test versions; and personal settings that need no administrator rights: start with Windows, language, and the idle time after which `user_active` turns off.
- The status cards on the Overview page open the page where what they show is set up.
- Whether the daily update check runs is a setting of its own; it used to follow the `update_available` entity's switch.

## 0.4.1

- **Download and install** an update from the window: the installer is downloaded, compared with its published checksum and started.

## 0.4.0

- **Control from Home Assistant:** volume and mute, media keys, display off and wake, sleep, hibernate, shut down and restart (off until switched on), and notifications.
- **Custom buttons** that run a PowerShell command or start a program.
- **New sensors:** signed-in user, IP address, Wi-Fi network, disk usage, default audio device, media playback, do not disturb, update available.
- With several users signed in, only the one at the computer is reported.
- A daily check for a newer version.
- MIT license.

## 0.3.0

- Custom sensor type **Device is connected**, e.g. for a USB-C dock.
- `external_display` binary sensor.

## 0.2.2

- The window runs as a process of its own and is drawn in software, which brought the tray app from about 335 MB after opening the window down to about 20 MB.

## 0.2.1

- **Start with Windows** switch.
- The service recovers when another copy of it held the pipe or shared its MQTT client ID.

## 0.2.0

- Custom sensors: fixed text, program running, PowerShell command.
- More built-in sensors: memory, battery, display, lid, session lock, last boot, user activity, microphone and camera in use, microphone muted.
- MSI installers for x64 and ARM64.
- The window follows the dark theme; entities turn unavailable when their source is away.

## 0.1.0

- First preview: the service, the tray app with its settings window, the MQTT and WebSocket engines, per-entity switches.
