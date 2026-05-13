using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Modal dialog shown at app startup to pick which profile to load. Listbox of saved
/// profile titles plus a synthetic "(Blank template)" entry for an unsaved-defaults
/// session. Enter or OK selects; Esc does nothing (deliberately disabled — picking is
/// required); Alt+F4 closes the dialog and exits the app; Del on a profile prompts to
/// delete it with a yes/no confirm. The user can also browse to a custom profiles
/// folder, which persists in <c>remsound.config.json</c> next to the exe.
///
/// On OK, exposes:
///   * <see cref="SelectedTitle"/> — the chosen title, or null for blank template.
///   * <see cref="SelectedProfile"/> — the loaded <see cref="Profile"/>, or null for blank.
///   * <see cref="Store"/> — the (possibly-rebuilt) profile store. If the user clicked
///     Browse and changed the folder, this points at the new folder; the caller should
///     use this reference rather than the one it passed in.
/// </summary>
internal sealed class ProfileSelectionDialog : Form
{
    private const string BlankTemplateLabel = "(Blank template)";

    private ProfileStore store;
    private readonly ListBox listBox;
    private readonly Label folderLabel;

    public string? SelectedTitle { get; private set; }
    public Profile? SelectedProfile { get; private set; }
    /// <summary>Current profile store. If the user clicked Browse during the dialog,
    /// this is rebuilt to point at the new folder; otherwise it's the same instance the
    /// caller passed in.</summary>
    public ProfileStore Store => store;

    public ProfileSelectionDialog(ProfileStore store)
    {
        this.store = store;

        Text = "RemSound — pick a profile";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(480, 420);
        // Esc is deliberately ignored (no CancelButton). Alt+F4 routes through the
        // window manager to FormClosing → DialogResult.Cancel, which the caller treats
        // as "user wants to quit".
        KeyPreview = true;

        listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Profiles",
        };
        listBox.KeyDown += OnListKeyDown;
        listBox.DoubleClick += (_, _) => Accept();

        var instructions = new Label
        {
            Text = "Select a profile and press Enter, or pick \"" + BlankTemplateLabel + "\" to start fresh.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 36,
            Padding = new Padding(8, 8, 8, 4),
        };

        // Folder status + Browse button row at the bottom. Browse opens a folder picker;
        // on OK we save AppConfig, rebuild the store, and refresh the list. Status label
        // shows the active folder so the user can verify where their profiles are coming
        // from. NVDA reads the label as a sibling of the listbox.
        folderLabel = new Label
        {
            Text = "Profiles folder: " + store.BaseDirectory,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
            Padding = new Padding(8, 4, 8, 4),
            AccessibleName = "Profiles folder: " + store.BaseDirectory,
        };

        var okButton = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.None };
        okButton.Click += (_, _) => Accept();
        var deleteButton = new Button { Text = "&Delete", AutoSize = true };
        deleteButton.Click += (_, _) => DeleteSelected();
        var browseButton = new Button { Text = "&Browse for profiles folder…", AutoSize = true };
        browseButton.Click += (_, _) => BrowseForFolder();
        var resetFolderButton = new Button { Text = "&Reset to default folder", AutoSize = true };
        resetFolderButton.Click += (_, _) => ResetToDefaultFolder();
        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 80,
            AutoSize = false,
            Padding = new Padding(8),
            WrapContents = true,
        };
        buttonRow.Controls.Add(okButton);
        buttonRow.Controls.Add(deleteButton);
        buttonRow.Controls.Add(browseButton);
        buttonRow.Controls.Add(resetFolderButton);

        Controls.Add(listBox);
        Controls.Add(buttonRow);
        Controls.Add(folderLabel);
        Controls.Add(instructions);

        AcceptButton = okButton; // makes Enter work in the form context too

        Load += (_, _) =>
        {
            RefreshList();
            listBox.Focus();
        };
    }

    private void RefreshList()
    {
        var prevSelected = listBox.SelectedItem as string;
        listBox.BeginUpdate();
        listBox.Items.Clear();
        listBox.Items.Add(BlankTemplateLabel);
        foreach (var t in store.ListProfileTitles())
        {
            listBox.Items.Add(t);
        }
        // Try to restore selection; fall back to first item.
        var idx = prevSelected is null ? 0 : Math.Max(0, listBox.Items.IndexOf(prevSelected));
        listBox.SelectedIndex = Math.Min(idx, listBox.Items.Count - 1);
        listBox.EndUpdate();
        // Keep the folder label in sync so it always reflects what the listbox is reading.
        folderLabel.Text = "Profiles folder: " + store.BaseDirectory;
        folderLabel.AccessibleName = folderLabel.Text;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            Accept();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            DeleteSelected();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void Accept()
    {
        var selected = listBox.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)) return;
        if (selected == BlankTemplateLabel)
        {
            SelectedTitle = null;
            SelectedProfile = null;
        }
        else
        {
            SelectedTitle = selected;
            SelectedProfile = store.Load(selected);
            if (SelectedProfile is null)
            {
                MessageBox.Show(this,
                    $"Could not read profile \"{selected}\". Treating as blank template.",
                    "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SelectedTitle = null;
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void DeleteSelected()
    {
        var selected = listBox.SelectedItem as string;
        if (string.IsNullOrEmpty(selected) || selected == BlankTemplateLabel) return;
        var result = MessageBox.Show(this,
            $"Delete profile \"{selected}\"? This cannot be undone.",
            "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;
        if (!store.Delete(selected))
        {
            MessageBox.Show(this,
                $"Could not delete \"{selected}\".",
                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshList();
    }

    /// <summary>Open a folder picker, persist the choice to AppConfig, and rebuild the
    /// profile store + list against the new folder. No-op on cancel. If the new folder
    /// has no profiles yet, the listbox simply shows just the blank-template entry; the
    /// user can save into the new folder later.</summary>
    private void BrowseForFolder()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose a folder for RemSound profiles",
            UseDescriptionForTitle = true,
            SelectedPath = store.BaseDirectory,
            ShowNewFolderButton = true,
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        ApplyFolder(picker.SelectedPath);
    }

    private void ResetToDefaultFolder()
    {
        // Clearing the AppConfig field and reloading swings the store back to the legacy
        // default (per-machine subfolder under the exe). Cheap and reversible — user can
        // Browse to a custom folder again any time.
        var cfg = AppConfig.Load();
        cfg.ProfilesDirectory = null;
        try { cfg.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save app config: {ex.Message}",
                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        store = cfg.CreateStore();
        RefreshList();
    }

    private void ApplyFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var cfg = AppConfig.Load();
        cfg.ProfilesDirectory = folderPath;
        try { cfg.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save app config: {ex.Message}",
                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        store = cfg.CreateStore();
        RefreshList();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Eat plain Esc — selection is required, no implicit cancel.
        if (keyData == Keys.Escape) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
