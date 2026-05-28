# RemSound v3.1

A meaningful round of work on **audio cue sounds** and the **system tray**. Two new cues, a way to swap in your own WAV files per profile, and a rebuilt tray menu with a live status tooltip. Plus a couple of smaller bug fixes. No wire format change — v3.1 talks to v3.0.x machines exactly as before.

## New audio cue sounds

Two new sounds join the existing connect / disconnect / record-start / record-stop cues:

* **Profile saved.** Plays a short cue whenever a profile is saved — either via File → Save (Ctrl+S) or Save as. An audible "yes, that took" so you don't have to look at the screen.
* **Profile switched.** Plays a short cue whenever a profile finishes loading. Fires at startup if you started with a profile, and after every mid-session profile switch. **It plays the new profile's cue, not the old one's** — so if you give each profile a different switched-to sound, you can hear which profile you're now on without checking the title bar.

## Custom cue sounds, per profile

The **Audio cue sounds** list in Preferences now has six entries (Connect, Disconnect, Recording start, Recording stop, Profile saved, Profile switched), each with a tickbox to enable or silence it.

Two new buttons sit just below the list, both acting on whichever cue is currently highlighted:

* **Play [cue name]** (Alt+P) previews the currently-configured sound through your default Windows output. Works regardless of whether the cue is ticked, so you can listen before deciding to enable.
* **Browse for [cue name]…** (Alt+B) opens a Windows file picker so you can choose your own WAV file to replace the default sound for that cue. Right-clicking the Browse button offers a **Use default sound** item to undo the swap.

Both the tick states AND the custom sound choices are **saved with the active profile**. A "quiet listening" profile can have all cues off; a "studio" profile can use a distinct set of sounds. When you switch profiles, the cues switch too.

The default WAV files have moved out of the install folder root into a new `sounds\` subfolder, keeping the install layout tidier.

## System tray icon redesigned

**Hover summary.** The tray icon's tooltip now shows a live summary that refreshes every second:

* `RemSound — not connected`
* `RemSound — 2 peers, sending (WASAPI), receiving (WASAPI)`
* `RemSound — recording for 5:23, 1 peer, sending (WASAPI + ASIO), receiving (WASAPI + ASIO)`

The recording timer only shows when you're actually recording. The "(WASAPI)", "(ASIO)", or "(WASAPI + ASIO)" tag reflects which lanes the corresponding direction is actually using.

**Right-click menu rebuilt.** Five items, each with a single-letter shortcut:

| Item | Key |
|---|---|
| Show RemSound | W |
| Enable sending (tickable, shows current state) | S |
| Enable receiving (tickable, shows current state) | R |
| Profiles (submenu of recent profiles) | P |
| Exit | X |

* **Show RemSound** now reliably brings the window to the front AND gives it focus, fixing a case where screen-reader users had to Alt+Tab to actually reach the restored window.
* **Enable sending / Enable receiving** now **toggle** the state instead of always switching it on — so you can use them to turn off as well.
* The **Profiles submenu** shows your five most recent profiles (same list the File menu uses) with number-key shortcuts. While the submenu is open, press 1 for the most recent, 2 for the next, and so on. Switching from the tray works the same way as from the File menu — RemSound reloads under the new profile, your devices and peers come back as the new profile has them.

## Smaller bug fixes

* **Tray icon's first-launch tooltip read as "RemSound RemSound"** for some screen-reader users because the tooltip text matched the process name. Initial tooltip is now "RemSound — starting up", which avoids the duplicate read. Within a second the live state takes over and reads cleanly from then on.
* **Recent profiles in both the File menu and the new tray menu** no longer announce a "Recent profile N:" prefix on each item — they just read the profile name. The 1..5 number-key shortcuts still work either way.

## Compatibility

No wire format change. v3.1 talks to other v3.0.x machines (v3.0, v3.0.1, v3.0.2) exactly the same as before. Profiles created or saved on v3.1 carry the new per-cue settings and a v3.0.x build loading one will silently ignore the new fields (so the profile still works, just without the new cue-customisation state).

## Install

1. Download `RemSound-v3.1.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it will not touch your profiles, settings or recordings.
4. Run `RemSound.exe`. Press F1 for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

**v1.9 through v3.0.2:** Help → Check for updates works — it will fetch and install v3.1 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.1 installs itself shortly after launch and RemSound reopens on whichever profile you were running.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v3.1 but not apply it. Install v3.1 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
