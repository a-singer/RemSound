# RemSound v3.0.1

Hot-fix for a bug in the **"Automatically open my router for incoming connections (UPnP)"** tickbox in Preferences.

## What was broken

On some network setups — machines with several network adapters, a VPN connected, or a router that doesn't answer the way RemSound's UPnP library expects — ticking the UPnP box could freeze RemSound's window. Audio kept flowing (so peers stayed connected), but you couldn't open the window again, the system-tray hotkey stopped responding, and the only way out was to end the RemSound process from Task Manager.

## Why it happened

RemSound was running the router-discovery step on the same thread that draws the window, so when discovery couldn't get a quick answer from the router it blocked the window until it finished — which on some networks meant forever. v3.0.1 moves that work off to a background thread, so the window stays responsive while RemSound looks for the router. The live status label in Preferences still updates as discovery progresses — that was already on the right thread.

The same fix is applied to the three places UPnP can kick off: at app startup (when you have UPnP ticked from a previous session), when you tick the box in Preferences, and after a sleep/resume (when RemSound re-pokes the router in case the mapping was dropped).

## No other changes

Same wire format as v3.0, same codec list, same everything else. If you were already running v3.0 happily without UPnP turned on, this update fixes a problem you may not have hit — install it at your leisure.

## Install

1. Download `RemSound-v3.0.1.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it will not touch your profiles, settings or recordings.
4. Run `RemSound.exe`. Press F1 for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

**v1.9, v2.0, v2.1, v3.0:** Help → Check for updates works — it will fetch and install v3.0.1 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.0.1 installs itself shortly after launch with a brief notice; RemSound then reopens on whichever profile you were running (new in v3.0).

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v3.0.1 but not apply it. Install v3.0.1 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
