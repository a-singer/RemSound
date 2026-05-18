using System.Reflection;

namespace RemSound.App;

/// <summary>
/// About dialog. Shows the running version, a short blurb about RemSound and the latest
/// release notes (built in below — bumped per release alongside the project's
/// <see cref="System.Version"/> property).
///
/// Layout follows the same NVDA-friendly conventions the rest of the app uses: a small
/// modal dialog with a heading label, a read-only multi-line text box for the notes that
/// the user can tab into and arrow through, and a Close button as the AcceptButton /
/// CancelButton. Escape dismisses.
/// </summary>
internal sealed class AboutDialog : Form
{
    /// <summary>Markdown-ish release notes shown in the About box's scrolling text area.
    /// Bumped per release. Keep it short — the canonical release notes also live on the
    /// GitHub Releases page, which the user can reach via the Help menu's "Check for
    /// updates" path.</summary>
    private const string ReleaseNotes =
        """
        RemSound v1.7

        Updater fix. No wire-format or audio-pipeline changes —
        v1.5, v1.6 and v1.7 peers interoperate.

        Bug fix:
          * Check for updates now reliably finds RemSound
            releases. RemSound and the relay server share one
            GitHub repository; the server's releases use
            "server-" tags. The updater previously looked only
            at the single newest release of any kind, so a
            server release could derail it (it would misread
            the version and report "up to date"). It now scans
            the release list and considers only RemSound client
            versions, ignoring server releases, drafts and
            pre-releases.

        RemSound v1.6

        Three reliability fixes. No wire-format or audio-pipeline
        changes — v1.5 and v1.6 peers interoperate.

        Bug fixes:
          * Peer address recovery. If the address you connected
            to goes unreachable — a peer rebooted onto a new IP,
            or a computer name resolved to a stale address —
            RemSound now follows the peer to the live address it
            is heartbeating from, instead of sending audio into
            the void. Recovers on its own within a few seconds.
            Limited to private-network addresses so a relay can
            never be mistaken for a moved peer.
          * Fixed a crash that could happen when a peer
            reconnected (e.g. after rebooting). The Connectivity
            peer list could be read mid-rebuild with a stale
            index and bring the app down from the status timer.
          * Fixed runaway memory and CPU on a long-running
            receiver. Decoder sessions orphaned by peer
            reconnects were not being reclaimed — over hours they
            piled up, each holding a multi-megabyte buffer and
            costing render-thread time every callback. They are
            now reaped once idle, with a hard cap as a backstop.

        RemSound v1.5

        Menu reorganisation, multi-peer audio-routing fix, recording
        fix for BothIndependent mode, and Ctrl+O for Open profile.
        Wire format and audio pipeline are unchanged — v1.4 and v1.5
        peers interoperate.

        Bug fixes:
          * BothIndependent recording: when both WASAPI and ASIO
            output devices were ticked, recordings came out garbled
            and twice the expected duration. The recorder taps fired
            from both lanes' render reads and the writer thread
            appended both streams into one ring as if they were
            sequential audio. Recorder now has per-lane rings and
            mixes them in the writer thread.
          * A peer announcing audio on the WASAPI lane was inaudible
            when the receiver only had an ASIO output device ticked
            (and vice versa). Sessions whose announced lane has no
            active output now fall through to whichever lane IS
            being read.

        Menu reorganisation:
          * New Options menu (Alt+O) holds: Recording settings,
            Keyboard shortcuts, Startup behaviour, Preferences.
            These used to be scattered across File menu (Keyboard
            shortcuts, Preferences), Record menu (Recording settings),
            and inside the Preferences dialog (Startup behaviour).
          * Record menu mnemonic moved from Alt+O to Alt+K (rendered
            as "Record (Alt+K)") so Alt+O could go to Options. K is
            unusual for "Record" but the Record menu doesn't have a
            natural free letter — Alt+R is taken by Receive audio.
          * File menu: new Recent profiles submenu (Alt+F, R). Lists
            the last five profiles you've opened, most-recent first.
            Press 1..5 while the submenu is open to jump to a slot.
            File → Rename current profile moves to Alt+F, M, and
            Minimise to tray moves to Alt+F, N, to free up R for the
            new submenu.
          * Lock to audio clock (Audio profile tab) was Alt+K; now
            Alt+D (the D in "audio") since the Record menu won Alt+K.

        UX additions:
          * Ctrl+O opens the Open profile dialog (matches the menu
            chord). Previously the menu had no global shortcut.
          * New global hotkey: Start / Stop recording. Pickable from
            Options - Keyboard shortcuts. Unbound by default. Works
            system-wide — RemSound doesn't need keyboard focus.

        Recording feature unchanged in this release — the dialog
        layout, formats (WAV / MP3 / OGG-Opus / FLAC), and tap-points
        all the same as v1.4.

        RemSound v1.4

        Recording-settings dialog cleanup and a few mnemonic
        adjustments. No wire-format or audio-pipeline changes — v1.4
        and v1.3 peers interoperate.

        Recording settings dialog:
          * Channel mode is now its own dedicated listbox (Alt+C)
            instead of being folded into every attribute row. WAV
            shrinks from 6 attribute rows to 3, MP3 and OGG-Opus from
            8 to 4, FLAC from 4 to 2.
          * FLAC compression level (0..8) is now selectable in its
            own listbox (Alt+L). Previously hard-fixed at the libFLAC
            default of 5. The list is shown only when the file format
            is FLAC. Friendly tags on the endpoints: "0 — fastest
            encode, biggest file", "5 — default (libFLAC reference)",
            "8 — slowest encode, smallest file". All levels produce
            bit-identical audio — pure encode-time vs file-size
            tradeoff.

        Record menu mnemonics:
          * Start / Stop recording is now Alt+O, R (was Alt+O, S).
            Matches the Ctrl+R global toggle so the same letter does
            the same job from either entry point.
          * Recording settings is now Alt+O, S (was Alt+O, T). Reads
            more naturally now that the R slot is freed.
          * Open folder (O) and Change folder (C) unchanged.

        Dialog mnemonic adjustment:
          * Cancel button in the Recording settings dialog moved
            from Alt+C to Alt+N so the Channels listbox can take
            Alt+C. Esc still dismisses the dialog the way it always
            has.

        RemSound v1.3

        Updater hardening. The v1.2 self-updater silently failed on
        installs inside Dropbox-synced folders because Dropbox held
        write locks on the existing files during the brief window
        between the parent exiting and the helper script copying the
        new files in. Robocopy gave up after 5 seconds and the helper
        relaunched the old binary anyway — so the user saw the same
        version they started with after pressing "Yes" on the install
        prompt.

        What's new:
          * Helper script retries robocopy for up to 60 seconds per
            file (was 5). Dropbox lock release happens reliably within
            that window in practice.
          * Helper now checks robocopy's exit code. On a true failure
            (exit code 8 or higher), it writes update-failed.txt next
            to RemSound.exe with diagnostic detail and does NOT
            relaunch the old binary. Earlier versions silently
            relaunched the unmodified old binary, hiding the failure.
          * Helper writes a step-by-step log to _update-helper.log in
            the install folder. If a future update goes wrong this is
            where the trace lives.
          * Stale failure markers are cleared at the start of every
            new update attempt, so a successful run leaves the install
            folder clean.

        Wire format, audio pipeline, and recording feature are
        unchanged from v1.2.

        RemSound v1.2

        Recording, sound cues, and receiver-side drift compensation.
        This release is mostly about features that sit on top of the
        v1.1 transport — the wire format and audio pipeline are
        unchanged, so v1.1 and v1.2 peers interoperate.

        What's new:
          * Recording. New Record menu (Alt+O) — Start / Stop with
            Ctrl+R, dedicated settings dialog, per-profile choice of
            source (received only, sent only, or both), file format
            (WAV, MP3, OGG-Opus, FLAC), bit depth or bitrate, mono or
            stereo, and recordings folder. Files are crash-resilient:
            WAV re-patches its RIFF header every 5 seconds, MP3 / FLAC
            / OGG-Opus all produce well-formed truncated files if the
            app crashes mid-recording.
          * OGG-Opus and FLAC encoders now wired up — they were stubs
            in earlier builds. OGG-Opus reuses the same Concentus
            encoder as the wire path; FLAC uses pure-managed CUETools
            FLAKE (no native DLL).
          * Recording start / stop sound cues. Plays a short ding
            when recording transitions on or off. Played via the
            default Windows output device, separate from the
            recording pipeline, so a normal recording does not include
            the cue.
          * Sound-cue Preferences. The old single "Mute connect /
            disconnect sounds" checkbox is replaced by a per-cue
            CheckedListBox: Connect / Disconnect / Recording start /
            Recording stop, each independently toggleable. Old profile
            settings that had the legacy mute on are honoured on first
            load.
          * Receiver-side drift compensation switched from discrete
            single-frame splices to a continuous WdlResampler running
            at a slowly-updated rate ratio. Smooths out long-session
            clock drift between sender and receiver without the
            occasional 21 µs splice the v1.1 corrector emitted.

        UI changes:
          * Record menu moved to Alt+O (Rec&ord). The old Alt+R chord
            conflicted with the Receive audio checkbox on the main
            form. Inside the menu the item mnemonics are unchanged
            (S / T / O / C for Start, settings, Open folder, Change
            folder).
          * Auto-tune interval combo label and accessible name are now
            mode-aware. In BothIndependent mode it reads "Auto-tune
            interval — WASAPI and ASIO" so it's clear the same combo
            drives ticks for both lanes; each lane still independently
            tunes to its own target latency. Earlier builds also had
            a bug where ticking ASIO auto-tune alone left this combo
            greyed out — fixed.

        Diagnostics (only active with Enable logs ticked):
          * Per-stage discontinuity probes — sender raw capture,
            sender pre-encode (now per lane in BothIndependent),
            receiver post-decode, receiver post-ring, receiver
            post-resampler. Lets a log inspection localise where a
            click was introduced (capture / wire / decode / playout).
          * Wire-level sequence tracking on each PCM stream:
            in-order / missed / reordered / duplicated packet counts
            in the diag log. Healthy LAN should show all-zero except
            in-order; non-zero on the others points to transport
            issues rather than software.
          * Clipped-sample delta in the diag log.

        Bug fixes:
          * Auto-tune interval combo no longer greys out when only
            ASIO auto-tune is ticked in BothIndependent.

        RemSound v1.1

        Priority and performance hardening, plus always-on network
        packet priority. Aimed at the cold-start latency jitter you
        sometimes hear when the machine has been idle.

        What's new:
          * Use CPU and Windows performance settings in high priority
            mode (Audio profile tab, Alt+U). Off by default; opt in per
            profile. Combines eight Windows power and scheduling levers
            so the OS can't decide we're idle and downclock: process
            power-throttling off (including timer-resolution honouring),
            an execution-required power request, scheduler quantum
            pinned to 1 ms, process priority raised to High, working-set
            minimum locked, memory priority normalised, and
            SetThreadExecutionState set to keep the system awake. Fully
            reversed on toggle-off and on app exit. Costs battery on
            laptops — that's why it's off by default.
          * Network packet priority — always on, no toggle. The
            outbound UDP socket is attached to Windows' built-in qWAVE
            service at Voice priority (DSCP 46, WMM Voice on Wi-Fi). On
            a busy Wi-Fi access point, RemSound's packets win
            medium-contention against other clients; on wired LAN,
            switches that honour DSCP give us scheduling priority.
            Neutral across the public internet (most ISPs strip DSCP at
            the edge). Silent fallback if the qWAVE service isn't
            available on stripped-down Windows images.
          * Sender and receiver UDP kernel buffers bumped to 1 MB each
            way — enough to absorb roughly 30 ms of GC or scheduler
            stall without dropping datagrams.
          * MMCSS audio-thread priority on the network listener, mix
            loop, and render thread bumped from High to Critical
            (always on, no toggle).

        Bug fixes:
          * Toggling "Enable logs" in Preferences no longer marks the
            current profile as dirty. Logs are a machine-local setting,
            so the spurious flag was triggering the "unsaved changes?"
            prompt on exit after just toggling logs.

        RemSound v1.0

        Initial public release.

        Highlights:
          * Low-latency peer-to-peer audio over UDP. WASAPI for any Windows audio device,
            and a parallel ASIO lane for pro audio interfaces (Audient, Komplete Audio,
            Focusrite, RME). Each lane keeps its own native callback latency.
          * Pick an ASIO driver from the dropdown at the top of the Audio inputs and outputs
            tab to bring ASIO into the pipeline; select "(none)" to run WASAPI-only.
          * Profile system. Save your entire setup — device ticks, peers, codec, latency
            targets, hotkeys, ASIO driver choice — into a JSON file. Pick which profile to
            load at every launch.
          * Continuous auto-tune on either lane. Watches receive jitter and nudges the
            latency target up or down to stay click-free without forcing you to overshoot.
          * Opus inband FEC. Single-packet losses recover transparently in both Opus modes;
            you don't hear them at all. PCM is also available for clean LAN connections.
          * Remote control. Configurable global hotkeys can nudge a peer's RemSound volume
            or their Windows default-output-device master volume, opt-in on the receiver.
          * Built-in self-updater. Optionally polls GitHub for newer releases on a schedule
            you set; can install them silently if you want.

        See the user manual (Help menu, or F1 from anywhere in the app) for full details
        on every control, the keyboard shortcuts, and the troubleshooting guide.
        """;

    public AboutDialog()
    {
        Text = "About RemSound";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(560, 420);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        var headingLabel = new Label
        {
            Text = $"RemSound version {version}",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold),
            AccessibleName = $"RemSound version {version}",
        };

        var notesBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            TabStop = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Text = ReleaseNotes,
            AccessibleName = "Release notes (tab into and arrow to read)",
        };

        var closeButton = new Button
        {
            Text = "Close",
            AutoSize = true,
            DialogResult = DialogResult.OK,
            TabIndex = 1,
        };
        closeButton.Click += (_, _) => Close();
        notesBox.TabIndex = 0;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        buttons.Controls.Add(closeButton);

        root.Controls.Add(headingLabel, 0, 0);
        root.Controls.Add(notesBox, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        AcceptButton = closeButton;
        CancelButton = closeButton;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        };
    }
}
