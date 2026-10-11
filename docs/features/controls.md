# Controls

What Home Assistant can do to the computer. With the MQTT engine, and with the [HADA integration](../home-assistant/websocket.md#sensors-through-the-hada-integration), these show up as ordinary `button`, `switch`, `number` and `notify` entities of the device, usable from a dashboard or an automation. [What works through which connection](../home-assistant/choosing.md#what-works-through-which-connection) has the whole picture.

| Entity | Type | Provided by | Notes |
|---|---|---|---|
| `lock_screen` | button | Service | Locks the interactive session |
| `sleep`, `hibernate`, `shutdown`, `restart` | button | Service | **Off until you switch them on** on the Entities page. Nothing is forced: apps with unsaved work can still object, as when shutting down from the Start menu |
| `turn_off_display`, `wake_display` | button | Tray | Turns the screen off, or back on as moving the mouse would |
| `media_play_pause`, `media_next`, `media_previous`, `media_stop` | button | Tray | Press the multimedia keys; whichever app handles them reacts |
| `volume_level` | number (0–100 %) | Tray | Sets the volume of the default playback device |
| `audio_mute` | switch | Tray | Mutes the default playback device |
| `microphone_mute` | switch | Tray | Mutes the default microphone in Windows |
| `media_player` | media player | Tray | *Not released yet.* What is playing, play, pause, next, previous, stop, volume and mute in one entity. **Through the HADA integration only**; see [below](#the-media-player) |
| `notification` | notify | Tray | A message sent to it is shown as a Windows notification. See [Notifications](notifications.md) |

## The power buttons are off by default

`sleep`, `hibernate`, `shutdown` and `restart` do not exist in Home Assistant until you switch them on: open the HADA window, choose **Entities**, **Unlock editing**, turn on the ones you want and **Save**. Whoever can press a button in Home Assistant can then switch this computer off, so turn on only what you use.

Nothing is forced. Shutting down and restarting go through Windows the same way the Start menu does: an app with unsaved work can still object and hold it up.

## The media player

*Not released yet.*

Home Assistant shows a speaker or a television as one *media player*: a card with what is playing, the buttons under it and a volume slider. `media_player` makes the computer one of those. It says what Windows' own media controls show, the title, artist, album and app, and how loud the default playback device is; and it takes play, pause, next, previous, stop, the volume and mute.

- **Through the HADA integration only.** Home Assistant has no media player over MQTT, and none for a computer connected without the integration. There the separate entities do the same work: the four media buttons, `volume_level`, `audio_mute`, and the sensor `media_playback`.
- **The separate entities stay**, whichever way you connect. If the media player is all you want in Home Assistant, switch them off on the **Entities** page; if you do not want the media player, switch that off.
- Play and pause go to the app Windows shows in its media controls, so *play* never pauses and *pause* never starts anything, unlike the play/pause key.
- The **Entities** page says so when the media player is switched on and none of your connections can show it.

```yaml
actions:
  - action: media_player.volume_set
    target:
      entity_id: media_player.laptop_media_player
    data:
      volume_level: 0.3
  - action: media_player.media_pause
    target:
      entity_id: media_player.laptop_media_player
```

## Using them

```yaml
actions:
  - action: number.set_value
    target:
      entity_id: number.laptop_volume_level
    data:
      value: 30
  - action: switch.turn_on
    target:
      entity_id: switch.laptop_mute_microphone
  - action: button.press
    target:
      entity_id: button.laptop_media_play_pause
```

Entity IDs start with the device name you set on the **Connections** page; look them up on the device's page in Home Assistant.

Controls of the tray app act on the desktop of whoever is at the computer. They are *unavailable* while the tray app is not running. See [Several users on one computer](several-users.md).

Your own buttons, which run a PowerShell command or start a program, are described under [Custom sensors and buttons](custom-entities.md). With the WebSocket engine there are no entities to press; see [WebSocket engine](../home-assistant/websocket.md).
