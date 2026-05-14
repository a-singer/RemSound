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
