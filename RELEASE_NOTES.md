# RemSound v1.3

Updater hardening for Dropbox-installed copies. The wire format, audio pipeline, and recording feature are unchanged from v1.2.

## What's fixed

The v1.2 self-updater silently failed on installs inside Dropbox-synced folders. The cause: Dropbox held write locks on the existing `RemSound.exe` / DLLs during the brief window between the parent process exiting and the helper script copying the new files in. Robocopy gave up after 5 seconds, and the helper unconditionally relaunched the old binary regardless — so users saw the same version they started with after pressing **Yes** on the install prompt.

## What's new

- **Robocopy retries bumped to 60 seconds per file** (was 5). Dropbox lock release happens reliably within that window in practice.
- **Helper checks robocopy's exit code.** On a true failure (exit code 8+), the helper writes `update-failed.txt` next to `RemSound.exe` with the cause and recovery steps, and does **not** relaunch the old binary. Earlier versions silently relaunched the unmodified old binary, hiding the failure.
- **Helper writes a step-by-step log** to `_update-helper.log` in the install folder. If a future update goes wrong this is the trace.
- **Stale failure markers are cleared** at the start of every new update attempt, so a successful run leaves the install folder clean.

## Already on v1.0, v1.1, or v1.2 and inside a Dropbox folder? Read this first

**The auto-updater in earlier versions won't reliably take you to v1.3** if you installed RemSound inside a Dropbox-synced (or other file-sync) folder. That's the bug v1.3 fixes — but the fix is in the *running* version's helper script, not in the downloaded zip, so your old version can't use it. The pre-v1.3 helper retries for only 5 seconds before silently giving up and relaunching the old binary; your About dialog will keep saying the old version even after pressing **Yes** on the update prompt.

Manual install fixes it permanently:

1. Quit RemSound (right-click the tray icon → Exit, or just close the window).
2. Wait ~30 seconds for Dropbox to settle.
3. Download `RemSound-v1.3.zip` below.
4. Extract it on top of your existing install folder (right-click → Extract All, point at your RemSound folder, accept "replace files"). Or unzip somewhere else first and copy the files across.
5. Launch RemSound.exe. About should now report v1.3.0.

From v1.3 onward, **Check for updates** uses the hardened helper (60-second retry window, failure markers, no silent rollback), so you only have to do this manual step once.

## Install (clean machine)

1. Download `RemSound-v1.3.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`, etc.). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.
