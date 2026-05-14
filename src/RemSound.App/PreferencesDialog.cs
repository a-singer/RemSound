using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Preferences dialog. Holds the three settings that used to live on the (now-removed)
/// Profiles and preferences tab and aren't profile-management actions in their own right:
///   * Mute connect/disconnect sounds — the small ding on peer state changes.
///   * Accept remote volume commands from peers — opt-in for the remote-control feature.
///   * Startup behaviour — opens the existing <see cref="StartupBehaviourDialog"/> sub-dialog.
///
/// Both checkboxes save through <see cref="RemSoundSettingsStore"/> on every change (so
/// the user doesn't need to re-confirm via an OK button). The Startup behaviour button
/// just opens the existing modal sub-dialog. Esc or the Close button dismisses.
///
/// Reachable via the File → Preferences menu item or Ctrl+P from the main window.
/// </summary>
internal sealed class PreferencesDialog : Form
{
    private readonly Button browseProfilesFolderButton = new()
    {
        Text = "&Browse for RemSound profiles folder...",
        AccessibleName = "Browse for RemSound profiles folder",
        AutoSize = true,
    };

    private readonly AccessibleCheckBox muteCuesBox = new()
    {
        Text = "Mute connect/disconnect sounds (Alt+&M)",
        AccessibleName = "Mute connect/disconnect sounds",
        AutoSize = true,
    };

    private readonly AccessibleCheckBox acceptRemoteVolumeBox = new()
    {
        Text = "Accept remote volume commands from peers (Alt+&A)",
        AccessibleName = "Accept remote volume commands from peers",
        AutoSize = true,
    };

    private readonly Button startupBehaviourButton = new()
    {
        Text = "Startup behaviour... (Alt+&S)",
        AccessibleName = "Startup behaviour",
        AutoSize = true,
    };

    // Update settings — frequency dropdown, manual check button, silent-install checkbox.
    // Sits above the logging row so users meet it during setup; the canonical order in the
    // dialog is "things related to the program staying current" before "things related to
    // diagnosing how it's running".
    private readonly Label updateFrequencyLabel = new()
    {
        Text = "Check for updates (Alt+&U):",
        AccessibleName = "Check for updates frequency",
        AutoSize = true,
    };

