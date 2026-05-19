# RemSound v1.8

Updater improvements and a rewritten user manual. Wire format and audio pipeline are unchanged from v1.4 onward — all versions interoperate.

## Updater improvements

- **Check for updates is more reliable.** It now looks further down the GitHub release list when deciding what the newest version is, so it always finds the latest RemSound release.
- **An update can no longer overwrite your own data.** The update now replaces RemSound's program files only — it leaves your settings file, and your profiles, logs and recordings folders, untouched. Previously a release package could in principle carry stray files over the top of yours; the updater now refuses to copy those even if they were present.
- **A successful update leaves a tidy folder.** Once an update finishes cleanly, the temporary update files, the update log, and any leftover failure note are all cleared automatically.
- **The "update failed" note is now plain English.** If an update can't finish, the `update-failed.txt` note left next to RemSound is written in clear, non-technical language with simple numbered steps. Technical detail for support stays in the separate update log.

## Rewritten user manual

The user manual (press F1 inside RemSound, or open `readme.html`) has been rewritten from the ground up in plain language. Technical jargon has been removed or explained in everyday terms, and the writing now describes what to do rather than narrating keystrokes.

## Install

1. Download `RemSound-v1.8.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

v1.5 / v1.6 / v1.7 users: use **Help → Check for updates** — it pulls v1.8 cleanly. The manual install above always works too.

If you installed RemSound inside a Dropbox-synced (or other file-sync) folder and your install is v1.0 / v1.1 / v1.2, see the [v1.3 release notes](https://github.com/Ednunp/RemSound/releases/tag/v1.3) for one-time manual install steps. From v1.3 onward Check-for-updates handles synced folders correctly.
