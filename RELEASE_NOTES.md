# RemSound v3.0

A big release with two things you'll actually notice — plus everything from the never-separately-released v2.2 work bundled in. v3.0 is a wire-format break: two v3.0 machines talk to each other perfectly, but a v3.0 machine talking to a v2.x machine will have very high latency until you upgrade the second machine too. See **Compatibility** below.

## What's new

* **Opus "live latency" mode.** RemSound now sends sound in tiny 2.5-millisecond chunks instead of the usual 10 or 20 ms. End-to-end delay drops to about 5 ms of codec delay — close to PCM — perfect for playing along with someone in real time. Uses a bit more network bandwidth than the regular Opus mode but still a fraction of PCM. Best on a clean wired network. Pick it from the codec list as **"Opus, live latency — for jamming and monitoring"**.

* **RemSound reopens on the same profile after it updates itself.** If a silent update fires in the middle of a session, the session drops briefly while the new files swap in, then RemSound reopens with the same devices, peers and settings — you don't see the profile picker, and you don't have to be at the computer for it. The next time you launch RemSound yourself, your normal startup choice applies as before.

## Other changes

* **Codec list is now three choices, with clearer names.**
  * `PCM 48K 24 bit — uncompressed` — best quality, ~2.3 Mbps
  * `Opus, broadcast quality — loss tolerant` — ~200 kbps, ~12 ms codec delay (was "Opus high quality (20 ms)")
  * `Opus, live latency — for jamming and monitoring` — ~320 kbps, ~5 ms codec delay (the new one)

  The old `Opus lower quality (10 ms)` middle option has been retired — it sat between the other two without a clear reason to pick it. If your saved profile was using it, RemSound silently picks broadcast quality for you on first launch — slightly more delay, more loss tolerance. Pick "live latency" if you want the new low-latency mode.

* **Save on a locked (read-only) profile now goes through when you ask on purpose.** The lock still suppresses the automatic "save your changes?" prompt on close (its main job), but if you press Save (Ctrl+S) or pick File → Save deliberately, a one-time warning explains what's about to happen and lets you confirm or cancel. Once you tick "do not show again", future deliberate saves on a locked profile go through silently. Save as... is unchanged — it always works.

## Includes everything from v2.2 (which was never separately released)

* **Native Opus encoder.** RemSound's Opus encoder used to put quite a lot of work on Windows' memory manager — about 4 megabytes per second of "throwaway" memory churn while sending Opus audio. v3.0 ships a native build of the same encoder that does its work in a tighter, faster way. The audio you hear is identical (it really is the same encoder, just packaged better); the memory churn drops by about 97 %. On laptops you should see less background CPU when streaming Opus, and longer sessions are less likely to see brief pauses while Windows tidies up memory.
* **Smaller all-round efficiency tidy-up.** RemSound checks the audio-device list less often, reuses some small bits of memory it used to make fresh each time, and skips some paperwork on the receive side when there's nothing to do. Each one is small on its own; together they cut everyday memory churn modestly.
* **New diagnostic log columns** (only emit when Enable logs is ticked):
  * `cpu` — how much of one CPU core RemSound just used
  * `memMB` / `wsMB` — managed heap and working set
  * `allocKBps` — per-second memory-churn rate (lower is better)
  * `captureMs / sendMs / recvMs / renderMs` — how busy each of the four audio threads is
* **Some old diagnostic columns removed.** `fanCacheMs`, `driftDrop`, `driftRep`, `driftAcc` are gone — they were always zero after the playback engine changed in May.

## Compatibility

**v3.0 is a wire-format break.** RemSound describes audio frame sizes to other RemSound machines using a slightly different unit on the wire (sample-count instead of milliseconds) so the new 2.5 ms mode can be expressed cleanly.

* **v3.0 ↔ v3.0**: works perfectly.
* **v3.0 ↔ v2.x**: audio still passes (the underlying Opus / PCM decode is unchanged) but the v2.x side will mis-read the frame-size announcement and build up too much buffer. Expect very high latency on that side until you upgrade it to v3.0.

To avoid the high-latency window: update **both** machines to v3.0 before your next session. The auto-updater on v1.9 and later will handle the upgrade for you, but the timing matters — if one machine updates before the other, latency will be high until the second machine catches up.

**Suppression flags** from v2.x for the "save was blocked on a read-only profile" dialog do **not** carry over. You'll see the new one-time warning once per machine. That's deliberate; the behaviour changed and you need to know.

## Install

1. Download `RemSound-v3.0.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it will not touch your profiles, settings or recordings.
4. Run `RemSound.exe`. Press F1 for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

**v1.9, v2.0, v2.1:** Help → Check for updates works — it will fetch and install v3.0 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.0 installs itself shortly after launch with a brief notice; RemSound then reopens on whichever profile you were running.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v3.0 but not apply it. Install v3.0 by hand using the steps above — just this once. From the build you install onward, updates are automatic.

If you installed RemSound inside a synced folder (Dropbox etc.) and your install is v1.0 / v1.1 / v1.2, see the [v1.3 release notes](https://github.com/Ednunp/RemSound/releases/tag/v1.3) for one-time manual install steps.
