# RemSound v1.4

Recording-settings dialog cleanup and a few mnemonic adjustments. No wire-format or audio-pipeline changes — v1.4 and v1.3 peers interoperate.

## Highlights

### Recording settings dialog reorganised

- **Channel mode** is now its own dedicated listbox (**Alt+C**) instead of being folded into every attribute row. The per-format attribute lists shrink correspondingly:
    - **WAV** — 6 rows → **3** (16-bit PCM / 24-bit PCM / 32-bit float)
    - **MP3** — 8 rows → **4** (128 / 192 / 256 / 320 kbps CBR)
    - **OGG-Opus** — 8 rows → **4** (96 / 128 / 192 / 256 kbps VBR)
    - **FLAC** — 4 rows → **2** (16-bit / 24-bit)
- **FLAC compression level** (0..8) now selectable in its own listbox (**Alt+L**). Previously hard-fixed at the libFLAC default of 5. The list is only shown when the file format is FLAC, with friendly tags on the endpoints (`0 — fastest encode, biggest file`, `5 — default (libFLAC reference)`, `8 — slowest encode, smallest file`). All levels produce bit-identical lossless audio — it's a pure encode-time vs file-size trade-off.

### Record menu mnemonics

- **Start / Stop recording** is now `Alt+O, R` (was `Alt+O, S`). Matches the `Ctrl+R` global toggle so the same letter does the same job from either entry point. The underline stays on a literal *R* as the label flips between "Sta**r**t recording" and "Stop **r**ecording".
- **Recording settings** is now `Alt+O, S` (was `Alt+O, T`). Reads more naturally now that the *R* slot is freed.
- **Open folder** (`Alt+O, O`) and **Change folder** (`Alt+O, C`) unchanged.

### Dialog mnemonic adjustment

- The **Cancel** button in the Recording settings dialog now uses **Alt+N** (`Ca&ncel`) rather than the conventional Alt+C, so the **Channels** listbox can take Alt+C as its natural letter. Esc still dismisses the dialog the way it always has.

## Install

1. Download `RemSound-v1.4.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`, etc.). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading from v1.3 inside a Dropbox folder

Should work cleanly via Help → Check for updates. v1.3 introduced the hardened helper (60-second retry window, exit-code check, no silent rollback) so the Dropbox-lock window is no longer a problem. If you're on v1.0/v1.1/v1.2 in a Dropbox folder, see the v1.3 release notes for the one-time manual install steps — once on v1.3 or later, future auto-updates are reliable.
