# WebSocket engine

For when there is no MQTT broker. HADA talks to Home Assistant directly, with a long-lived access token of an administrator account. It can do less than the [MQTT engine](mqtt.md); see the limitations below.

> Configure **one** engine. With both configured, every sensor appears in Home Assistant twice.


Sensors appear as `sensor.{device}_{entity}`, e.g. `sensor.desktop_01_cpu_load`, and binary sensors as `binary_sensor.{device}_{entity}`. Commands are sent by firing the command event, for example from a script. `action` is the entity ID; switches and numbers take a `value`, and `notification` a `message` and an optional `title`:

```yaml
- action: event
  event_type: hada_command
  event_data:
    device_id: desktop_01
    action: lock_screen
- action: event
  event_type: hada_command
  event_data:
    device_id: desktop_01
    action: volume_level
    value: 30
- action: event
  event_type: hada_command
  event_data:
    device_id: desktop_01
    action: notification
    title: Laundry
    message: The washing machine is done.
```

`device_id` is required, so a single event never targets every PC at once. A notification also takes `image` and `actions`, as described under [Notifications](../features/notifications.md).

## Events from the computer

When a [quick action](../features/custom-entities.md#quick-actions) is chosen, or a button of a notification is pressed, HADA fires the event `hada_event` in Home Assistant:

```yaml
automation:
  - alias: Toggle the desk lamp from the laptop
    triggers:
      - trigger: event
        event_type: hada_event
        event_data:
          device_id: desktop_01
          name: quick_action
          value: toggle_lamp
    actions:
      - action: light.toggle
        target:
          entity_id: light.desk_lamp
```

`name` is `quick_action` or `notification_action`; `value` is the quick action's ID, or the `action` of the button.

## Limitations

- States are written through the REST API. The entities have no unique ID, so they can't be renamed or managed in the UI.
- There are no button, switch, number or notify entities to press in the UI. A switch shows up as a binary sensor and a number as a sensor, and everything is controlled through the command event.
- Home Assistant forgets these states when it restarts. HADA sends them again once it reconnects.
- On a graceful stop, sensors are set to `unavailable`. After a crash or power loss, the last states remain.
- If Home Assistant rejects the token, the engine stops retrying until its settings change or the service restarts, so repeated failed logins don't get the PC's IP banned.
