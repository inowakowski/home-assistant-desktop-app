# Security

## Passwords and tokens

The MQTT passwords and the Home Assistant token are needed by the service whenever it connects, without anyone there to type them. So they have to be stored in a form the computer can turn back into the password; what can be done is to keep that to as few as possible:

- They are in `%ProgramData%\HADA\settings.json`, encrypted with Windows DPAPI for this computer. A copy of the file, from a backup for example, is useless on any other computer.
- The folder is open only to SYSTEM and the Administrators group (and the service's account). Ordinary users of the computer cannot read the file, and cannot change what it says.
- The folder must also *belong* to them, because the owner of a folder can always change who may open it. Any user may create folders in `ProgramData`, so someone could have made `HADA` there before HADA was installed. The service therefore checks the owner before it reads anything from the folder: one that belongs to anyone other than the system, the Administrators group or one of its members is moved aside, renamed `HADA.untrusted-<date>`, and HADA starts without settings and says so in the log. A folder it trusts is given to the Administrators group, so that no single account keeps an owner's rights to it.
- The window never receives a saved password or token, only whether one is saved. An empty password field keeps the saved one. Passwords are never written to the log.
- A test with a saved password, or saving settings, needs an elevated administrator, since either may send the password to an address of one's choosing.

What this does not protect against, and cannot:

- **An administrator of the computer**, or a program running as one, can read the passwords: such a program can do whatever the service can, including decrypting them. That is true of every program that logs in somewhere on its own.
- **Someone with the computer's disk in their hands** can find the key DPAPI uses on the disk too, unless the disk is encrypted. Turn on BitLocker (Device encryption) on a computer that leaves the house.
- **The network:** without TLS, an MQTT password travels in plain text to the broker, as does a token to `http://` Home Assistant. Use TLS (port 8883) or `https://` where the network is not yours, or a VPN such as ZeroTier, which encrypts everything it carries.

What helps on the Home Assistant side: give HADA a broker user of its own, so its password opens nothing else and can be changed without touching anything else.

A [portable copy](../getting-started/portable.md) encrypts its secrets for the user who runs it rather than for the computer, and keeps them in its own folder, which is as protected as the place it was unpacked to.

## Everything else

- **Pipe access:** `HADA.Session` accepts only local interactive users and denies network access. Only SYSTEM, administrators or the service account can create it. Clients refuse to talk to a pipe with any other owner, and connect at identification level, so the service can check who they are but cannot act as them.
- **What the tray may send:** the tray registers its own entities and reports values for those only. It can't replace entities the service registered, and can't report values for them. A local user who runs their own program against the pipe can therefore add entities of their own to the device, but not touch the service's.
- **Control API:** any local interactive user can read status, non-secret settings and logs. Saving settings and testing connections require an elevated administrator, because a connection test may send a saved password to the address being tested.
- **Commands:** anyone in Home Assistant who can press a button, publish to a command topic or fire the command event can do what the enabled entities allow: lock the PC, change the volume, turn the screen off, show a notification, press a custom button. Shutting down, restarting, sleeping and hibernating are possible only after an administrator switched those buttons on. A command carries nothing but a value the entity accepts: `on` or `off`, a number in range, or a message; never a program or command line.
- **Custom buttons:** what a button runs is fixed in settings, which only an elevated administrator can change. PowerShell buttons run with the service's rights (SYSTEM when installed); program buttons run as the signed-in user. The tray starts programs only when the service asks it to, and talks to the service only through a pipe created by SYSTEM, an administrator or the user themself.
- **Updates:** the update check, daily or when someone presses **Check now**, and a download you start yourself, are the only connections HADA makes to anything other than your broker or Home Assistant. The installer is fetched over HTTPS from this project's releases only, and the file cannot be replaced between the checksum check and the installer starting. See [Updates](../getting-started/updating.md).
- **Running the service from a console:** a development copy started by an account other than SYSTEM does not change the permissions of an existing `%ProgramData%\HADA`; only the installed service protects that folder. When it creates the folder, or saves settings, it does add its own account, as the service account needs access.
- **Custom PowerShell sensors and buttons:** their commands run with the service's rights, which is SYSTEM when installed. Only an elevated administrator can define them, in the window or in `appsettings.json`, and an administrator can already run anything as SYSTEM, so this grants nothing new. Still, treat `%ProgramData%\HADA\settings.json` and `appsettings.json` as files that can run code.
- **Events to Home Assistant:** a quick action or a notification button sends an ID, never free text: letters, digits, `_`, `-` and `.`, at most 64 characters. The service passes on only what the tray app of the user at the computer sends, and a quick action only if it is one that exists.
- **Notification pictures:** the tray app fetches the picture a notification names, over `http` or `https` only, as the signed-in user. Whoever can send notifications can therefore make the computer request an address of their choice; the answer is used only if it is a picture of at most 5 MB.
- **Dashboard window:** it shows only the address the user entered, in a browser profile of its own under the user's profile.
- **Portable copies:** the service of a [portable copy](../getting-started/portable.md) is a process of the user who started it. It accepts settings from that same user without elevation, since whatever its settings can make it do, that user can do anyway; other users are still refused. Its secrets are encrypted for that user's account, not for the computer. Its folder is as protected as the place it was unpacked to: someone who can write there can change what HADA runs.
