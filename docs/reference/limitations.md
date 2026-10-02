# Known limitations

- **Lock screen from the service:** as a service in session 0, the lock action disconnects the active console session, which returns Windows to the lock screen. This hasn't been verified on every Windows edition. Run from a user session, it uses `LockWorkStation`.
- **Sleep, hibernate, shut down, restart, turn off display, media keys:** these go through the same Windows calls as the Start menu and a multimedia keyboard, but have not been tried on every kind of computer. On Modern Standby devices *Sleep* and *Turn off display* come to much the same thing. `wake_display` cannot wake a computer that is asleep, because a sleeping computer is not connected.
- **Notifications** are the simple kind: a title and a text, without buttons or images.
- **Do not disturb** is read from a Windows state that is not documented. If a Windows version stops providing it, the entity is not registered.
- **Tray sensors after a crash of the service:** when the tray exits, its sensors turn unavailable. If the service itself is killed while the tray is connected, e.g. by a power cut, and Windows then starts without anyone signing in, they show their last value until the tray connects again.
- **Screen off on Modern Standby devices:** many laptops, most ARM64 ones included, go to sleep within seconds of the screen turning off and drop the network. `display_on` is sent as `off` first, but if the connection is already gone, Home Assistant shows the device as unavailable instead.
- **Microphone and camera:** detection reads the usage history Windows keeps for its privacy settings. Apps that bypass it are not seen, and an app that crashed while using the device may be reported as still using it until Windows restarts.
- **Lists in `appsettings.json`:** a list saved from the window (disabled and enabled entities, custom sensors and buttons) overrides the file's list entry by entry, so entries beyond the saved list's length still apply. Keep such lists in one place.
- **Keys pressed by HADA:** key buttons and the media keys are injected into the user's session. Windows drops injected keys while a program running as administrator has the focus, and never lets them reach its own secure shortcuts (`Ctrl+Alt+Del`, `Win+L`).
- **Buttons of notifications** are reported only by the tray app that showed the notification and is still running.
- **GPU load** comes from Windows' performance counters, which Task Manager uses too. Temperatures are not reported: reading them needs a kernel driver, which HADA does not install.
