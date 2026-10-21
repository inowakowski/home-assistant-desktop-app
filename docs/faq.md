# Frequently asked questions

## Do I need MQTT?

No, but it is the better way. With MQTT the computer appears in Home Assistant as a device, entities have unique IDs and can be renamed, buttons and switches can be pressed from a dashboard, and Home Assistant notices when the computer goes away. The [WebSocket engine](home-assistant/websocket.md) needs no broker and can do less.

## Why are there two programs?

A Windows service cannot see a user's desktop: the focused window, the volume, the media that is playing. A program on the desktop cannot run before anybody signs in, or shut the computer down without asking. So the **service** holds the connection and does what needs no desktop, and the **tray app** adds the rest and hands it to the service. See [Architecture](development/architecture.md).

## Why does changing settings need administrator rights?

The settings decide what a service running with system rights does, including which PowerShell commands it runs. If any signed-in user could change them, any user could run anything as the system. Reading status and logs needs no special rights; the **Settings** page does not either: your personal settings, and (*not released yet*) how updates are looked for, which decides only what HADA asks GitHub.

## Some entities are *unavailable*. Why?

- All of them: the computer is off or asleep, or the service lost its connection.
- The ones from the tray app (window, volume, media, activity and so on): the tray app is not running, nobody is signed in, or another user is at the computer. See [Several users on one computer](features/several-users.md).

This is on purpose: a value from an hour ago should not look current.

## The power buttons are missing in Home Assistant

They are off until you turn them on. See [Controls](features/controls.md#the-power-buttons-are-off-by-default).

## Does HADA send anything anywhere else?

Only to the MQTT broker or the Home Assistant you configured. The one exception is the [update check](getting-started/updating.md): once a day the service asks GitHub whether a newer version exists, which you can turn off on the **Settings** page.

## How much memory does it use?

About 15 MB for the service and 20 to 25 MB for the tray app, measured on Windows 11 ARM64. The previous window takes about 125 MB while it is open, as measured, and nothing once it is closed, because the window is a separate process.

## Windows warns me when I run the installer

The installers are not code-signed yet, so SmartScreen does not know them. Choose **More info → Run anyway**. Each release publishes SHA-256 checksums to compare the download with.

## Can I install it without clicking through the installer?

Yes: `msiexec /i HADA-<version>-x64.msi /qn`. The tray app then starts at the next sign-in. Settings can be put in `appsettings.json`; see [Configuration](reference/configuration.md).

## Can I use it without installing it, or without administrator rights?

Yes, with the [portable version](getting-started/portable.md): a folder to unpack and run. Its service then runs as you instead of as a Windows service, so it does not work while nobody is signed in.

## Where are my settings, and how do I remove everything?

Settings and logs are in `%ProgramData%\HADA`; personal settings under `HKCU\Software\HADA`. Uninstalling leaves both in place, so that updating keeps them. Delete them by hand to forget everything. On Linux and macOS the folders are listed under [Where it keeps things](getting-started/linux-macos.md#where-it-keeps-things).

## Is there a macOS or Linux version?

Yes, as a preview that nobody has tried by hand yet. HADA runs on Linux (GNOME and KDE; as an AppImage, a `.deb` or an `.rpm`) and on macOS 14 or newer, with most of what it does on Windows. The packages were built and checked by machines only, and their file names say `-untested`. See [Linux and macOS](getting-started/linux-macos.md) for what works where.

## Something is wrong

Look at the **Logs** page first, then at [Troubleshooting](reference/troubleshooting.md) and the [known limitations](reference/limitations.md). Bugs and ideas are welcome as [issues on GitHub](https://github.com/inowakowski/home-assistant-desktop-app/issues).
