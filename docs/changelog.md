# Changelog

Installers are on the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases). Versions before 1.0 were previews.

## 1.9.0 (pre-release)

HADA for Linux, a first preview: packages for x64 and arm64 that nobody has tried on Linux by hand yet, which their names say. And a fix for all systems. A pre-release, so that it gets tried before a stable version carries it.

- **HADA on Linux, untested.** An AppImage to run as it is, a .deb for Debian and Ubuntu, an .rpm for Fedora and openSUSE, each for x64 and arm64: `HADA-1.9.0-linux-x64-untested.AppImage` and so on. They were built and checked by machines only; once someone has tried them, the same packages come without `-untested`. GNOME and KDE are both meant.
    - **The same window** as on Windows, and an icon in the system tray, from which the window is opened and HADA is ended. Closing the window leaves HADA running in the tray. On GNOME the icon needs the AppIndicator extension, which Ubuntu has.
    - **The service runs as you**, started by the app when you sign in and ending when you sign out: turn on **Start when you sign in** on the Settings page. Your settings are your own; nothing needs unlocking. Saved passwords and tokens are sealed with a key only you can read.
    - **What arrives in Home Assistant:** CPU load, memory, battery and charger, last boot, the signed-in user, whether the session is locked, whether a restart is due, the usage of each disk (`disk_root_usage`, `disk_home_usage`), whether you are active, whether the camera or the microphone records and for which app, the volume, and buttons to lock the screen, sleep, hibernate, shut down and restart (off until switched on). Volume, mute and the microphone's mute can be set from Home Assistant.
    - **Notifications** from Home Assistant appear as the desktop's own, with a picture and buttons; a pressed button goes back to Home Assistant as on Windows.
    - **Not yet on Linux:** the active window, media, the dashboard window, quick actions and their shortcuts, the network and Wi-Fi, the display and the lid, the external display, Do not disturb, the screen's brightness, the GPU. They come with later versions.
- Fixed: right after connecting directly to Home Assistant, a sensor could be registered as an entity twice.

## 1.8.0 (pre-release)

The new window takes over: it has every page now, and the tray icon opens it. A pre-release, so that it gets tried before a stable version carries it.

- **The tray icon opens the new window,** and so do **HADA** in the Start menu and starting HADA again. As before, there is one window: started again, the open one comes to the front.
- **Every page is in the new window.** Besides the Overview and Connections:
    - **Entities**, each with its switch, and a note when the connections you use do not carry all of them.
    - **Custom entities**: sensors, buttons and quick actions of your own.
    - **Settings**: updates, and what is yours alone, such as the language and starting with Windows.
    - **Logs**: the service's messages by level, to copy or clear, following the newest.
- **Back to the previous window** with **New window (preview)** on the Settings page, for as long as both come with HADA. The new window stays at hand as **HADA (new window, preview)** in the Start menu.
- **Unlock editing** opens the window as administrator at the page you were on.
- Fixed: **Start with Windows**, turned on in the new window of a portable copy, registered the window instead of HADA itself to start when you sign in. Turn it off and on again if you turned it on in 1.7.0.

## 1.7.0 (pre-release)

A first look at the new HADA window, which is to be the same on Windows, macOS and Linux. It comes beside the window you know, which stays as it is and is what the tray icon opens. A pre-release, so that it gets tried before a stable version carries it.

- **The new window, a preview.** Open it with **HADA (new window, preview)** in the Start menu, or **New window (preview).cmd** in a portable copy. It has the Overview and Connections pages so far; the other pages say they are still to come, and are in the window you know.
    - The **Overview** has a card for each way of connecting that you use, MQTT or directly, with a row for each server and its state. A notice says when a connection needs attention; close it, and it comes back with the next problem. **Manage** leads to the Connections page, and a server's row to that server.
    - **Connections** lists your servers by the way they connect. A server opens with its state and all of its settings, to test, change or remove. **Add a connection** takes three steps: the way to connect, with what each gives and needs, the address and signing in, and a check that saves it and says what the connection does now.
    - The sidebar can be folded to its icons, and stays so.
    - On Windows it looks like a Windows 11 app, in the light or dark colours and the accent colour you chose.
- Both windows now say in your language, before saving, that two servers are the same broker or the same Home Assistant as the same device, which would make them throw each other off. Before, saving was refused with an English message from the service.
- Stopping a portable copy closes the new window too.

## 1.6.0 (pre-release)

Little to see: this version changes how HADA is built inside. The settings window, the tray app and the service work as before. A pre-release, so that it gets tried before a stable version carries the change.

- Fixed: the service of a portable copy, told to stop while it was still starting, ended with an error instead of stopping.
- Fixed: when HADA stopped right after its first reading of a sensor, a Home Assistant connected to directly could be sent that sensor once more as a new entity, only to show it as unavailable.

## 1.5.1

- Fixed: **Sign in through Home Assistant** opened a window of files instead of the browser when it was chosen in the window unlocked for editing, which is where it is chosen in an installed HADA. The browser is now sent to Home Assistant by way of an address on the computer itself, which works however the browser is opened.
- While waiting for the sign-in, the window shows that address, to open by hand should no browser come up.

## 1.5.0

More for a Home Assistant connected to directly: the computer as a media player, signing in instead of pasting a token, a local address for the networks you call home, and a refusal to send the token unencrypted over the internet. The app now says which of your connections carries what. A stable version, which also brings everything new in 1.3.0 and 1.4.0 to those who use stable versions. The media player needs version 0.4.0 of the HADA integration.

