# Changelog

Installers are on the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases). Versions before 1.0 were previews.

## Unreleased

- **Entities through the HADA integration.** A third way for the sensors of a Home Assistant connected to directly: the [HADA integration](https://github.com/inowakowski/hada-homeassistant), installed in Home Assistant with HACS, makes the computer a device whose entities follow the computer's, are removed when it no longer has them, and are unavailable the moment it goes away. Its buttons, switches and numbers are real ones, to press and set in Home Assistant as with MQTT, without a broker. An early version: notifications and quick actions do not go through it yet. See [WebSocket engine](home-assistant/websocket.md#sensors-through-the-hada-integration).
- **Sensors as real entities without MQTT.** A Home Assistant connected to through the WebSocket API can get the sensors as entities of a device, with a unique ID: they can be renamed and managed in Home Assistant, survive its restarts, and need no administrator's token. New connections do this; existing ones keep writing states until changed on the Connections page, as the entity IDs differ. See [WebSocket engine](home-assistant/websocket.md#what-the-connection-is-used-for).
- **Notifications as on a phone.** With **Notifications** switched on for a Home Assistant connected to through the WebSocket API, the computer registers with the `mobile_app` integration and Home Assistant has the action `notify.mobile_app_…`, with a title, a picture and buttons; a pressed button fires `mobile_app_notification_action`. Works next to MQTT, with **Sensors** set to **Off** for that connection, and with the token of an ordinary user. See [Notifications](features/notifications.md#notifications-as-on-a-phone).
- **More in a notification,** through `mobile_app` and through MQTT alike: `tag` to replace a notification and `clear_notification` to take it back, an address opened when it or one of its buttons is pressed, `sticky` and `silent`. Through `mobile_app`, a picture may be one that needs signing in at Home Assistant, such as a camera's. Which browser the addresses open in can be chosen on the Settings page. See [Notifications](features/notifications.md#more-than-a-text).
- **Several Home Assistants through the WebSocket API:** as with MQTT servers, up to eight, each with its own name, address and access token, connected at the same time. Each has its own card on the Overview page. The one Home Assistant set up before stays as the first server. See [WebSocket engine](home-assistant/websocket.md#several-home-assistants-at-once).

## 1.3.0 (pre-release)

Nothing new to see: this version changes what HADA is built on, and is a pre-release so that it gets tried before a stable version carries it.

- HADA runs on .NET 10, the long-term release, instead of .NET 9, whose support ends in November 2026. The installers and the portable version bring it along, as before.
- What is specific to Windows is kept apart from the rest, as groundwork for macOS and Linux versions. Those do not exist yet.

## 1.2.0

A stable version, which also brings everything new in 1.1.0 to those who use stable versions.

- **Several Home Assistants at once:** any number of MQTT servers, up to eight, each with a name of its own, connected at the same time. Each has its own card on the Overview page; the buttons of a notification answer the Home Assistant that sent it.
- **Broker names that stand for several addresses**, as `.local` names do, are tried address by address, IPv4 first, and the one that answered is remembered. The log says what a name stands for, and why a name could not be found.
- A server that cannot be reached is mentioned in the log once, not at every retry.
- **Portable version:** a ZIP to unpack and run, without installing and without administrator rights. It keeps everything in its own folder and can run next to an installed HADA.
- `HADA.Tray.exe --exit` tells the running tray app to exit.
- **Security:** the service checks who owns `%ProgramData%\HADA` before reading anything from it. A folder someone other than an administrator made is set aside instead of trusted. See [Security](reference/security.md#passwords-and-tokens).

Settings saved by earlier versions are taken over: their one MQTT server becomes the first of the list, with its password. The tray app and the service of 1.2.0 work only with each other.

## 1.1.0 (pre-release)

- **Quick actions:** entries in the tray icon's menu, optionally with a global keyboard shortcut, that show up in Home Assistant as triggers of the device.
- **Notifications with a picture and buttons.** A pressed button is reported back to Home Assistant.
- **Button: press keys**, a custom button that presses a key combination on the desktop.
- **Dashboard window:** a small window showing a Home Assistant address, opened from the tray icon's menu.
- **`gpu_load` sensor**, and the adapter's `mac_address` as an attribute of `ip_address`.

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
