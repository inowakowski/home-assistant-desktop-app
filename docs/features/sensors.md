# Sensors

What HADA tells Home Assistant about the computer. Every sensor can be turned off on the **Entities** page of the HADA window; a sensor that is off is removed from Home Assistant.

**Provided by** says which part of HADA reads the value. The service runs whether or not anybody is signed in. The tray app runs in the signed-in user's session, so its sensors are *unavailable* while nobody is signed in, or while the tray app is not running.

| Entity | Type | Provided by | Notes |
|---|---|---|---|
| `cpu_load` | sensor (%) | Service | System-wide CPU load, updated every 10 s |
| `memory_usage` | sensor (%) | Service | Physical memory in use |
| `gpu_load` | sensor (%) | Service | How busy the graphics processor is, as Task Manager shows it. Not on computers whose graphics driver does not report it |
| `disk_c_usage`, `disk_d_usage`, … | sensor (%) | Service | One per built-in drive: how full it is. `free_gb` and `total_gb` are attributes |
| `ip_address` | sensor | Service | The IPv4 address of the connection Windows routes through. `interface`, `connection_type` (`ethernet`, `wifi`, `other`) and `mac_address`, which Wake-on-LAN needs, are attributes |
| `wifi_network` | sensor | Service | Only on computers with Wi-Fi. The name of the connected network, `not_connected` otherwise. `signal` (%) is an attribute |
| `display_on` | binary sensor | Service | On while the screen is on. `display_state` (`on`, `dimmed`, `off`) is an attribute. Reported the moment it changes |
| `session_locked` | binary sensor | Service | On while Windows is locked or showing the sign-in screen |
| `active_user` | sensor | Service | The account name of whoever is signed in at the computer's own screen; `none` on the sign-in screen |
| `last_boot` | sensor (timestamp) | Service | When Windows was started |
| `battery_level` | sensor (%) | Service | Only on computers with a battery |
| `battery_charging` | binary sensor | Service | Only on computers with a battery |
| `plugged_in` | binary sensor | Service | Only on computers with a battery |
| `lid_open` | binary sensor | Service | Only on computers with a battery. Reported the moment it changes |
| `update_available` | binary sensor | Service | On when a newer HADA was released; the Overview page then offers to download and install it. `installed_version`, `latest_version` and `release_url` are attributes. See [Updates](../getting-started/updating.md) |
| `active_window` | sensor | Tray | Title of the focused window. The process name is an attribute |
| `user_active` | binary sensor | Tray | On when the keyboard or mouse was used in the last 60 seconds. Each user can change that time on the **Settings** page |
| `audio_volume` | sensor (%) | Tray | Default playback device volume. `muted` is an attribute |
| `audio_device` | sensor | Tray | Name of the default playback device, e.g. to tell headphones from speakers. The default microphone is the `microphone` attribute |
| `media_playback` | sensor | Tray | `playing`, `paused`, `stopped` or `idle`: what Windows' own media controls show. `title`, `artist`, `album` and `app` are attributes |
| `microphone_in_use` | binary sensor | Tray | On while an app uses the microphone. The apps are listed in the `apps` attribute |
| `microphone_muted` | binary sensor | Tray | On while the default microphone is muted in Windows. `level` (input level, %) is an attribute. Muting only inside a call app is not seen |
| `camera_in_use` | binary sensor | Tray | On while an app uses the camera. The apps are listed in the `apps` attribute |
| `do_not_disturb` | binary sensor | Tray | On while Windows holds notifications back. `mode` (`off`, `priority_only`, `alarms_only`) is an attribute |
| `external_display` | binary sensor | Tray | On while a monitor other than the built-in one is connected. `displays` and `external_displays` (counts) are attributes. A monitor without power is not seen |

Need something that is not in the list? Add it without code as a [custom sensor](custom-entities.md), or [in code](../development/adding-entities.md).

## Privacy

> **Privacy:** `active_window` sends window titles to Home Assistant. Titles can contain document names, e-mail subjects or web page titles. `media_playback` sends what you are listening to or watching, `microphone_in_use` and `camera_in_use` the names of the apps using them, `active_user` your account name, and `wifi_network` the name of your network.

Turn off what you do not want to share. Nothing leaves the computer except to the MQTT broker or the Home Assistant you configured.
