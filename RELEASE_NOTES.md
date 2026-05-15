# RemSound v1.2

Recording, sound cues, and receiver-side drift compensation. The wire format and audio pipeline are unchanged from v1.1, so v1.1 and v1.2 peers interoperate.

## Highlights

- **Recording to disk.** Dedicated Record menu (Alt+O) with Start/Stop on Ctrl+R, settings dialog, per-profile source / format / bit-depth / channel-mode / folder. Pick **received only**, **sent only**, or **both** as the recording source. Files are crash-resilient — a process crash mid-recording leaves a playable file containing everything up to the last header refresh (~5 seconds).
- **Four output formats, all functional.** WAV (16/24-bit PCM or 32-bit float), MP3 (LAME, 128–320 kbps CBR), **OGG-Opus** (96–256 kbps VBR, reuses the Concentus encoder from the wire path), **FLAC** (pure-managed CUETools FLAKE, lossless ~50% the size of WAV). All four record at 48 kHz; labels make the rate explicit.
- **Recording start / stop sound cues.** Short audible confirmation when recording transitions on or off. Played via the default Windows output device, separate from the recording pipeline, so a normal recording does not include the cue.
- **Per-cue sound preferences.** The single "Mute connect/disconnect sounds" checkbox in Preferences is replaced by a per-cue CheckedListBox: Connect / Disconnect / Recording start / Recording stop. Old profiles with the legacy mute on are honoured automatically on first load.
- **Receiver-side drift compensation upgraded.** Continuous `WdlResampler` running at a slowly-updated rate ratio replaces v1.1's discrete single-frame splice corrector. Smooths long-session sender-vs-receiver clock drift without the occasional 21 µs splice.

## UI changes

- **Record menu moved to Alt+O.** The old `Alt+R` chord collided with the **Receive audio (Alt+R)** checkbox on the main form. Inside the menu the item mnemonics are unchanged (S / T / O / C).
- **Auto-tune interval label is mode-aware.** In BothIndependent mode the interval combo's label reads "Auto-tune interval — WASAPI and ASIO" so it's clear the same combo drives both lanes' tick cadence — each lane still independently tunes to its own latency target.

## Diagnostics (only active with Enable logs ticked)

- Per-stage discontinuity probes: sender raw-capture, sender pre-encode (per-lane in BothIndependent), receiver post-decode, post-ring-buffer, post-resampler. Lets log inspection localise where in the chain an audio click was introduced.
- Wire-level sequence tracking on each PCM stream: in-order / missed / reordered / duplicated packet counts in the diag log.
- Clipped-sample delta in the diag log.

## Bug fixes

- Auto-tune interval combo no longer greys out when only the ASIO lane's auto-tune is ticked in BothIndependent mode. Previously the combo's enabled state followed only the WASAPI checkbox.

## Install

1. Download `RemSound-v1.2.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`, etc.). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.
