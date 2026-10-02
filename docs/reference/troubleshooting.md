# Troubleshooting

Start with the **Logs** page, or `%ProgramData%\HADA\logs\service.log`.

| What you see | Likely cause and what to do |
|---|---|
| The log repeats *Disconnected from MQTT broker* every few seconds, or says the connection *keeps dropping right after it is made* | Two HADA services are connected with the same **Device ID**. A broker allows one connection per ID and drops the other. Give every computer its own Device ID. A second copy of the service on the same computer cannot be the cause from 0.4.0 on: one started from a console refuses to start while another is running |
| The window says the service is not running although it is | Another copy of the service held the `HADA.Session` pipe when this one started. The service takes the pipe over within seconds of the other copy exiting; the log says when |
| The device is in Home Assistant, but entities are missing | Each time it connects, the service logs *Announced N entities to Home Assistant*. If N is what you expect, the reason is on the Home Assistant side: look under **Settings → System → Logs** for `mqtt` entries. If N is too low, the entities are switched off on the **Entities** page or the tray app is not connected |
| Sensors show *unknown* right after they appear | Should not happen from 0.2.0 on, where states are retained. With an older version, wait for the value to change |
| A button does nothing | The **Logs** page says why: a tray action was pressed while the tray app is not running in the session in use, or the action itself failed. The power buttons do not exist in Home Assistant until they are switched on on the **Entities** page |
| A notification does not appear | Windows is holding notifications back (**Do not disturb**, see `do_not_disturb`), or notifications are turned off for HADA under **Settings → System → Notifications**. It is then only in the notification centre |
| `wifi_network` says `not_connected` although Wi-Fi is connected | Windows 11 24H2 and newer hide the network name unless location access is allowed: **Settings → Privacy & security → Location** |
| Every sensor appears twice | Both MQTT and the WebSocket engine are configured. Clear the Home Assistant URL, or the broker host |
