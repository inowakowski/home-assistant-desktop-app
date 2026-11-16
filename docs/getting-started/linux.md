# Linux (preview)

HADA runs on Linux too, with most of what it does on Windows. The packages are a **preview that nobody has tried by hand yet**.

!!! warning "Untested"
    The Linux packages were built and checked by machines only: unit tests on Ubuntu, and a start of the installed package without a screen. Nobody has used them on a Linux computer, and the file names say so: `HADA-<version>-linux-x64-untested.deb`. Once someone has tried them, the same packages come without `-untested`.

    Try them, and tell what you find as [issues on GitHub](https://github.com/inowakowski/home-assistant-desktop-app/issues). Do not rely on them yet. Windows is the system HADA is made on and tried on.

## Get it

Download the package that matches the computer from the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases). Each is self-contained, so the computer needs no .NET.

| Package | For |
|---|---|
| `HADA-<version>-linux-x64-untested.AppImage`, `.deb`, `.rpm` | Intel and AMD processors |
| `HADA-<version>-linux-arm64-untested.AppImage`, `.deb`, `.rpm` | ARM64 |

The AppImage runs as it is, without installing. The `.deb` is for Debian and Ubuntu and the systems built on them, the `.rpm` for Fedora and openSUSE. GNOME and KDE are both meant. On GNOME the icon in the system tray needs the AppIndicator extension, which Ubuntu has.

Then set up the connection to Home Assistant as described under [First setup](first-setup.md), with the differences below.

## How it differs from Windows

- **One program.** The app holds the icon in the system tray, the [window](../window.md), and the sensors of your session, and starts the service as a process of its own. The service runs as you, with no administrator and no root, and ends when the app does.
- **Closing the window does not stop HADA.** It goes on in the tray; open the window again from the icon, and end HADA from its menu. The first time, the window says so.
- **Nothing to unlock.** The settings are your own, so there is no **Unlock editing**, as with a [portable copy](portable.md) on Windows.
- **Starting at sign-in.** Turn on **Start when you sign in** on the **Settings** page: that is an entry in your autostart folder, `~/.config/autostart`.
- **Custom sensors and buttons** run their commands with `sh`, as you, not with PowerShell. A [custom button](../features/custom-entities.md) that starts a program works on the desktop of your session.
- **Quick actions and the dashboard.** Quick actions are in the icon's menu. The Home Assistant dashboard opens in your browser, not in a window of HADA.
- **Updating.** Install the newer package yourself, from the releases page: the AppImage replaced, the `.deb` or `.rpm` with the package manager. The **Overview** and **Settings** pages tell when a newer version exists, and **What is new** opens its release page. There is no **Download and install** button: it belongs to the Windows installer, and HADA cannot tell which of these packages it came from or start any of them for you. Your settings stay in their folders (see below).

## Where it keeps things

| | Folder |
|---|---|
| Settings | `~/.config/hada` (or `$XDG_CONFIG_HOME/hada`) |
| Logs | `~/.local/state/hada/logs` (or `$XDG_STATE_HOME/hada/logs`) |
| Other files of the app, and the key for saved passwords (`secret.key`) | `~/.local/share/hada` (or `$XDG_DATA_HOME/hada`) |

The **Logs** page of the window shows the service's recent entries on every system. Removing HADA leaves these folders in place; delete them to forget everything.

## What arrives in Home Assistant

Entity IDs are the same as on Windows; see [Sensors](../features/sensors.md) and [Controls](../features/controls.md) for what each one means.

| Entity | On Linux |
|---|---|
| `cpu_load`, `memory_usage`, `battery_level`, `battery_charging`, `plugged_in`, `last_boot`, `active_user`, `session_locked`, `user_active`, `idle_time` | Yes |
| `disk_…_usage` | Yes: `disk_root_usage`, `disk_home_usage` and so on, by mount point |
| `ip_address` | Yes |
| `network_download`, `network_upload` | Yes |
| `wifi_network` | Yes, with the signal strength |
| `lid_open`, `external_display`, `gpu_load` | Yes. The GPU load of AMD cards, and of NVIDIA's through `nvidia-smi` |
| `display_on`, `turn_off_display`, `wake_display` | Yes: through GNOME on Wayland and X11, through KDE (which switches the screens but does not say how they are), and on other X11 desktops |
| `screen_brightness` | Yes, of a laptop's screen |
| `active_window` | Only in an X11 session: Wayland does not tell apps which window is in front |
| `fullscreen_app` | Only in an X11 session, from the window in front; `kind` is always `app` |
| `audio_volume`, `audio_device`, `microphone_muted`, `volume_level`, `audio_mute`, `microphone_mute` | Yes |
| `microphone_in_use`, `camera_in_use` | Yes, and for which app |
| `media_playback`, `media_player`, `media_stop` | Yes: what plays in any player that the desktop's media controls show |
| `media_play_pause`, `media_next`, `media_previous` | Yes |
| `do_not_disturb`, `silence_notifications` | Yes, on GNOME and KDE: GNOME's by switching notification banners off, KDE's only for as long as HADA runs |
| `notification` | Yes, as the desktop's own notifications, with a picture and buttons; a pressed button goes back to Home Assistant |
| `restart_required` | Yes |
| `lock_screen`, `sleep`, `shutdown`, `restart` | Yes. The last three are off until you switch them on |
| `keep_awake` | Yes, through systemd-logind, as `systemd-inhibit --list` shows |
| `dark_mode` | Yes, on GNOME (its colour scheme) and KDE (Breeze Dark and Breeze Light) |
| `sign_out` | Yes, on GNOME and KDE; off until you switch it on |
| `hibernate` | Yes, off until switched on |
| Buttons that press keys | Only in an X11 session: Wayland does not let apps type into others |
| Keyboard shortcuts of quick actions | Only in an X11 session. X11 keeps `Ctrl+Alt` with `F1` to `F12` for switching to a text console |
| Custom sensor of a connected USB device | Yes |

The connection to Home Assistant is the same on every system: MQTT, direct, or direct with the HADA integration. See [Which connection to choose](../home-assistant/choosing.md).

## Security

What [Security](../reference/security.md) says holds in the main, with these differences:

- **The service has the rights of your account**, not those of the system. Whoever can read your files can read HADA's settings, and root can read everything.
- **Passwords and tokens** are sealed with AES-GCM under a key that HADA makes the first time one is saved. The key is the file `secret.key` in your own data folder (see above), readable by you only and kept apart from the settings, so a copy of the settings file alone is useless. The system's own store, Secret Service, is not used yet.
- **The processes of HADA find each other through sockets** in your own runtime folder, which nobody else can enter.
- **Commands** from Home Assistant can do what the entities that are switched on allow, as on Windows. Shutting down, restarting, sleeping and hibernating stay off until you turn them on.

## Not yet

- Anything that only X11 has, when the session is Wayland (see the table).
- Secret Service for the saved passwords.
- Updating from the window (a newer version is installed by hand, as described under Updating above), and a package somebody has tried. Bugs and wishes are welcome as [issues on GitHub](https://github.com/inowakowski/home-assistant-desktop-app/issues).

The [changelog](../changelog.md) says in which version each of these arrived.
