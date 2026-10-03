# Portable version

*New in 1.2.0.*

HADA without installing it: unpack a folder and run it. No administrator rights, nothing registered with Windows, and everything it writes stays in that folder. Good for trying HADA out, for a computer where you may not install software, or for carrying it along.

## Get it running

1. Download `HADA-<version>-x64-portable.zip` (Intel and AMD) or `HADA-<version>-arm64-portable.zip` from the [releases page](https://github.com/inowakowski/home-assistant-desktop-app/releases).
2. Before unpacking, open the ZIP file's **Properties** and tick **Unblock**, if that box is there. Otherwise Windows marks every file in it as downloaded from the internet and may warn when you start it.
3. Unpack it wherever you may write, for example your Documents folder. You get one folder, `HADA`.
4. Start **Start HADA.cmd**. The HADA icon appears in the notification area and the window opens.
5. Set up a connection as described under [First setup](first-setup.md). There is no **Unlock editing** here: the settings are yours to change.

To stop it, choose **Exit** in the icon's menu, or start **Stop HADA.cmd**.

## What is in the folder

| | |
|---|---|
| `HADA.portable` | The file that makes the folder a portable copy. Without it the programs behave as if installed |
| `service\\`, `tray\\` | The two programs, the same as in the installer |
| `data\\` | Created on first start: your settings, the logs, your preferences, the dashboard window's sign-in. Copy it to keep or move your configuration |
| `Start HADA.cmd`, `Stop HADA.cmd` | Start and stop |

## How it differs from the installed version

| | Installed | Portable |
|---|---|---|
| The service | A Windows service running as SYSTEM, started with Windows | An ordinary program running as you, started and stopped by the tray app |
| With nobody signed in | Keeps reporting | Not running |
| Changing settings | Needs an administrator | Needs nobody's permission |
| Custom PowerShell sensors and buttons | Run as SYSTEM, without a desktop | Run as you |
| Start with Windows | On, for every user | Off until you turn it on, on the **Settings** page; then for you only |
| Updating | **Download and install** in the window | Replace the `service` and `tray` folders |
| Saved passwords | Readable by the service on this computer | Readable by your account on this computer |
| Several users | The one at the computer is reported | Each user would run their own copy |

Sleep, hibernate, shut down, restart, lock and everything on the desktop work as in the installed version, with your account's rights.

## Updating

1. Exit HADA.
2. Unpack the new ZIP somewhere else.
3. Replace the `service` and `tray` folders with the new ones. Leave `data` alone.
4. Start it again.

The window says when a newer version exists, if the update check is on; it cannot replace its own folders.

## Moving and removing

Exit HADA first. To move it, move the folder; your settings travel in `data`. Saved passwords and tokens do not: they are encrypted for your account on this computer, and have to be entered again on another one.

To remove it, delete the folder. Two things are outside it, both in your own part of the registry:

- If you turned **Start with Windows** on, turn it off before moving or deleting the folder. It is an entry in your startup programs that points at the folder.
- Windows needs to be told who shows HADA's notifications. That is one small key, `HKCU\\Software\\Classes\\AppUserModelId\\HADA.Tray.P…`, which you can delete.

## Next to an installed HADA, or another copy

A portable copy shares nothing with an installed HADA or with another portable copy in a different folder: each has its own settings, its own pipe between its programs and its own tray icon. They can run side by side. What they must not share is the **Device ID** on the Connections page: two HADAs reporting to the same Home Assistant under one ID throw each other off the broker.

## From the command line

| Command | Effect |
|---|---|
| `tray\\HADA.Tray.exe` | Start, and open the window |
| `tray\\HADA.Tray.exe --background` | Start without opening the window |
| `tray\\HADA.Tray.exe --exit` | Tell the running copy to exit, which also stops its service |
