# Logs

| Where | What |
|---|---|
| **Logs** page of the window | Recent service entries of every level, since the service started |
| `%ProgramData%\HADA\logs\service.log` | The service's entries from Information up. Readable by administrators |
| `%LocalAppData%\HADA\logs\tray.log` | The tray app's entries, including errors it otherwise only shows in a message box |
| `%LocalAppData%\HADA\logs\settings-window.log` | The same for the settings window |
| Windows Event Log, Application, source `HADA.Service` | The service's warnings and errors |

Each file is limited to 2 MB; the three previous files are kept as `service.1.log` and so on.

A sensor that fails is logged and stops, without taking the service or the other sensors with it.
