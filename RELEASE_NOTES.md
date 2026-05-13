# RemSound v1.0

Initial public release.

## Highlights

- Low-latency peer-to-peer audio over UDP. WASAPI for any Windows audio device, with a parallel ASIO lane for pro audio interfaces (Audient, Komplete Audio, Focusrite, RME). Each lane keeps its own native callback latency.
- Pick an ASIO driver from the dropdown at the top of the Audio inputs and outputs tab to bring ASIO into the pipeline; select **(none)** to run WASAPI-only.
- Profile system. Save your entire setup — device ticks, peers, codec, latency targets, hotkeys, ASIO driver choice — into a JSON file. Pick which profile to load at every launch.
- Continuous auto-tune on either lane. Watches receive jitter and nudges the latency target up or down to stay click-free without forcing you to overshoot.
- Opus inband FEC. Single-packet losses recover transparently in both Opus modes; you don't hear them at all. PCM is also available for clean LAN connections.
- Remote control. Configurable global hotkeys can nudge a peer's RemSound volume or their Windows default-output-device master volume, opt-in on the receiver.
- Built-in self-updater. Optionally polls GitHub for newer releases on a schedule you set; can install them silently if you want.

## Install

1. Download `RemSound-v1.0.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`, etc.). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.
