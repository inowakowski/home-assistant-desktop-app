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

*Pictures and buttons are new in 1.1.0; 1.0.0 shows the title and the message.*

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

*New in 1.4.0:* the same JSON also takes what is described under [More than a text](#more-than-a-text): `tag`, `url`, `sticky`, `silent`, and `uri` on a button.

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

With [several Home Assistants](../home-assistant/mqtt.md#several-home-assistants-at-once), a press is reported only to the one that sent the notification. For a button that opens a door, see [Who pressed it](#who-pressed-it).

A press is reported only while the tray app that showed the notification is still running. A notification found in the notification centre after the computer was restarted can no longer report its buttons.

## Notifications as on a phone

*New in 1.4.0.*

`notify.send_message` takes a text and nothing else. For the same notifications a phone with the companion app gets, connect the computer to Home Assistant directly and switch **Notifications** on, on the **Connections** page under **Home Assistant (WebSocket)**. This works next to MQTT: set **Entities** to **Off** there, and the connection is used for notifications only.

The computer then registers with Home Assistant's `mobile_app` integration, and Home Assistant has the action `notify.mobile_app_` followed by the device name:

```yaml
action: notify.mobile_app_laptop
data:
  title: Front door
  message: Someone is at the door.
  data:
    image: /local/door.jpg
    actions:
      - action: open_door
        title: Open
      - action: ignore
        title: Ignore
```

- `title`, `image` and `actions` are as above. An `image` that is only a path is fetched from the Home Assistant that sent the notification, and may be one that needs signing in there, such as a camera's: `/api/camera_proxy/camera.front_door`.
- A pressed button fires the event `mobile_app_notification_action`, with the button's `action` and the computer's `device_id`: the event automations and blueprints written for phones wait for.

```yaml
automation:
  - alias: Open the door from the notification
    triggers:
      - trigger: event
        event_type: mobile_app_notification_action
        event_data:
          action: open_door
          device_id: laptop
    conditions:
      # Who pressed it; see "Who pressed it" below.
      - condition: template
        value_template: "{{ trigger.event.context.user_id == '0123456789abcdef0123456789abcdef' }}"
    actions:
      - action: lock.unlock
        target:
          entity_id: lock.front_door
```

- While the computer is off or not connected, the action fails with an error saying the device is not connected; Home Assistant does not keep the notification for later.
- The access token need not be an administrator's for this. `hada_command` events need one.
- Home Assistant shows the computer under **Settings → Devices & services → Mobile App**, and makes a `device_tracker` entity for it, which HADA does not update. Deleting the device there is undone the next time HADA connects, while **Notifications** is on.
- What a phone understands beyond this page (channels, colours, vibration, text input and the like) is ignored.

### Through the HADA integration

*New in 1.4.0.*

With the [HADA integration](../home-assistant/websocket.md#sensors-through-the-hada-integration) installed in Home Assistant and chosen for the connection, the same notifications are sent with `hada.notify`, to the notify entity of the computer; nothing is registered with `mobile_app`:

```yaml
action: hada.notify
target:
  entity_id: notify.laptop_notification
data:
  title: Front door
  message: Someone is at the door.
  data:
    image: /api/camera_proxy/camera.front_door
    actions:
      - action: open_door
        title: Open
```

A pressed button fires `hada_event`, with `name: notification_action`, the button's `action` as `value` and the computer's Device ID as `device_id`:

```yaml
triggers:
  - trigger: event
    event_type: hada_event
    event_data:
      device_id: laptop
      name: notification_action
      value: open_door
```

## Who pressed it

A button that does something that matters, such as opening a door, should run only for the computer it was meant for:

- **Name the computer.** Any user of Home Assistant may connect a computer of their own, and its buttons send the same `action`s. In an automation on `hada_event` or `mobile_app_notification_action`, put the Device ID in `device_id`; through MQTT, it is part of the topic.
- **Check the user, too.** Home Assistant lets the companion app of any of its users fire any event with any data, `device_id` included. What it cannot make up is the user the event comes from: for a computer, the user whose token it connects with. A condition on `trigger.event.context.user_id`, as in the example above, keeps the door shut for everybody else. The id is shown under **Settings → People → Users** with advanced mode on. With the [HADA integration](../home-assistant/websocket.md) a computer fires `hada_event` only under its own Device ID, but a companion app can still fire one.
- **Through MQTT**, whoever may publish to the broker can send a press. Give HADA a broker user of its own, and let only that user write to `hada/…`.

`notify.send_message` works with that entity too, for a text and a title.

## More than a text

*New in 1.4.0.*

Inside `data` of a `notify.mobile_app_…` or `hada.notify` action, or beside `message` in the JSON published through MQTT:

| | |
|---|---|
| `tag: laundry` | The notification replaces the one shown earlier with the same tag, instead of adding to it |
| `message: clear_notification` with a `tag` | Shows nothing: takes the notification with that tag back, from the screen and from the notification centre |
| `url: https://…`, or `clickAction:` | Pressing the notification opens that address in the browser. A path, such as `/lovelace/cameras`, is one at the Home Assistant that sent the notification (not through MQTT, where HADA does not know its address) |
| `uri: https://…` on a button | The button opens that address, and is not reported as pressed |
| `sticky: true`, or `persistent: true` | The notification stays on the screen until it is dismissed |
| `silent: true`, `importance: low` or `push: {sound: none}` | No sound |

```yaml
- action: notify.mobile_app_laptop
  data:
    title: Washing machine
    message: Done. Hang it up!
    data:
      tag: laundry
      sticky: true
      url: /lovelace/laundry
# … and once the door of the machine was opened:
- action: notify.mobile_app_laptop
  data:
    message: clear_notification
    data:
      tag: laundry
```

- Only `http` and `https` addresses are opened; anything else is left out.
- Addresses open in your default browser. To have them open in another one, choose it under **Browser for notifications** on the **Settings** page; each user chooses their own. With a browser chosen there, a notification found in the notification centre after the tray app was restarted no longer opens its address, as with its buttons.
- Two Home Assistants may use the same tag: each replaces and takes back only its own notifications.

## Good to know

While **Do not disturb** is on, Windows puts the notification in the notification centre without showing it. With the WebSocket engine, see [WebSocket engine](../home-assistant/websocket.md).
