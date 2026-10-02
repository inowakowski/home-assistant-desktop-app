# Configuration

Settings saved in the window are stored in `%ProgramData%\HADA\settings.json`:

- The folder is accessible only to SYSTEM, administrators and the account running the service.
- Passwords and tokens in the file are additionally encrypted with Windows DPAPI.
- Each section saved from the window (MQTT, Home Assistant, entities, custom sensors and buttons, updates) replaces the same section of `appsettings.json`.
- Your personal settings (start with Windows, language, dashboard address, activity threshold) are not in that file but under `HKCU\Software\HADA`, one set per user.

You can also configure the service without the window, through `appsettings.json` next to `HADA.Service.exe`:

```json
{
  "Mqtt": {
    "Host": "homeassistant.local",
    "Port": 1883,
    "UseTls": false,
    "Username": "hada",
    "DeviceId": "",
    "DeviceName": "",
    "DiscoveryPrefix": "homeassistant",
    "BaseTopic": "hada"
  },
  "HomeAssistant": {
    "BaseUrl": "http://homeassistant.local:8123",
    "DeviceId": "",
    "DeviceName": "",
    "CommandEventType": "hada_command"
  },
  "Entities": {
    "Disabled": [ "active_window" ],
    "Enabled": [ "shutdown" ]
  },
  "CustomSensors": {
    "Items": [
      { "Name": "Game running", "Type": "ProcessRunning", "Value": "game", "IntervalSeconds": 5 }
    ]
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `Mqtt:Host` | *(empty = MQTT engine off)* | Broker host name or IP. The window also accepts pasted addresses such as `mqtt://broker:1883` and splits them into host, port and TLS |
| `Mqtt:Port` | `1883` | Usually `8883` with TLS |
| `Mqtt:UseTls` | `false` | Use TLS for the broker connection |
| `Mqtt:Username` / `Mqtt:Password` | – | Broker credentials. See [Secrets](#secrets) |
| `Mqtt:DiscoveryPrefix` | `homeassistant` | Must match the MQTT integration's discovery prefix |
| `Mqtt:BaseTopic` | `hada` | Root of HADA's state, attributes, command and availability topics |
| `HomeAssistant:BaseUrl` | *(empty = WebSocket engine off)* | Home Assistant URL, `http` or `https` |
| `HomeAssistant:AccessToken` | – | Long-lived token of an administrator. See [Secrets](#secrets) |
| `HomeAssistant:CommandEventType` | `hada_command` | Event type the WebSocket engine listens to for commands |
| `HomeAssistant:DeviceEventType` | `hada_event` | Event type the WebSocket engine fires for quick actions and notification buttons |
| `*:DeviceId` | machine name | Used in topics and entity IDs. Lowercased, and anything other than letters and digits becomes `_` (`DESKTOP-01` → `desktop_01`) |
| `*:DeviceName` | machine name | Device and friendly-name prefix shown in Home Assistant |
| `Entities:Disabled` | *(none)* | Entity IDs not shared with Home Assistant |
| `Entities:Enabled` | *(none)* | IDs of the entities that are off unless listed here: `sleep`, `hibernate`, `shutdown`, `restart` |
| `Updates:CheckAutomatically` | `true` | Whether the service asks GitHub for a newer version once a day |
| `Updates:IncludePrereleases` | `false` | Whether versions marked as pre-release count as newer versions |
| `CustomSensors:Items` | *(none)* | [Custom sensors and buttons](../features/custom-entities.md): `Name`, `Type` (`Text`, `ProcessRunning`, `PowerShell`, `DeviceConnected`, `CommandButton`, `LaunchButton`, `KeysButton` or `QuickAction`), `Value`, and optionally `Id`, `Unit` and `IntervalSeconds` |

> Configure **one** engine. With both configured, every sensor appears in Home Assistant twice.

## Secrets

The settings window is the simplest way to set `Mqtt:Password` and `HomeAssistant:AccessToken`; they are then stored encrypted. When configuring through files, keep them out of `appsettings.json`.

**Development** (`dotnet run` uses the `Development` environment, which loads user secrets):

```powershell
dotnet user-secrets set "Mqtt:Password" "<password>" --project src/HADA.Service
dotnet user-secrets set "HomeAssistant:AccessToken" "<token>" --project src/HADA.Service
```

**Installed service without the window:** put the secrets in `appsettings.Production.json` next to `HADA.Service.exe`, then restrict that file to SYSTEM and Administrators:

```json
{
  "Mqtt": { "Password": "<password>" },
  "HomeAssistant": { "AccessToken": "<token>" }
}
```

```powershell
icacls "C:\Program Files\HADA\service\appsettings.Production.json" /inheritance:r /grant:r "*S-1-5-18:R" "*S-1-5-32-544:F"
```

Avoid service-level environment variables in the registry, because local users can read them.
