# RemSound v3.1.1

Hot-fix for one small but annoying bug introduced in v3.1.

## What was happening

If the v3.1 auto-update fired while RemSound was set to start minimised to the tray, the tray icon's hover tooltip could get stuck saying **"RemSound — starting up"** indefinitely, instead of switching to the live "X peers, sending, receiving" summary after a second or so. The workaround was to show the main window and then minimise it again — that cleared the stuck text.

## Why it happened

Windows registers a tray icon's tooltip text with the shell at the exact moment the icon becomes visible. RemSound was setting an initial "starting up" string at construction time, and the shell tended to keep showing that text on hover even after the live state had been computed and pushed through. The hide-then-re-show workaround forced the shell to rebuild its registration of the icon, which picked up whatever the current live text was.

## The fix

The right tooltip text is now computed once, just before the icon becomes visible for the first time, so Windows registers the icon with the correct live summary from the start. From there, the same 1-second refresh keeps it current — same as before.

## Nothing else has changed

Same wire format, same codec list, same audio cues, same everything else from v3.1. If you didn't see the stuck-tooltip issue, this update is small and silent — but worth taking because the underlying cause was a real Windows shell behaviour and the fix is solid.

## Install

1. Download `RemSound-v3.1.1.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it will not touch your profiles, settings or recordings.
4. Run `RemSound.exe`.

## Upgrading

**v1.9 through v3.1:** Help → Check for updates works — it will fetch and install v3.1.1 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.1.1 installs itself shortly after launch.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v3.1.1 but not apply it. Install v3.1.1 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
