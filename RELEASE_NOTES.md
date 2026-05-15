# RemSound v1.5

Menu reorganisation plus two real bug fixes for BothIndependent mode. Wire format and audio pipeline are unchanged from v1.4 — v1.4 and v1.5 peers interoperate.

## Bug fixes

- **BothIndependent recording**: when both WASAPI and ASIO output devices were ticked, recordings came out garbled and roughly double the expected duration. The recording taps fired from both lanes independently and the writer thread appended both streams into a single ring as if they were sequential audio. The recorder now has per-lane rings and mixes them in the writer thread before handing the resulting per-direction stream to the file writer.
- **A peer announcing on the WASAPI lane was inaudible when the receiver only had an ASIO output ticked** (and vice versa). The session was opened but its decoded samples sat in the SessionPlayout ring with nothing draining them. Sessions whose announced lane has no active output device now fall through to whichever lane IS being read — so a single ASIO output plays everyone, regardless of how each peer announced their stream.

## Menu reorganisation

- **New Options menu (Alt+O)** holds *Recording settings*, *Keyboard shortcuts*, *Startup behaviour*, and *Preferences*. Pre-v1.5 these were scattered across the File menu (Keyboard shortcuts, Preferences), the Record menu (Recording settings), and a button inside the Preferences dialog itself (Startup behaviour).
- **Record menu mnemonic moves from Alt+O to Alt+K**. Rendered as "Record (Alt+K)" so the chord is visible despite K not being a letter in "Record". Alt+R is taken by the Receive audio checkbox; Alt+O has the natural home on the Options menu now.
- **File menu — new Recent profiles submenu (Alt+F, R)**. Lists the five most-recently-opened profiles, newest first. Press 1..5 while the submenu is open to jump straight to a slot; arrow + Enter also works. Missing files (e.g. external drive unmounted) are skipped from the menu but kept in storage in case they come back.
- **File menu — small mnemonic shuffle** to make room for Recent profiles: *Rename current profile* moves from Alt+M to Alt+M (was R), and *Minimise to tray* moves from M to **N**.
- **Lock to audio clock** (Audio profile tab) was Alt+K; now **Alt+D** (the D from "au**d**io"). Top-level menus win Alt-letter dispatch on a Form, so Record taking Alt+K bumped the checkbox.

## UX additions

- **Ctrl+O** is now bound to Open profile (matches the menu chord). Previously the menu had no global shortcut.
- **New global hotkey: Start / Stop recording.** Pickable from Options → Keyboard shortcuts. Unbound by default so it doesn't collide with anything on a fresh install. Works system-wide — RemSound doesn't need keyboard focus.

## Install

1. Download `RemSound-v1.5.zip` from this release.
2. Extract somewhere with write permission (e.g. `C:\RemSound\`, `Documents\RemSound\`). Avoid `Program Files` unless you grant write permission so the self-updater can replace files in place.
3. Run `RemSound.exe`. Allow on private networks when Windows Firewall prompts.
4. Press F1 (or use the Help menu) for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading from v1.3 / v1.4

If you installed RemSound inside a Dropbox-synced (or other file-sync) folder and your install is v1.0 / v1.1 / v1.2, see the [v1.3 release notes](https://github.com/Ednunp/RemSound/releases/tag/v1.3) for one-time manual install steps. From v1.3 onward Check-for-updates handles Dropbox correctly.

v1.3 and v1.4 users on any install location can use **Help → Check for updates** — the hardened updater in v1.3+ pulls v1.5 cleanly.
