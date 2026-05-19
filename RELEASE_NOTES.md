# RemSound v2.0

A smoother startup for setups that use an ASIO driver. Wire format and audio pipeline are unchanged from v1.4 onward — all versions interoperate.

## Change

**Startup no longer looks frozen when a profile uses an ASIO driver.** Opening an ASIO driver takes a couple of seconds, and that happens while the main window is being built — so the window used to appear blank or "Not Responding" during that time. RemSound now shows a small **"Loading audio driver, please wait..."** window while the driver opens, then brings up the main window as usual. Profiles that don't use an ASIO driver are unaffected — they start instantly, with no extra window.

The ASIO driver itself is opened exactly as before; only the on-screen wait indicator was added, so this changes nothing about how RemSound talks to your audio hardware.

## Install

1. Download `RemSound-v2.0.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it will not touch your profiles or settings. (For a fresh install, just extract it anywhere you have write permission and run `RemSound.exe`.)
4. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts. Press F1 for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

**v1.9 users:** Help → Check for updates works — it will fetch and install v2.0 automatically.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v2.0 but not apply it. Install v2.0 by hand using the steps above — just this once. From the build you install onward, updates are automatic.

If you installed RemSound inside a synced folder (Dropbox etc.) and your install is v1.0 / v1.1 / v1.2, see the [v1.3 release notes](https://github.com/Ednunp/RemSound/releases/tag/v1.3) for one-time manual install steps.