- **No unencrypted connection over the internet.** A Home Assistant connected to directly at an `http://` address is refused when the address is a public one, as the access token could be read on the way; the Overview page says so. Addresses of the computer's own network work as before, and `https://` ones always do. The refusal can be lifted per server on the Connections page, which now also says under the address whether the connection is encrypted. See [Security](reference/security.md#the-connection-to-home-assistant).
- **Signing in through Home Assistant.** Instead of making a long-lived token and pasting it, choose **Sign in through Home Assistant** on the Connections page: the browser opens Home Assistant's own sign-in page, and HADA keeps what comes back. It can be withdrawn in your Home Assistant profile, under Refresh tokens. A pasted token works as before. See [WebSocket engine](home-assistant/websocket.md#signing-in-instead-of-pasting-a-token).
- **Another address on your home network.** A Home Assistant connected to directly can have a second address, used while the computer is on the networks you name as home: the short way, without going out to the internet and back. Anywhere else the main address is used and the local one is not tried at all. The Connections page lists the networks the computer is on, to add with a click. See [WebSocket engine](home-assistant/websocket.md#another-address-on-your-home-network).
- **A dashboard for every Home Assistant.** Each Home Assistant connected to directly has a dashboard in the tray icon's menu by itself, at the connection's address; on the Settings page each can be switched off or given an address of your own. The address typed there before still works, as **Another address**. With several dashboards the menu lists them by name, and the one window shows whichever is chosen. See [The dashboard window](window.md#the-dashboard-window).
- **The computer as a media player.** Through the HADA integration the computer is a media player in Home Assistant: what is playing, play, pause, next, previous, stop, volume and mute in one entity. It is a new entity on the Entities page; the media buttons, the volume slider and the playback sensor stay as they are, and are what MQTT and a connection without the integration have. Needs the HADA integration from its next version on. See [Controls](features/controls.md#the-media-player).
- **What works through which connection,** in one table: [Which connection to choose](home-assistant/choosing.md#what-works-through-which-connection).
- **Two more entities.** `restart_required` is on while Windows waits for a restart to finish installing updates. `screen_brightness` shows the brightness of a laptop's screen and lets Home Assistant set it. Both work through MQTT and through the HADA integration; a connection without the integration shows the brightness without the slider.
- **The Entities page says what does not arrive.** A connection that carries less than the list offers is named there with what is missing: a direct connection without the HADA integration has no buttons, switches or sliders to click in Home Assistant, for instance.
- **Quick actions with any user's token.** What happens on the computer reached Home Assistant only with an administrator's token, unless the HADA integration was used. With **Notifications** on, or sensors sent as entities of the device, it now arrives with the token of an ordinary user too.
- **Readings sent together.** What several sensors report at the same moment reaches a Home Assistant connected to directly in one message, through the HADA integration and as entities of the device alike, instead of one message each; and only the newest reading of a sensor is sent when Home Assistant is slow to answer. Nothing to set up.
- **An integration that is behind says so.** When the HADA integration in Home Assistant is older than HADA and leaves entities out for not knowing their kind, the Overview page says that it wants updating.
- Fixed: a button pressed in Home Assistant in the very moment the computer connected to the HADA integration could be lost.

## 1.4.0 (pre-release)

Home Assistant without a broker: notifications as on a phone, and the computer as a device with entities of its own, through Home Assistant's `mobile_app` integration or through the new HADA integration. A pre-release, so that it gets tried before a stable version carries it. MQTT works as before.

- **Help with choosing a connection.** The Connections page says in plain words what MQTT, a direct connection and a direct connection with the HADA integration each need and give, and so does a new page of the documentation, [Which connection to choose](home-assistant/choosing.md).
- **Entities through the HADA integration.** A third way for the sensors of a Home Assistant connected to directly: the [HADA integration](https://github.com/inowakowski/hada-homeassistant), installed in Home Assistant with HACS, makes the computer a device whose entities follow the computer's, are removed when it no longer has them, and are unavailable the moment it goes away. Its buttons, switches and numbers are real ones, to press and set in Home Assistant as with MQTT, without a broker. Notifications have the action `hada.notify`, with all a phone's notification may carry, and quick actions are `event` entities. See [WebSocket engine](home-assistant/websocket.md#sensors-through-the-hada-integration).
- **Sensors as real entities without MQTT.** A Home Assistant connected to through the WebSocket API can get the sensors as entities of a device, with a unique ID: they can be renamed and managed in Home Assistant, survive its restarts, and need no administrator's token. New connections do this; existing ones keep writing states until changed on the Connections page, as the entity IDs differ. See [WebSocket engine](home-assistant/websocket.md#what-the-connection-is-used-for).
- **Notifications as on a phone.** With **Notifications** switched on for a Home Assistant connected to through the WebSocket API, the computer registers with the `mobile_app` integration and Home Assistant has the action `notify.mobile_app_…`, with a title, a picture and buttons; a pressed button fires `mobile_app_notification_action`. Works next to MQTT, with **Entities** set to **Off** for that connection, and with the token of an ordinary user. See [Notifications](features/notifications.md#notifications-as-on-a-phone).
- **More in a notification,** through `mobile_app` and through MQTT alike: `tag` to replace a notification and `clear_notification` to take it back, an address opened when it or one of its buttons is pressed, `sticky` and `silent`. Through `mobile_app`, a picture may be one that needs signing in at Home Assistant, such as a camera's. Which browser the addresses open in can be chosen on the Settings page. See [Notifications](features/notifications.md#more-than-a-text).
- **Several Home Assistants through the WebSocket API:** as with MQTT servers, up to eight, each with its own name, address and access token, connected at the same time. Each has its own card on the Overview page. The one Home Assistant set up before stays as the first server. See [WebSocket engine](home-assistant/websocket.md#several-home-assistants-at-once).
- Stopping a portable copy, with **Exit** in the icon's menu or with **Stop HADA.cmd**, closes its windows too. Before, a window left open kept the program's files in use, and the folder could not be replaced or deleted.

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
