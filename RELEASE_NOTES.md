# RemSound v1.3

Updater hardening for Dropbox-installed copies. The wire format, audio pipeline, and recording feature are unchanged from v1.2.

## What's fixed

The v1.2 self-updater silently failed on installs inside Dropbox-synced folders. The cause: Dropbox held write locks on the existing `RemSound.exe` / DLLs during the brief window between the parent process exiting and the helper script copying the new files in. Robocopy gave up after 5 seconds, and the helper unconditionally relaunched the old binary regardless — so users saw the same version they started with after pressing **Yes** on the install prompt.

## What's new

- **Robocopy retries bumped to 60 seconds per file** (was 5). Dropbox lock release happens reliably within that window in practice.
- **Helper checks robocopy's exit code.** On a true failure (exit code 8+), the helper writes `update-failed.txt` next to `RemSound.exe` with the cause and recovery steps, and does **not** relaunch the old binary. Earlier versions silently relaunched the unmodified old binary, hiding the failure.
- **Helper writes a step-by-step log** to `_update-helper.log` in the install folder. If a future update goes wrong this is the trace.
- **Stale failure markers are cleared** at the start of every new update attempt, so a successful run leaves the install folder clean.

## Install

1. Download `RemSound-v1.3.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`, etc.). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.
