# RemSound v1.9

A critical fix to the auto-updater. Wire format and audio pipeline are unchanged from v1.4 onward — all versions interoperate.

## The fix

**The auto-updater now actually installs updates.** Every release of RemSound up to and including v1.8 had a fault in the update step: the install-folder path passed to the internal file-copy command ended in a backslash, which Windows mis-interpreted, so the copy command was rejected and no files were ever replaced. "Check for updates" would download the new version, but the swap silently never happened and you stayed on the old version. That copy step is now fixed.

## Important — install v1.9 by hand, just this once

Because the fault was in the *old* version doing the updating, **"Check for updates" on v1.8 or earlier cannot install v1.9** — it will download it but fail to apply it. You need to install v1.9 manually this one time. After that the updater works correctly and every future update is automatic.

To install v1.9 over an existing copy:

1. Download `RemSound-v1.9.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting the program files when prompted. The zip contains program files only — it will **not** touch your profiles or your settings.
4. Run `RemSound.exe`.

For a fresh install, just extract the zip anywhere you have write permission (e.g. `C:\RemSound\`, `Documents\RemSound\` — avoid `Program Files`) and run `RemSound.exe`. Allow on private networks when Windows Firewall prompts. Press F1 for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## From v1.9 onward

**Help → Check for updates** works properly — v1.9 → v1.10 and every future release installs itself, no manual step needed.
