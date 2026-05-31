# RemSound v3.1.3

An important reliability fix. After an update, a chain of small faults could line up and leave RemSound misbehaving — in the worst case, more than one copy running at once with the sound getting louder and louder. This release breaks that chain in several places, the most important being that RemSound now flatly refuses to run as two copies at the same time.

## What could go wrong (and now can't)

A few separate problems were feeding into each other:

- **Locked profiles could quietly unlock themselves.** If you'd marked a profile as read-only (locked) and then deliberately saved it, the save was silently dropping the lock. Next time you opened that profile it was editable again — so it started asking "save your changes?" when you didn't expect it.
- **That "save your changes?" question could block an update.** When RemSound updates itself it has to close and restart. If the save question popped up at that moment, a stray Enter or Escape landed on "Cancel" and quietly stopped the restart — so the update never finished cleanly.
- **Nothing stopped extra copies running.** With the restart half-done, RemSound could end up with two copies going at once — and then more — each one playing incoming audio, which is why the sound climbed to a deafening level. The only way out was force-closing them all.

## What's fixed

- **Only one copy at a time.** RemSound now refuses to run as two copies. If you open it while it's already running, it asks what you'd like to do: **switch to the copy that's already running** (it may be tucked in the system tray, by the clock), or — if that copy is stuck — **force it closed and start fresh**. This makes the stacking-copies runaway impossible.
- **Updates can't be interrupted.** When RemSound updates and restarts, nothing is allowed to get in the way — no question can cancel the restart.
- **Locked means locked.** Marking a profile read-only now survives saving it, deliberately or otherwise.
- **No double-installs.** A single copy can no longer kick off two update installs at once.

## Nothing else has changed

Same wire format, same codec list, same audio cues, same everything else from v3.1. v3.1.3 talks to v3.0.x and v3.1.x peers exactly as before.

## Install

1. Download `RemSound-v3.1.3.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your profiles, settings or recordings.
4. Run `RemSound.exe`.

## Upgrading

**v1.9 through v3.1.2:** Help → Check for updates works — it will fetch and install v3.1.3 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.1.3 installs itself shortly after launch.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v3.1.3 but not apply it. Install v3.1.3 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
