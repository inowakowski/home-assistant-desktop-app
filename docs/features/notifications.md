# Notifications

`notification` shows up in Home Assistant as a notify entity (MQTT engine, Home Assistant 2024.5 or newer). A message sent to it appears as a Windows notification from the HADA tray icon, titled *Home Assistant*:

```yaml
action: notify.send_message
target:
  entity_id: notify.laptop_notification
data:
  message: The washing machine is done.
```

## Title, picture and buttons

*Pictures and buttons are new in 1.1.0, a pre-release; 1.0.0 shows the title and the message.*

For more than a plain text, publish JSON to the entity's topic instead:

```yaml
action: mqtt.publish
data:
  topic: hada/laptop/notification/set
  payload: >-
    {
      "title": "Front door",
      "message": "Someone is at the door.",
      "image": "http://homeassistant.local:8123/local/door.jpg",
      "actions": [
        { "action": "open_door", "title": "Open" },
        { "action": "ignore", "title": "Ignore" }
      ]
    }
```

- `title` replaces *Home Assistant* as the heading.
- `image` is an `http` or `https` address of a PNG, JPEG, GIF or BMP of at most 5 MB. The computer fetches it, so it must be reachable from there without signing in. A picture that cannot be fetched is left out; the notification still shows.
- `actions` become buttons, five at most. `action` is an ID of your choice (letters, digits, `_`, `-`, `.`), `title` what the button says.

When a button is pressed, HADA publishes its `action` to `hada/{device}/event/notification_action`:

```yaml
automation:
  - alias: Open the door from the notification
    triggers:
      - trigger: mqtt
        topic: hada/laptop/event/notification_action
        payload: open_door
    actions:
      - action: lock.unlock
        target:
          entity_id: lock.front_door
```

A press is reported only while the tray app that showed the notification is still running. A notification found in the notification centre after the computer was restarted can no longer report its buttons.

## Good to know

While **Do not disturb** is on, Windows puts the notification in the notification centre without showing it. With the WebSocket engine, see [WebSocket engine](../home-assistant/websocket.md).
