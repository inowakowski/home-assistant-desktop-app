# MQTT engine

The recommended way to connect. HADA announces itself through MQTT discovery, so the computer appears in Home Assistant as a device with all its entities, each with a unique ID, and Home Assistant knows when the computer goes away.


Entities show up automatically under **Settings → Devices & services → MQTT** as a device named after the PC. Buttons, the volume slider and the mute switches can be used from the dashboard or in automations like any other `button`, `number` or `switch` entity:

```yaml
actions:
  - action: number.set_value
    target:
      entity_id: number.laptop_volume_level
    data:
      value: 30
  - action: button.press
    target:
      entity_id: button.laptop_media_play_pause
```

If the service stops or loses its connection, all of the device's entities turn *unavailable*. The tray app's sensors also turn *unavailable* while the tray app is not running, for example when nobody is signed in, so an automation never acts on a value from an hour ago.

## Topics

With `{device}` being the Device ID:

| Topic | Content |
|---|---|
| `homeassistant/{sensor\|binary_sensor\|button\|switch\|number\|notify}/{device}/{entity}/config` | Discovery config (retained; emptied when the entity is disabled or removed) |
| `hada/{device}/availability` | `online` / `offline` (retained, last will) |
| `hada/{device}/{entity}/availability` | `online` / `offline` (retained). `offline` while the entity's source is away, e.g. the tray app's sensors after sign-out |
| `hada/{device}/{entity}/state` | State (retained). Binary sensors and switches report `on` / `off` |
| `hada/{device}/{entity}/attributes` | Sensor attributes as JSON (retained) |
| `homeassistant/device_automation/{device}/{entity}/config` | Discovery config of a [quick action](../features/custom-entities.md#quick-actions) as a trigger of the device (retained; emptied when it is removed) |
| `hada/{device}/event/quick_action` | A quick action was chosen; the payload is its ID. Not retained |
| `hada/{device}/event/notification_action` | A button of a [notification](../features/notifications.md) was pressed; the payload is its `action`. Not retained |
| `hada/{device}/{entity}/set` | Command: `PRESS` for a button, `on` / `off` for a switch, the value for a number, the message (or JSON with `message` and optionally `title`, `image` and `actions`) for `notification`. Anything else is ignored, as are retained commands |
