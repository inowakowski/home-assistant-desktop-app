# WebSocket engine

For when there is no MQTT broker, and for [notifications as on a phone](../features/notifications.md#notifications-as-on-a-phone). HADA talks to Home Assistant directly, with a long-lived access token. For controls it can do less than the [MQTT engine](mqtt.md); see the limitations below.

> Send the sensors through **one** engine. With MQTT and this connection both sending them, every sensor appears in Home Assistant twice. To use both, set **Sensors** to **Off** here.

## What the connection is used for

*Not released yet.*

Each Home Assistant on the **Connections** page has two settings.

**Notifications**, on or off. On, the computer registers with Home Assistant's `mobile_app` integration, which adds the action `notify.mobile_app_` followed by the device name; see [Notifications](../features/notifications.md#notifications-as-on-a-phone). Off, notifications arrive as `hada_command` events only.

**Sensors**, one of:

| | What Home Assistant gets | Token |
|---|---|---|
| **As entities of the device** (recommended) | The computer is a device of the `mobile_app` integration, as with notifications, and its sensors are entities of that device: with a unique ID, so they can be renamed, put in an area and switched off in Home Assistant, and kept when it restarts | Any user's |
| **Through the HADA integration** | The computer is a device of the [HADA integration](https://github.com/inowakowski/hada-homeassistant), which has to be installed in Home Assistant. See [below](#sensors-through-the-hada-integration) | Any user's |
| **As states** (the earlier way) | States written through the REST API, described under [Limitations](#limitations) | An administrator's |
| **Off** | Nothing. For when the same Home Assistant gets the sensors through MQTT | |

A server added in the window starts with **Notifications** on, and with sensors as entities, or off if an MQTT server is set up. A connection set up before there was a choice keeps doing what it did: sensors as states, and no registration.

**Test connection** says when the token is not an administrator's, and what will then not work: sensors as states, and command events.

### Sensors as entities of the device

- Home Assistant names the entities after the device name and the sensor: `sensor.laptop_cpu_load` for the device name `Laptop`. Unlike with states, the Device ID is not part of the name.
- A switch shows up as a binary sensor and a number as a sensor, as with states; they are controlled through the command event.
- A sensor switched off on HADA's **Entities** page, one whose source is away, and every sensor while HADA is stopped is shown as *unavailable*. HADA cannot delete an entity from Home Assistant; switch off there what you do not want to see.
- Changing from states to entities changes the entity IDs, so automations using them need the new ones. The states are removed when HADA connects, if the token is an administrator's, and are gone in any case once Home Assistant restarts.
- Deleting the device in Home Assistant is undone the next time HADA connects, with new entities.

### Sensors through the HADA integration

*Not released yet, and the integration is an early version: sensors and binary sensors only.*

The [HADA integration](https://github.com/inowakowski/hada-homeassistant) is a part of HADA that runs inside Home Assistant. Install it there with HACS and add it once under **Settings → Devices & services**; its page says how. Computers then show up by themselves.

Compared with entities of the `mobile_app` device:

- An entity the computer no longer has, or that is switched off on HADA's **Entities** page, is removed from Home Assistant instead of staying there as *unavailable*.
- When the computer goes away, however it does, its entities are *unavailable* at once, not only when HADA was stopped properly.
- The computer is a device of its own, apart from the one **Notifications** makes under Mobile App.

While Home Assistant does not have the integration, or it was not added there, the connection's card on the **Overview** page says so, and HADA looks for it again every minute; nothing else about the connection is affected.

## Commands


With sensors as states, they appear as `sensor.{device}_{entity}`, e.g. `sensor.desktop_01_cpu_load`, and binary sensors as `binary_sensor.{device}_{entity}`, where `{device}` is the Device ID.

Commands are sent by firing the command event, for example from a script. `action` is the entity ID; switches and numbers take a `value`, and `notification` a `message` and an optional `title`:

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

`device_id` is required, so a single event never targets every PC at once. A notification also takes `image`, `actions` and [more](../features/notifications.md#more-than-a-text), as described under [Notifications](../features/notifications.md).

## Several Home Assistants at once

*Not released yet.*

As with [MQTT servers](mqtt.md#several-home-assistants-at-once), the computer can be connected directly to more than one Home Assistant at the same time. On the **Connections** page, under **Home Assistant (WebSocket)**:

1. Choose **Add a server**, and give each server a name, such as the place it is in. The name is yours: it is shown on the server's tab, on its card on the **Overview** page and in the log.
2. Fill in the address and the access token of that Home Assistant, and choose **Test connection**. The test tries the server whose tab is open.
3. **Save**.

- Each server has its own access token, Device ID and name. Two servers at the same address need different Device IDs.
- A command event is acted on only by the connection it arrived through, and the button of a notification answers only the Home Assistant that sent it.
- Entities switched off on the **Entities** page are off for every server.
- The Home Assistant set up before this was possible stays as it was; it is simply the first server.

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

- There are no button, switch or number entities to press in the UI. A switch shows up as a binary sensor and a number as a sensor, and everything is controlled through the command event.
- On a graceful stop, sensors are set to `unavailable`. After a crash or power loss, the last states remain.

With sensors **as states**, also:

- States are written through the REST API. The entities have no unique ID, so they can't be renamed or managed in the UI.
- Home Assistant forgets these states when it restarts. HADA sends them again once it reconnects.
- If Home Assistant rejects the token, the engine stops retrying until its settings change or the service restarts, so repeated failed logins don't get the PC's IP banned.