    private readonly ComboBox updateFrequencyBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 200,
        AccessibleName = "Check for updates (Alt+U)",
    };

    private readonly Button checkForUpdatesNowButton = new()
    {
        Text = "Check for updates &now",
        AccessibleName = "Check for updates now",
        AutoSize = true,
    };

    private readonly AccessibleCheckBox silentlyInstallUpdatesBox = new()
    {
        Text = "Silently &install updates when available",
        AccessibleName = "Silently install updates when available",
        AutoSize = true,
    };

    private readonly AccessibleCheckBox loggingBox = new()
    {
        Text = "Enable &logs",
        AccessibleName = "Enable logs",
        AutoSize = true,
    };

    private readonly Button writeLogsNowButton = new()
    {
        Text = "&Write logs now",
        AccessibleName = "Write logs now",
        AutoSize = true,
    };

    private readonly Button closeButton = new()
    {
        Text = "Close",
        AutoSize = true,
        DialogResult = DialogResult.OK,
    };

    /// <summary>True if the user toggled Mute cues or Accept remote during this dialog
    /// session. The owner uses this to know whether to MarkProfileDirty after the dialog
    /// closes (since both settings live on Profile and need to flag a save-pending state).</summary>
    public bool ChangedAnyProfileSetting { get; private set; }

    public PreferencesDialog(
        RemSoundSettingsStore settings,
        ProfileStore? profileStore,
        Func<bool> getLoggingEnabled,
        Action<bool> applyLoggingEnabled,
        Action writeLogsNow,
        Action checkForUpdatesNow,
        Action onUpdateFrequencyChanged)
    {
        Text = "Preferences";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(560, 440);

        // 1st row — Browse for profiles folder. Same FolderBrowserDialog the startup
        // ProfileSelectionDialog uses; the choice is persisted to AppConfig.ProfilesDirectory
        // and applied on next launch (mid-session reload would force a re-pick of profile
        // which is more disruption than the change is worth — users restart RemSound when
        // they want to switch folders).
        browseProfilesFolderButton.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Choose a folder for RemSound profiles",
                UseDescriptionForTitle = true,
                SelectedPath = profileStore?.BaseDirectory ?? AppContext.BaseDirectory,
                ShowNewFolderButton = true,
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            if (string.IsNullOrWhiteSpace(picker.SelectedPath)) return;
            var cfg = AppConfig.Load();
            cfg.ProfilesDirectory = picker.SelectedPath;
            try
            {
                cfg.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save app config: {ex.Message}",
                    "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            MessageBox.Show(this,
                $"Profiles folder updated to:\n\n{picker.SelectedPath}\n\nThe new folder will be used next time RemSound launches.",
                "Profiles folder updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        muteCuesBox.Checked = settings.LoadMuteConnectionCues();
        muteCuesBox.CheckedChanged += (_, _) =>
        {
            settings.SaveMuteConnectionCues(muteCuesBox.Checked);
            ChangedAnyProfileSetting = true;
        };

        acceptRemoteVolumeBox.Checked = settings.LoadAcceptRemoteVolumeCommands();
        acceptRemoteVolumeBox.CheckedChanged += (_, _) =>
        {
            settings.SaveAcceptRemoteVolumeCommands(acceptRemoteVolumeBox.Checked);
            ChangedAnyProfileSetting = true;
        };

        startupBehaviourButton.Click += (_, _) =>
        {
            using var dialog = new StartupBehaviourDialog(profileStore);
            dialog.ShowDialog(this);
            // Startup behaviour persists through AppConfig + registry directly, so we
            // don't need to flag profile-dirty for that.
        };

        // Update settings — wired against AppConfig directly since they're machine-local.
        // The frequency combo's index maps 1:1 to the UpdateCheckFrequency enum so reordering
        // either side stays in lockstep.
        updateFrequencyBox.Items.AddRange(new object[] { "Never", "Every hour", "Every 6 hours", "Every 24 hours" });
        var cfgForLoad = AppConfig.Load();
        updateFrequencyBox.SelectedIndex = (int)cfgForLoad.UpdateCheckFrequency;
        silentlyInstallUpdatesBox.Checked = cfgForLoad.SilentlyInstallUpdates;
        updateFrequencyBox.SelectedIndexChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.UpdateCheckFrequency = (UpdateCheckFrequency)updateFrequencyBox.SelectedIndex;
            try { cfg.Save(); } catch { /* harmless — choice just won't survive a restart */ }
            onUpdateFrequencyChanged();
        };
        silentlyInstallUpdatesBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.SilentlyInstallUpdates = silentlyInstallUpdatesBox.Checked;
            try { cfg.Save(); } catch { /* harmless */ }
        };
        checkForUpdatesNowButton.Click += (_, _) => checkForUpdatesNow();

        loggingBox.Checked = getLoggingEnabled();
        loggingBox.CheckedChanged += (_, _) =>
        {
            // Machine-local setting (AppConfig.LoggingEnabled). applyLoggingEnabled writes
            // through immediately and flips the live gate, so closing the dialog needs no
            // further action. NOT a profile setting — do NOT touch ChangedAnyProfileSetting
            // or we'll trigger a spurious "save profile?" prompt on exit when the user
            // toggled nothing else.
            applyLoggingEnabled(loggingBox.Checked);
        };

        writeLogsNowButton.Click += (_, _) => writeLogsNow();

        closeButton.Click += (_, _) => Close();

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 10,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Tab order top-to-bottom: browse, mute cues, accept remote, startup, update
        // frequency, check-now, silent install, enable logs, write logs now, close. Updates
        // sit above the log row so a user setting up the app meets them first.
        browseProfilesFolderButton.TabIndex = 0;
        muteCuesBox.TabIndex = 1;
        acceptRemoteVolumeBox.TabIndex = 2;
        startupBehaviourButton.TabIndex = 3;
        updateFrequencyBox.TabIndex = 4;
        checkForUpdatesNowButton.TabIndex = 5;
        silentlyInstallUpdatesBox.TabIndex = 6;
        loggingBox.TabIndex = 7;
        writeLogsNowButton.TabIndex = 8;
        closeButton.TabIndex = 9;

        // Group the frequency label + combo on one FlowLayoutPanel row so the visible label
        // sits inline next to the combo while keeping the combo as the focusable target.
        var freqRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
        };
        updateFrequencyLabel.Padding = new Padding(0, 6, 8, 0);
        freqRow.Controls.Add(updateFrequencyLabel);
        freqRow.Controls.Add(updateFrequencyBox);

        panel.Controls.Add(browseProfilesFolderButton, 0, 0);
        panel.Controls.Add(muteCuesBox, 0, 1);
        panel.Controls.Add(acceptRemoteVolumeBox, 0, 2);
        panel.Controls.Add(startupBehaviourButton, 0, 3);
        panel.Controls.Add(freqRow, 0, 4);
        panel.Controls.Add(checkForUpdatesNowButton, 0, 5);
        panel.Controls.Add(silentlyInstallUpdatesBox, 0, 6);
        panel.Controls.Add(loggingBox, 0, 7);
        panel.Controls.Add(writeLogsNowButton, 0, 8);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 0, 12, 12),
        };
        buttons.Controls.Add(closeButton);

        Controls.Add(panel);
        Controls.Add(buttons);

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
