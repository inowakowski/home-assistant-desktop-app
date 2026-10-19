# Linux and macOS (preview)

HADA runs on Linux and on macOS too, with most of what it does on Windows. The packages are a **preview that nobody has tried by hand yet**.

!!! warning "Untested"
    The Linux and macOS packages were built and checked by machines only: unit tests on Ubuntu and macOS, and a start of the installed package without a screen. Nobody has used them on a Linux or a macOS computer, and the file names say so: `HADA-<version>-linux-x64-untested.deb`, `HADA-<version>-macos-arm64-untested.dmg`. Once someone has tried them, the same packages come without `-untested`.

    Try them, and tell what you find as [issues on GitHub](https://github.com/inowakowski/home-assistant-desktop-app/issues). Do not rely on them yet. Windows is the system HADA is made on and tried on.

## Get it

Download the package that matches the computer from the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases). Each is self-contained, so the computer needs no .NET.

| System | Package | For |
|---|---|---|
| Linux | `HADA-<version>-linux-x64-untested.AppImage`, `.deb`, `.rpm` | Intel and AMD processors |
| Linux | `HADA-<version>-linux-arm64-untested.AppImage`, `.deb`, `.rpm` | ARM64 |
| macOS | `HADA-<version>-macos-arm64-untested.dmg` | Apple silicon |
| macOS | `HADA-<version>-macos-x64-untested.dmg` | Intel |

- **Linux.** The AppImage runs as it is, without installing. The `.deb` is for Debian and Ubuntu and the systems built on them, the `.rpm` for Fedora and openSUSE. GNOME and KDE are both meant. On GNOME the icon in the system tray needs the AppIndicator extension, which Ubuntu has.
- **macOS 14 Sonoma or newer.** Open the image and drag HADA into Applications. The app is not signed by an Apple developer, so the first time macOS refuses to open it: allow it under **System Settings → Privacy & Security → Open Anyway**. HADA has no icon in the Dock; its icon is in the menu bar.

Then set up the connection to Home Assistant as described under [First setup](first-setup.md), with the differences below.

## How it differs from Windows

- **One program.** The app holds the icon (in the system tray, or in the menu bar on a Mac), the [window](../window.md), and the sensors of your session, and starts the service as a process of its own. The service runs as you, with no administrator and no root, and ends when the app does.
- **Closing the window does not stop HADA.** It goes on in the tray or the menu bar; open the window again from the icon, and end HADA from its menu. The first time, the window says so.
- **Nothing to unlock.** The settings are your own, so there is no **Unlock editing**, as with a [portable copy](portable.md) on Windows.
- **Starting at sign-in.** Turn on **Start when you sign in** on the **Settings** page: on Linux that is an entry in your autostart folder, `~/.config/autostart`, on a Mac a login item that **System Settings** then lists under Login Items.
- **Custom sensors and buttons** run their commands with `sh`, as you, not with PowerShell. A [custom button](../features/custom-entities.md) that starts a program works on the desktop of your session.
- **Quick actions and the dashboard.** Quick actions are in the icon's menu. The Home Assistant dashboard opens in your browser, not in a window of HADA.
- **Updating.** Install the newer package yourself, from the releases page: the AppImage replaced, the `.deb` or `.rpm` with the package manager, the app dragged over the old one. The **Overview** and **Settings** pages tell when a newer version exists, and **What is new** opens its release page. There is no **Download and install** button: it belongs to the Windows installer, and HADA cannot tell which of these packages it came from or start any of them for you. Your settings stay in their folders (see below).

## Where it keeps things

| | Linux | macOS |
|---|---|---|
| Settings | `~/.config/hada` (or `$XDG_CONFIG_HOME/hada`) | `~/Library/Application Support/HADA` |
| Logs | `~/.local/state/hada/logs` (or `$XDG_STATE_HOME/hada/logs`) | `~/Library/Logs/HADA` |
| Other files of the app, and the key for saved passwords (`secret.key`) | `~/.local/share/hada` (or `$XDG_DATA_HOME/hada`) | `~/Library/Application Support/HADA` |

The **Logs** page of the window shows the service's recent entries on every system. Removing HADA leaves these folders in place; delete them to forget everything.

## What arrives in Home Assistant

Entity IDs are the same as on Windows; see [Sensors](../features/sensors.md) and [Controls](../features/controls.md) for what each one means. A dash means the system has no such entity.

