# Several users on one computer

Every signed-in user's tray app connects to the service, and they all offer the same entities. Only one of them is reported at a time: the tray of whoever's desktop is on the computer's own screen. `active_user` says who that is. When another user comes to the front, their values take over at once, and commands such as `volume_level` or `notification` go to them. While the sign-in screen is shown, the session entities are *unavailable*. A user connected over Remote Desktop is reported when nobody uses the computer's own screen.
