# RemSound v2.2

A maintenance release that makes RemSound use less of your computer's CPU and memory, especially when sending audio with the Opus codec. No new features to learn, no settings have changed, audio sounds exactly the same. Wire format and audio pipeline are unchanged from v1.5 onward — every version from v1.5 to v2.2 still talks to every other version cleanly.

## What's lighter on your computer

* **Opus sending now uses much less memory.** RemSound's Opus encoder used to do quite a lot of one-off memory work on every audio frame — about 4 MB per second of "throwaway" memory churn while sending Opus audio. v2.2 ships a native build of the same encoder that does the same work in a tighter way. The audio you hear is identical (it really is the same encoder, just packaged better); the memory churn drops by about 97 %. On laptops you should see less background CPU when streaming Opus, and long sessions are less likely to see brief pauses while Windows tidies up memory.

* **Smaller all-round efficiency tidy-up.** A handful of small fixes — RemSound checks the audio-device list a bit less often, reuses some small bits of memory it used to make fresh each time, and skips some paperwork on the receive side when there's nothing to do. Each one is small on its own; together they shave a few percent off RemSound's everyday CPU footprint and reduce memory churn modestly.

* **Removed some old leftover code** that was retired months ago but still lived on as zero-valued columns in the diagnostic log. Same behaviour, cleaner files for anyone who reads the diagnostic logs.

## For people who use the diagnostic logs

* **New columns added** (only emit when Enable logs is ticked, so cost nothing when off):
  * `cpu=X.X%` — how much of one CPU core RemSound is using right now.
  * `memMB=X.X` and `wsMB=X.X` — RemSound's memory footprint (managed heap and working set).
  * `allocKBps=X.X` — how fast RemSound is asking Windows for new bits of memory right now. A low number is what we want.
  * `captureMs / sendMs / recvMs / renderMs` — milliseconds of CPU each of RemSound's four audio threads spent doing work in the last second.

* **Some old columns removed.** `fanCacheMs`, `driftDrop`, `driftDropΔ`, `driftRep`, `driftRepΔ` and `driftAcc` are gone — they were always zero after the playback engine was changed in May.

## Nothing else has changed

No bug fixes in v2.2 specifically. Everything in v2.1 — the UPnP automatic router-opening, the lock-profile (read-only) tick, the silent-install notice, the wake-from-sleep audio fix, the hibernate fix — is still in place and works exactly the same.

## Install

1. Download `RemSound-v2.2.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it will not touch your profiles, settings or recordings.
4. Run `RemSound.exe`. Press F1 for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

**v1.9, v2.0, v2.1:** Help → Check for updates works — it will fetch and install v2.2 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v2.2 will install itself shortly after launch with a brief notice.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v2.2 but not apply it. Install v2.2 by hand using the steps above — just this once. From the build you install onward, updates are automatic.

If you installed RemSound inside a synced folder (Dropbox etc.) and your install is v1.0 / v1.1 / v1.2, see the [v1.3 release notes](https://github.com/Ednunp/RemSound/releases/tag/v1.3) for one-time manual install steps.