| Entity | Linux | macOS |
|---|---|---|
| `cpu_load`, `memory_usage`, `battery_level`, `battery_charging`, `plugged_in`, `last_boot`, `active_user`, `session_locked`, `user_active` | Yes | Yes |
| `disk_…_usage` | Yes: `disk_root_usage`, `disk_home_usage` and so on, by mount point | Yes |
| `ip_address` | Yes | Yes |
| `wifi_network` | Yes, with the signal strength | Yes. macOS shows the network's name only to apps allowed to know the location; without it the sensor says `connected` |
| `lid_open`, `external_display`, `gpu_load` | Yes. The GPU load of AMD cards, and of NVIDIA's through `nvidia-smi` | Yes |
| `display_on`, `turn_off_display`, `wake_display` | Yes: through GNOME on Wayland and X11, through KDE (which switches the screens but does not say how they are), and on other X11 desktops | Yes. With the Mac set to ask for the password at once, turning the screens off locks it too |
| `screen_brightness` | Yes, of a laptop's screen | Yes, of the Mac's own screen |
| `active_window` | Only in an X11 session: Wayland does not tell apps which window is in front | The app in front, by its name |
| `audio_volume`, `audio_device`, `microphone_muted`, `volume_level`, `audio_mute`, `microphone_mute` | Yes | Yes. The microphone is muted by setting its input volume to zero |
| `microphone_in_use`, `camera_in_use` | Yes, and for which app | Yes, but macOS does not say which app |
| `media_playback`, `media_player`, `media_stop` | Yes: what plays in any player that the desktop's media controls show | — |
| `media_play_pause`, `media_next`, `media_previous` | Yes | Yes, by pressing the keyboard's media keys. A Mac's keyboard has no stop key |
| `do_not_disturb`, `silence_notifications` | Yes, on GNOME and KDE: GNOME's by switching notification banners off, KDE's only for as long as HADA runs | — |
| `notification` | Yes, as the desktop's own notifications, with a picture and buttons; a pressed button goes back to Home Assistant | In Notification Center, with the title and the text, without pictures or buttons |
| `restart_required` | Yes | — |
| `lock_screen`, `sleep`, `shutdown`, `restart` | Yes. The last three are off until you switch them on | Yes, in the same way. The first time Home Assistant shuts the Mac down or restarts it, macOS asks whether HADA may control the login window |
| `hibernate` | Yes, off until switched on | — A Mac cannot hibernate on demand |
| Buttons that press keys | Only in an X11 session: Wayland does not let apps type into others | Yes, once HADA is allowed under **System Settings → Privacy & Security → Accessibility**; until then nothing is pressed, and the log says why |
| Keyboard shortcuts of quick actions | Only in an X11 session. X11 keeps `Ctrl+Alt` with `F1` to `F12` for switching to a text console | Yes |
| Custom sensor of a connected USB device | Yes | Yes |

The connection to Home Assistant is the same on every system: MQTT, direct, or direct with the HADA integration. See [Which connection to choose](../home-assistant/choosing.md).

## Security

What [Security](../reference/security.md) says holds in the main, with these differences:

- **The service has the rights of your account**, not those of the system. Whoever can read your files can read HADA's settings, and root can read everything.
- **Passwords and tokens** are sealed with AES-GCM under a key that HADA makes the first time one is saved. The key is the file `secret.key` in your own data folder (see above), readable by you only and kept apart from the settings, so a copy of the settings file alone is useless. The system's own stores, Secret Service on Linux and the Keychain on a Mac, are not used yet.
- **The processes of HADA find each other through sockets** in your own runtime folder, which nobody else can enter.
- **Commands** from Home Assistant can do what the entities that are switched on allow, as on Windows. Shutting down, restarting, sleeping and hibernating stay off until you turn them on.

## Not yet

- On Linux: anything that only X11 has, when the session is Wayland (see the table); Secret Service for the saved passwords.
- On macOS: Do not disturb, the pictures and buttons of notifications, the playback sensor and media player, hibernate, the restart-required sensor.
- On both: updating from the window (a newer version is installed by hand, as described under Updating above), and a package nobody has tried. Bugs and wishes are welcome as [issues on GitHub](https://github.com/inowakowski/home-assistant-desktop-app/issues).

The [changelog](../changelog.md) says in which version each of these arrived.
