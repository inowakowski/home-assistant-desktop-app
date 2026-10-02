# Notifications

`notification` shows up in Home Assistant as a notify entity (MQTT engine, Home Assistant 2024.5 or newer). A message sent to it appears as a Windows notification from the HADA tray icon, titled *Home Assistant*:

```yaml
action: notify.send_message
target:
  entity_id: notify.laptop_notification
data:
  message: The washing machine is done.
```

To choose the title, publish JSON to the entity's topic instead:

```yaml
action: mqtt.publish
data:
  topic: hada/laptop/notification/set
  payload: '{"title": "Laundry", "message": "The washing machine is done."}'
```

Windows shows at most 63 characters of a title and 255 of a message; longer ones are cut off. While **Do not disturb** is on, Windows puts the notification in the notification centre without showing it. With the WebSocket engine, see [WebSocket engine](../home-assistant/websocket.md).
