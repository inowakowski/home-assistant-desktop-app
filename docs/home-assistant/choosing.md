# Which connection to choose

HADA can talk to Home Assistant in three ways. They do not exclude each other, and none is wrong; they differ in what you need to have and in what you get. This page helps you pick.

*The second and third way are new in 1.4.0.*

## In short

| | **MQTT** | **Direct connection** | **Direct connection with the HADA integration** |
|---|---|---|---|
| What you need | An MQTT broker, for example the Mosquitto app in Home Assistant | Nothing but Home Assistant | The HADA integration, installed in Home Assistant with HACS |
| Sensors (screen, battery, what is playing, …) | Yes | Yes | Yes |
| Buttons, switches and sliders to click in Home Assistant (lock, volume, mute, …) | Yes | **No** | Yes |
| A notification with a text | Yes | Yes | Yes |
| A notification with a picture and buttons | Yes, but written as JSON | Yes, as on a phone | Yes, as on a phone |
| A camera's picture in a notification | No | Yes | Yes |
| Quick actions start automations | Yes | Yes | Yes |
| Works when the computer is away from home | Only if the broker can be reached from outside | Yes, through Home Assistant's outside address | Yes, through Home Assistant's outside address |
| Home Assistant knows at once that the computer went away | Yes | Only when HADA was closed properly | Yes |

All three show the computer in Home Assistant as a device with entities that can be renamed and put in an area.

## What works through which connection

*The second and third column are new in 1.4.0; rows marked \* are not released yet.*

HADA does as much as each way allows. Where a way cannot carry something, that is Home Assistant's doing, not a setting to find: the table says what, and the **Entities** page in HADA says it for the connections you have.

| | **MQTT** | **Direct connection** | **Direct connection with the HADA integration** |
|---|---|---|---|
| Sensors and binary sensors | Entities | Entities | Entities |
| Buttons, switches, the volume slider | Entities | Not in Home Assistant; from an automation, by [firing an event](websocket.md#commands), with an administrator's token | Entities |
| Media player as one entity \* | No: Home Assistant has none over MQTT. The media buttons, the volume and the playback sensor are there | No; the playback sensor is there | Yes |
| Notification: text and title | `notify.send_message` | `notify.mobile_app_…` | `notify.send_message` and `hada.notify` |
| Notification: picture, buttons, tag, address, sticky, silent | Yes, written as JSON in the message | Yes | Yes, with `hada.notify` |
| Notification: a camera's picture | No: it needs signing in at Home Assistant | Yes | Yes |
| Pressed notification button | Topic `…/event/notification_action` | Events `mobile_app_notification_action` and `hada_event` | Event `hada_event` |
| Quick action starts an automation | Trigger of the device, and a topic | Event `hada_event` | `event` entity, and event `hada_event` |
| Entities unavailable when the computer goes away | At once, however it went | Only when HADA was closed properly | At once, however it went |
| Removed or switched-off entities disappear | Yes | They stay, as *unavailable* | Yes |
| Token of an ordinary user is enough | Not applicable: the broker's user | Yes; \* quick actions too | Yes |
| Another address on the home network \* | Not applicable | Yes | Yes |
| Signing in through Home Assistant instead of a token \* | Not applicable: the broker has a user name and a password | Yes | Yes |
| The screen's brightness \*, the volume: shown and set | Yes | Shown; set only by firing an event | Yes |
| Several Home Assistants at once | Yes | Yes | Yes |

The earlier way of the direct connection, **As states**, is left out: it does what the second column does, with sensors that Home Assistant forgets when it restarts, and needs an administrator's token for all of it.

## Which one is for me

**I already have an MQTT broker.** Use MQTT. It needs nothing installed in Home Assistant and gives everything except a camera's picture in a notification. If you want notifications as on a phone as well, [add a direct connection just for them](#mqtt-and-notifications-as-on-a-phone).

**I have no broker and only want to see what the computer is doing.** Use the direct connection. You enter Home Assistant's address and an access token, and the sensors and notifications are there. Nothing to install.

**I have no broker and want to control the computer from Home Assistant.** Use the direct connection with the HADA integration. It takes a few minutes to install with HACS, and then everything works as with MQTT.

**My laptop is often away from home.** Use one of the two direct connections, with the address your Home Assistant has from outside.

## The three ways, one by one

### MQTT

The computer and Home Assistant both talk to a broker, a small program that passes messages on. Many people have one already, for Zigbee2MQTT or other devices.

- Set up on the **Connections** page, under **MQTT**: the broker's address, user name and password.
- The computer shows up in Home Assistant by itself.
- See [MQTT engine](mqtt.md).

### Direct connection

HADA connects to Home Assistant itself, the way the Home Assistant app on a phone does, and shows up under **Settings → Devices & services → Mobile App**.

- Set up on the **Connections** page, under **Home Assistant (WebSocket)**: Home Assistant's address and a long-lived access token. Leave **Notifications** on and **Entities** on *As entities of the device*.
- Notifications are sent with the action `notify.mobile_app_` followed by the computer's name, exactly as to a phone.
- Home Assistant has no buttons or switches for the computer. It can still be controlled from automations, by firing an event; see [Commands](websocket.md#commands).
- See [WebSocket engine](websocket.md).

### Direct connection with the HADA integration

The same connection, but Home Assistant has a part of HADA installed, the [HADA integration](https://github.com/inowakowski/hada-homeassistant), which knows how to show buttons, switches and sliders.

- In Home Assistant: install the integration with HACS and add it once under **Settings → Devices & services**.
- In HADA: on the **Connections** page, under **Home Assistant (WebSocket)**, set **Entities** to *Through the HADA integration*.
- Notifications are sent with the action `hada.notify`.
- See [Sensors through the HADA integration](websocket.md#sensors-through-the-hada-integration).

## Using more than one

### MQTT and notifications as on a phone

Keep MQTT for the sensors and controls, and add a direct connection with **Notifications** on and **Entities** set to *Off*. Home Assistant then gets the entities once, through MQTT, and can also send the computer notifications with pictures from cameras.

### What not to do

Do not send the entities two ways to the same Home Assistant, for example through MQTT and through the HADA integration: everything would be there twice. Whichever way you choose for the entities, set **Entities** to *Off* on the other.

## Changing later

You can change your mind at any time on the **Connections** page. Keep in mind:

- The entities get new names in Home Assistant, because each way names them a little differently. Automations and dashboards that use them have to be pointed at the new ones.
- The old entities stay in Home Assistant as *unavailable* until you delete the old device there.
