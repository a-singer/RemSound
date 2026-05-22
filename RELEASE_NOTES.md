# RemSound v2.1

Automatic router setup for internet streaming, a new "lock this profile" option to keep your default profile from prompting on close, a small notice before background updates install, and fixes for the "no sound after sleep / hibernate" problem. Wire format and audio pipeline are unchanged from v1.5 onward — all versions from v1.5 to v2.1 interoperate.

## What's new

* **Automatic router port opening (UPnP).** RemSound can now ask your router to open the audio port for incoming peer connections, so you don't have to set up port forwarding by hand. Off by default — tick **"Automatically open my router for incoming connections (UPnP)"** in Preferences (Ctrl+P) to turn it on. A live status line right below the tick tells you what happened: found your router and opened the port (with your external address), found your router but the port couldn't be opened, no router found that supports the feature, or the router opened the port but you're behind a carrier-grade NAT (common on mobile broadband — peers won't reach you directly, use Tailscale or the relay instead). Works with UPnP, NAT-PMP and PCP — whichever your router speaks.

* **Lock profile (read-only).** New tickable item in the File menu (Alt+F, L). When ticked, anything you change while RemSound is running stays in this session and is forgotten on close — your saved profile is left untouched, and there is **no save-changes prompt on exit**. Useful when you have a default profile you tweak constantly but don't want to commit those tweaks, and essential for unattended shutdowns where a save prompt could deadlock the close (screen reader gone, remote session dropped, machine hibernating). The lock state is saved on the profile itself, so it sticks across launches. Save As on a locked profile produces an unlocked copy. The window title and the startup profile picker both show "(read-only)" so you can tell at a glance.

* **Check for updates on startup.** New checkbox in Preferences, **on by default**. Shortly after RemSound launches it has a quiet look for a new release. Combined with "Silently install updates", this is "leave RemSound to keep itself up to date and never think about it".

* **Brief notice before a silent update installs.** When RemSound finds an update at startup and is set to install silently, it now shows a small window with the version it's about to install and an 8-second countdown. Press Enter (or wait) to install now, "Skip this version" to leave the update for another day, or "Postpone" to try again at the next check. Without this notice, the app would silently close on you a few seconds after launch and you'd have no idea why.

* **"Cue sounds" in Preferences is now labelled "Audio cue sounds"** for clarity.

## Bug fixes

* **No sound after the computer wakes from sleep.** On many setups (especially USB audio interfaces), waking the computer left RemSound's audio engine in a state where it looked like it was running but no sound actually came out — you'd have to quit and reopen RemSound. RemSound now notices when the system has woken up, waits a moment for USB devices to settle, and rebuilds its audio engine automatically. A brief "Reconnecting to audio driver, please wait..." window appears during the rebuild so you can see it's happening.

* **Receiver audio silent after waking from hibernate.** A follow-up to the wake-from-sleep fix above: on hibernate (rather than ordinary sleep), the ASIO receive output's tick selection could be silently wiped during hibernation entry, leaving the receiver running silent on resume even though everything looked normal in the logs. Fixed by recognising the transient driver-disappeared state at hibernation entry and preserving the user's tick until the driver comes back.

## Install

1. Download `RemSound-v2.1.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it will not touch your profiles, settings or recordings. (For a fresh install, just extract it anywhere you have write permission and run `RemSound.exe`.)
4. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts. Press F1 for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

**v1.9, v2.0:** Help → Check for updates works — it will fetch and install v2.1 automatically. If you've ticked the new "Check for updates on startup" and "Silently install updates", v2.1 will install itself shortly after launch with a brief notice.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v2.1 but not apply it. Install v2.1 by hand using the steps above — just this once. From the build you install onward, updates are automatic.

If you installed RemSound inside a synced folder (Dropbox etc.) and your install is v1.0 / v1.1 / v1.2, see the [v1.3 release notes](https://github.com/Ednunp/RemSound/releases/tag/v1.3) for one-time manual install steps.
