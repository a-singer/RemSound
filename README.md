# RemSound

Low-latency peer-to-peer audio between two or more Windows PCs over UDP. Pick what each machine captures and what it plays back; audio flows directly between them, no central server.

Built for music collaboration over the internet, live monitoring across rooms in a house, podcast co-hosting, NVDA-Remote audio workflows, and anything else that wants "send the sound from this PC to that PC, fast".

## Highlights

- **WASAPI and ASIO side by side.** Run them as two independent UDP streams at their own native latencies, or use WASAPI alone. ASIO drivers get hardware-clocked timing; WASAPI gets push-mode timing on single-source captures.
- **Profiles.** Save your full setup (device ticks, peers, codec, latency targets, hotkeys, ASIO driver) into one JSON file. Pick which profile to load at launch.
- **Continuous auto-tune.** Watches receive jitter and nudges the latency target to stay click-free without forcing you to overshoot. Independent per-lane in WASAPI+ASIO mode.
- **Opus with inband FEC.** Single-packet losses recover transparently — no click. PCM 24-bit 48 kHz is also available for clean LAN.
- **Remote control hotkeys.** Configurable global hotkeys can nudge a peer's RemSound volume or their Windows system master volume, opt-in on the receiver.
- **Built-in self-updater.** Optional GitHub-driven update check on a schedule you set.
- **Designed for screen readers.** Each control has a paired Alt+letter mnemonic. State changes raise the right UIA notifications. F1 anywhere opens the user manual.

## Install

1. Download the latest `RemSound-vX.Y.zip` from [Releases](https://github.com/Ednunp/RemSound/releases).
2. Extract somewhere it can write — e.g. `C:\RemSound\`, your `Documents`, or a folder in your user profile. Avoid `Program Files` unless you grant write permission to the install folder (the self-updater needs to overwrite files in place).
3. Run `RemSound.exe`. On first launch Windows Firewall will prompt — allow on private networks.
4. Open the user manual from the **Help** menu (or press F1) for the full walkthrough.

RemSound requires the .NET 10 Desktop Runtime. If it's not installed, Windows offers to fetch it on first launch. You can also install it from <https://dotnet.microsoft.com/download/dotnet/10.0> (pick the "Windows x64 Desktop Runtime").

## Updates

RemSound can check this repository's Releases page on a schedule (never, hourly, every 6 hours, every 24 hours) and either prompt you to install or do it silently. Configure via File → Preferences. You can also trigger a manual check from the Help menu or the same Preferences dialog.

## Build from source

You need the .NET 10 SDK. The solution lives at `RemSound.slnx`.

```powershell
cd D:\proj\RemSound
dotnet build -c Release
dotnet publish src\RemSound.App\RemSound.App.csproj -c Release
```

The publish output lands at `src\RemSound.App\bin\Release\net10.0-windows\publish\`. Copy its contents into a folder of your choice — or zip it for distribution. Don't enable `PublishSingleFile` or `SelfContained=true`; RemSound ships framework-dependent on purpose so the publish folder stays under 2 MB.

## Project layout

```
src/RemSound.Core      packet protocol, peer discovery, hotkeys, MMCSS, heartbeat, settings, AppConfig
src/RemSound.Sender    capture → mix → encode → UDP send
src/RemSound.Receiver  UDP receive → ring buffer → drift-corrected playout → render
src/RemSound.Harness   console test program (1 sender → 1 receiver, no UI)
src/RemSound.App       WinForms UI (sender + receiver + heartbeat + discovery + updater)
```

## Issues and feedback

Open an issue on the [GitHub issues page](https://github.com/Ednunp/RemSound/issues). If reporting an audio problem, please tick **File → Preferences → Enable logs**, reproduce the issue, then attach the latest log file from `logs\` next to `RemSound.exe`.

## Licence

MIT. See `LICENSE`.
