# RemSound v1.6

Three reliability fixes — peer-address recovery, a reconnect crash, and a long-run memory/CPU leak. Wire format and audio pipeline are unchanged from v1.4 / v1.5 — all three releases interoperate.

## Bug fixes

- **Peer address recovery.** If the address you connected to stops responding — a peer rebooted onto a new IP, or a computer name resolved (via DNS / a router or Pi-hole record) to a stale address — RemSound now follows the peer to the live address it is still heartbeating from, instead of sending audio to a dead address indefinitely. Recovery is automatic within a few seconds. It only adopts private-network (LAN) addresses, so a relay's public address can never be mistaken for a moved peer.
- **Reconnect crash fixed.** When a peer reconnected — typically after rebooting — the Connectivity tab's peer list could be read mid-rebuild with a stale list index, throwing an `IndexOutOfRangeException` from the 1-second status timer and bringing the app down with a crash dialog. The list reads are now bounds-checked, and the status tick is wrapped so a transient UI hiccup is logged instead of fatal.
- **Long-run memory and CPU leak fixed.** A receiver left running for hours could grow to several gigabytes of memory and climbing CPU. Decoder sessions orphaned by peer reconnects (each reconnect creates a fresh stream identity) were not being reliably reclaimed — they piled up, each holding a multi-megabyte playout buffer and costing render-thread time on every audio callback. Idle sessions are now reaped on their own activity timer, with a hard cap on live sessions as a backstop, so memory and CPU stay bounded however long RemSound runs.

## Install

1. Download `RemSound-v1.6.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

v1.3 / v1.4 / v1.5 users on any install location can use **Help → Check for updates** — the hardened updater pulls v1.6 cleanly.

If you installed RemSound inside a Dropbox-synced (or other file-sync) folder and your install is v1.0 / v1.1 / v1.2, see the [v1.3 release notes](https://github.com/Ednunp/RemSound/releases/tag/v1.3) for one-time manual install steps. From v1.3 onward Check-for-updates handles Dropbox correctly.
