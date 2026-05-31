# RemSound v3.2

A new audio cue plus a round of reliability work. The headline is a sound that warns you an update is about to install — and underneath it, RemSound now flatly refuses to run as two copies at once.

## New: an "Update sound" cue

RemSound now plays a short sound **just before it closes to install an update** — whether you started the update by hand or it installed silently in the background. So a silent update no longer catches you off guard: you hear that RemSound is about to restart.

Like every other cue, it's **per-profile**, you can **mute** it, and you can **swap in your own WAV** — all from **File → Preferences → Audio cue sounds**, where it's the new seventh entry in the list. Preview it with the Play button, point it at your own sound with Browse, or right-click Browse to go back to the default.

## Only one copy of RemSound at a time

RemSound now refuses to run as two copies at once. If you open it while it's already running, it asks what you'd like to do:

- **Switch to the copy that's already running** — brings it back to the front (even if it was minimised to the system tray, down by the clock).
- **Force the running copy to close and start fresh** — for when a copy is stuck or not responding.

This closes off a problem where, after an update, RemSound could end up with several copies running at once, each playing audio and getting louder — the only way out being to force-close them all.

## Updates are more dependable

- **Nothing can interrupt an update's restart any more.** Previously an "unsaved changes?" question could pop up at the wrong moment and quietly cancel it.
- **A locked (read-only) profile stays locked when you save it.** Before, deliberately saving a locked profile silently dropped the lock — which then brought the save question back and could trip up an update.
- **A single copy can't kick off two updates at once.**

## Nothing else has changed

Same wire format, same codec list, same audio cues (plus the new one), same everything else from v3.1. v3.2 talks to v3.0.x and v3.1.x peers exactly as before.

## Install

1. Download `RemSound-v3.2.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your profiles, settings or recordings.
4. Run `RemSound.exe`.

## Upgrading

**v1.9 through v3.1.3:** Help → Check for updates works — it will fetch and install v3.2 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.2 installs itself shortly after launch (and you'll hear the new update cue as it does).

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v3.2 but not apply it. Install v3.2 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
