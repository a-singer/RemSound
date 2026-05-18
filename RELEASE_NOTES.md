# RemSound v1.7

A self-updater fix. Wire format and audio pipeline are unchanged from v1.4 / v1.5 / v1.6 — all releases interoperate.

## Bug fix

- **Check for updates now reliably finds RemSound releases.** RemSound and the RemSound relay server are published from the same GitHub repository; the relay's releases use `server-` prefixed tags. The updater previously asked GitHub only for the single newest release of *any* kind — so whenever a relay release was the most recent, the updater misread its version and concluded RemSound was already up to date, silently skipping a real client update. The updater now scans the full release list and considers only RemSound client versions, ignoring server releases, drafts and pre-releases.

If your copy is stuck on an older version because of this, install v1.7 once by hand (below) — from v1.7 onward, **Help → Check for updates** works correctly on its own.

## Install

1. Download `RemSound-v1.7.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

v1.5 / v1.6 users: use **Help → Check for updates** — it pulls v1.7 cleanly (their updater can still see v1.7 as long as it's the newest release at check time). To be certain, the manual install above always works.

If you installed RemSound inside a Dropbox-synced (or other file-sync) folder and your install is v1.0 / v1.1 / v1.2, see the [v1.3 release notes](https://github.com/Ednunp/RemSound/releases/tag/v1.3) for one-time manual install steps. From v1.3 onward Check-for-updates handles Dropbox correctly.
