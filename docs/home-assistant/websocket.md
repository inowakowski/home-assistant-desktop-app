# WebSocket engine

For when there is no MQTT broker, and for [notifications as on a phone](../features/notifications.md#notifications-as-on-a-phone). HADA talks to Home Assistant directly, with a long-lived access token. For controls it can do less than the [MQTT engine](mqtt.md); see the limitations below.

Over the internet, give Home Assistant's `https://` address. An `http://` address is fine on your own network; to a public address HADA refuses it, as the access token could be read on the way. See [Security](../reference/security.md#the-connection-to-home-assistant).

> Send the sensors through **one** engine. With MQTT and this connection both sending them, every sensor appears in Home Assistant twice. To use both, set **Entities** to **Off** here. [Which connection to choose](choosing.md) compares the ways side by side.

## What the connection is used for

*New in 1.4.0.*

Each Home Assistant on the **Connections** page has two settings.

**Notifications**, on or off. On, the computer registers with Home Assistant's `mobile_app` integration, which adds the action `notify.mobile_app_` followed by the device name; see [Notifications](../features/notifications.md#notifications-as-on-a-phone). Off, notifications arrive as `hada_command` events only.

**Entities**, one of:

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

*New in 1.4.0.*

The [HADA integration](https://github.com/inowakowski/hada-homeassistant) is a part of HADA that runs inside Home Assistant. Install it there with HACS and add it once under **Settings → Devices & services**; its page says how. Computers then show up by themselves.

Compared with entities of the `mobile_app` device:

- **Buttons, switches and numbers are real ones**, to press and set in Home Assistant as with MQTT: lock the screen, set the volume, mute. An action there waits for the computer to take the command, and fails if it does not within ten seconds. A switch and a number show what the computer reports, not what they were just told.
- An entity the computer no longer has, or that is switched off on HADA's **Entities** page, is removed from Home Assistant instead of staying there as *unavailable*.
- When the computer goes away, however it does, its entities are *unavailable* at once, not only when HADA was stopped properly.
- **Notifications** have an action of their own, `hada.notify`, aimed at the computer's notify entity, which takes all that `notify.mobile_app_…` does; see [Notifications](../features/notifications.md#through-the-hada-integration). The **Notifications** switch, which is about the `mobile_app` integration, can stay off.
- **Quick actions** are `event` entities of the device. What happens on the computer fires `hada_event` as [below](#events-from-the-computer), with the token of any user: it is the integration that fires it.
- The computer is a device of its own, apart from the one **Notifications** makes under Mobile App.

While Home Assistant does not have the integration, or it was not added there, the connection's card on the **Overview** page says so, and HADA looks for it again every minute; nothing else about the connection is affected. An integration older than HADA is used for what it knows, and the card says that it wants updating, in Home Assistant with HACS (*new in 1.5.0*).

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

## Signing in instead of pasting a token

*New in 1.5.0.*

A long-lived access token has to be made in Home Assistant, copied and pasted. The other way is to sign in, as Home Assistant's own apps do:

1. On the **Connections** page, enter the address of Home Assistant and choose **Sign in through Home Assistant**.
2. Your browser opens Home Assistant's own sign-in page. Sign in, if you are not signed in there already, and allow the access.
3. The browser says *Signed in*; close the tab, go back to HADA and **Save**.

What HADA keeps is not your password, which it never sees, but a *refresh token*: Home Assistant exchanges it for access tokens that last half an hour each, and HADA asks for the next one in time. It is saved encrypted, as a pasted token is.

- **Withdrawing it.** In Home Assistant, open your profile, **Security**, and look under **Refresh tokens** for the one whose name starts with `http://127.0.0.1`: that is HADA on this computer. Delete it, and HADA is out; the **Overview** page says so, and nothing is tried again until you sign in anew.
- **Left unused.** Home Assistant removes a sign-in that was not used for a long time, 90 days when this was written. A computer that is switched on now and then keeps it alive.
- **Whose it is.** The sign-in is that of the Home Assistant user who signed in, as a token is that of the user who made it. An ordinary user is enough for notifications and for the HADA integration.
- **A token still works.** Paste one into the box instead, and it is used as before; the two ways are alternatives for each Home Assistant.

The browser comes back to HADA at an address on this computer itself, `127.0.0.1`, which no other computer can reach, and only the browser that was given the page can finish the sign-in. Opened from the administrator copy of the window, the browser still runs as you.

## Another address on your home network

*New in 1.5.0.*

A Home Assistant often has two addresses: one that works from anywhere, such as `https://home.example.org`, and one that only works at home, such as `http://192.168.1.20:8123`. At home the second is the shorter way: nothing goes out to the internet and back through a proxy or a tunnel, and it keeps working when the internet connection is down.

On the **Connections** page, open **Another address on your home network** for the Home Assistant and give:

- **Address on the home network:** the address Home Assistant has there.
- **Home networks:** the networks on which that address is used, one per line, by the names Windows knows them by. The networks the computer is connected to right now are listed below the box; click one to add it. A Wi-Fi network goes by its own name; a wired one, or a VPN, by the name Windows gave it, such as *Network 2*.

HADA then connects like this:

- On one of the home networks, to the local address. When the computer's networks change, it changes over by itself.
- Anywhere else, to the main address. The local address is not tried at all there: on a network that is not yours it would be somebody else's address, and whatever answers at it would be handed the access token.
- When the local address does not answer although the computer is at home, to the main address, and to the local one again the next time it connects.

Both are addresses of the same Home Assistant: the computer stays one device there, whichever way it comes. **Test connection** tries both and says which is which. The [dashboard window](../window.md#the-dashboard-window) shows the main address unless you give it another.

A network is told by its name, which is the best Windows has to offer: someone who sets up a Wi-Fi network with your network's name, and the same password, can make the computer take it for home. An `http://` address is safe at home only as far as the home network itself is.

## Several Home Assistants at once

*New in 1.4.0.*

As with [MQTT servers](mqtt.md#several-home-assistants-at-once), the computer can be connected directly to more than one Home Assistant at the same time. On the **Connections** page, under **Home Assistant (WebSocket)**:

1. Choose **Add a server**, and give each server a name, such as the place it is in. The name is yours: it is shown on the server's tab, on its card on the **Overview** page and in the log.
2. Fill in the address and the access token of that Home Assistant, and choose **Test connection**. The test tries the server whose tab is open.
3. **Save**.

- Each server has its own access token, Device ID and name. Two servers at the same address need different Device IDs.
- A command event is acted on only by the connection it arrived through, and the button of a notification answers only the Home Assistant that sent it.
- Entities switched off on the **Entities** page are off for every server.
- The Home Assistant set up before this was possible stays as it was; it is simply the first server.

## Events from the computer

When a [quick action](../features/custom-entities.md#quick-actions) is chosen, or a button of a notification is pressed, HADA fires the event `hada_event` in Home Assistant. Firing an event is an administrator's to do there; with the token of an ordinary user the event goes through the HADA integration, or through the `mobile_app` registration that **Notifications** or **As entities of the device** make (*through the registration is new in 1.5.0*). A connection that has none of these needs an administrator's token for it.

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
