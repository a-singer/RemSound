# RemSound v3.1.2

A small accessibility fix for the system tray icon, plus a clearer "check for updates" message for Windows 7 users. Minor update — safe to take, nothing else changes.

## The tray icon's double announcement (screen reader users)

If you run RemSound minimised to the tray with a screen reader, you may have noticed the tray icon reading out **two states one after the other** every time you landed on it — for example "no peers connected" and then immediately "1 peer connected". It always happened in that same order, and showing the window then minimising it again was the only thing that cleared it.

### Why it happened

Windows stamps a tray icon with its name at the exact moment the icon appears. On a fresh start — say, just after you log in — the icon appears a second or so before your other machine has reconnected, so the honest state at that instant really is "no peers". Windows remembers that as the icon's name and only ever refreshes the little hover bubble afterwards, never the stamped name. So your screen reader read the frozen name ("no peers") followed by the live bubble ("1 peer") — two readings of what should be one thing.

### The fix

RemSound now re-stamps the icon itself whenever something real changes — a peer connecting or dropping, sending or receiving switching on or off, or a recording starting or stopping. That's the automatic version of the old "show the window then minimise again" trick. Your screen reader now hears a single, current state.

## Recording shows as "recording" in the tray

While a recording is running, the tray summary now simply says **"recording"** rather than counting the seconds. A live timer would have forced the icon to flicker every second or left a screen reader announcing a stale time — neither was good. If you want the exact length, it's on the main window.

## Clearer update message on Windows 7

On a Windows 7 machine that can't make a secure (HTTPS) connection to the update server, **Check for updates** used to report "you're already on the latest version" — which was wrong and confusing. It now tells you plainly that the secure connection failed, names the Windows updates that fix it, and gives you a direct link to download the new version by hand if you'd rather. Windows 10 and 11 were never affected.

## Nothing else has changed

Same wire format, same codec list, same audio cues, same everything else from v3.1. v3.1.2 talks to v3.0.x and v3.1.x peers exactly as before.

## Install

1. Download `RemSound-v3.1.2.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it will not touch your profiles, settings or recordings.
4. Run `RemSound.exe`.

## Upgrading

**v1.9 through v3.1.1:** Help → Check for updates works — it will fetch and install v3.1.2 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.1.2 installs itself shortly after launch.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v3.1.2 but not apply it. Install v3.1.2 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
