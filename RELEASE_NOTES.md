# RemSound v3.3

The big one: **your audio is now encrypted end to end.** Plus the connect/disconnect cues are fixed, and a handful of smaller reliability improvements.

## Your audio is now encrypted

Everything RemSound sends is now scrambled as it leaves your computer and only unscrambled at the other end — so nobody in between can listen in, and you no longer need a VPN just to keep a private connection private.

It works with a password on each profile:

- **You and the person you connect to use the same password** → you hear each other.
- **Different passwords** → no audio passes, and RemSound tells you so plainly instead of leaving you with silent confusion.

Setting a password is easy: RemSound asks for one when you create a profile, you can change it any time with **File → Change this profile's password**, and you can see and edit the passwords for all your profiles in one place under **Options → Profile passwords**. If you start sending or receiving on a profile with no password, RemSound asks you to set one first. The encryption adds no delay you could ever notice.

### Important: everyone needs v3.3

Because the audio format changed to carry the encryption, **a v3.3 copy can only talk to other v3.3 (and later) copies.** Anyone you connect with needs to update to v3.3 too. If you try to connect to an older copy, RemSound will tell you they need to update.

## Connect and disconnect cues, fixed

- **Cues now play reliably, whatever the WAV format.** The old sound player couldn't handle high-resolution (24-bit / 96 kHz) files and would play them only sometimes — so cues, and the Preview button, were hit-and-miss. They now play every time, including any custom sound you supply.
- **The connect/disconnect cues now follow the actual audio**, not just the background "are you there?" heartbeat. So you won't hear a false "disconnect" while the sound is still playing, and the connect ding lands when the audio actually starts.

## Smaller improvements

- **No more crackle from address-hopping.** On setups where a peer is reachable two ways at once (e.g. a VPN and a local network), RemSound now sticks to the address that's actually working instead of flip-flopping between them.
- **Honest "online/offline".** A peer is no longer shown "offline" just because a discovery beacon blinked — only when it's genuinely gone by every measure.
- **See what's new after an update.** RemSound now opens its About box once after each update so you can see what changed. On by default; turn it off in Preferences.

## Install

1. Download `RemSound-v3.3.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your profiles, settings or recordings.
4. Run `RemSound.exe`.

After updating, set a password on the profile(s) you use to connect (RemSound will prompt you), and make sure the people you connect with have also updated to v3.3 and are using the same password.

## Upgrading

**v1.9 through v3.2:** Help → Check for updates works — it will fetch and install v3.3 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.3 installs itself shortly after launch.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it installing updates, so Check for updates will download v3.3 but not apply it. Install v3.3 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
