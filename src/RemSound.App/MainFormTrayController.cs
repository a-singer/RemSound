using System.Runtime.InteropServices;

namespace RemSound.App;

/// <summary>
/// Owns the system-tray icon RemSound shows while it's minimised. The icon's right-click
/// menu provides quick access to the most common actions without restoring the window —
/// useful for users who park RemSound in the tray and never look at the main window again.
///
/// Menu (2026-05-28 rewrite — the original four-item menu was just Show / Sending /
/// Receiving / Exit, with no recent-profiles entry and no live state on the checkable items):
///
///   * Show RemSound (Alt+W)         — restore the main window
///   * Enable sending (Alt+S)        — checkable, reflects sendMyAudioCheckbox state
///   * Enable receiving (Alt+R)      — checkable, reflects receiveAudioCheckbox state
///   * Profiles (Alt+P)              — submenu of AppConfig.RecentProfiles, picking one
///                                     switches the active profile
///   * Exit (Alt+X)                  — close the app
///
/// Tooltip (NotifyIcon.Text): set dynamically from MainForm's snapshot tick via
/// <see cref="SetTooltip"/>. Default is "RemSound" until the first refresh. Capped at
/// 127 characters because Windows truncates anything beyond that.
/// </summary>
internal sealed class MainFormTrayController : IDisposable
{
    /// <summary>Maximum NotifyIcon.Text length on Windows 10+. Windows truncates anything
    /// longer; truncating ourselves means the tooltip ends with our own "..." rather than
    /// being chopped mid-word.</summary>
    private const int MaxTooltipLength = 127;

    private readonly Form owner;
    private readonly NotifyIcon trayIcon = new();

    private readonly Func<bool> getSending;
    private readonly Action toggleSending;
    private readonly Func<bool> getReceiving;
    private readonly Action toggleReceiving;
    private readonly Func<IReadOnlyList<string>> getRecentProfilePaths;
    private readonly Action<string> switchToProfile;
    private readonly Action exit;

    private readonly ToolStripMenuItem sendingItem;
    private readonly ToolStripMenuItem receivingItem;
    private readonly ToolStripMenuItem profilesItem;

    public MainFormTrayController(
        Form owner,
        Func<bool> getSending,
        Action toggleSending,
        Func<bool> getReceiving,
        Action toggleReceiving,
        Func<IReadOnlyList<string>> getRecentProfilePaths,
        Action<string> switchToProfile,
        Action exit)
    {
        this.owner = owner;
        this.getSending = getSending;
        this.toggleSending = toggleSending;
        this.getReceiving = getReceiving;
        this.toggleReceiving = toggleReceiving;
        this.getRecentProfilePaths = getRecentProfilePaths;
        this.switchToProfile = switchToProfile;
        this.exit = exit;

        // Initial tooltip text — deliberately NOT just "RemSound" because some screen
        // readers (NVDA in particular) read tray icons as "<process name>, <tooltip>",
        // which with a single-word "RemSound" tooltip on a "RemSound" process renders as
        // "RemSound RemSound" until the first snapshot tick (~1s after launch) overwrites
        // it. Picking a sensible startup-state string avoids the duplicate read entirely;
        // the snapshot tick refreshes this with live peer / send / receive info from then
        // on.
        trayIcon.Text = "RemSound — starting up";
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Visible = false;
        trayIcon.DoubleClick += (_, _) => Restore();

        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("Sho&w RemSound")
        {
            AccessibleName = "Show RemSound",
        };
        showItem.Click += (_, _) => Restore();

        sendingItem = new ToolStripMenuItem("Enable &sending")
        {
            CheckOnClick = false, // we set Checked manually in RefreshMenuState; toggle drives the actual app state via the callback
            AccessibleName = "Enable sending",
        };
        sendingItem.Click += (_, _) => toggleSending();

        receivingItem = new ToolStripMenuItem("Enable &receiving")
        {
            CheckOnClick = false,
            AccessibleName = "Enable receiving",
        };
        receivingItem.Click += (_, _) => toggleReceiving();

        profilesItem = new ToolStripMenuItem("&Profiles")
        {
            AccessibleName = "Profiles",
        };
        // Populate ONCE at construction so WinForms recognises this as a real submenu
        // (an empty DropDownItems collection means the framework treats the item as a
        // plain command, never opens the submenu, and DropDownOpening never fires —
        // which produced "Profiles does nothing" in the first cut of this controller).
        // After that, every DropDownOpening rebuilds the items so a profile loaded since
        // the last menu open shows up immediately.
        RebuildProfilesSubmenu();
        profilesItem.DropDownOpening += (_, _) => RebuildProfilesSubmenu();

        var exitItem = new ToolStripMenuItem("E&xit")
        {
            AccessibleName = "Exit RemSound",
        };
        exitItem.Click += (_, _) => exit();

        menu.Items.Add(showItem);
        menu.Items.Add(sendingItem);
        menu.Items.Add(receivingItem);
        menu.Items.Add(profilesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // Refresh the checkable items' state every time the menu opens so the visible
        // ticks match the current main-window state (which can have changed while the
        // user was clicking around elsewhere).
        menu.Opening += (_, _) => RefreshMenuState();

        trayIcon.ContextMenuStrip = menu;
    }

    /// <summary>Replace the tooltip the OS shows over the tray icon. Called from MainForm's
    /// 1 Hz snapshot tick to keep the text current with peer count + send/receive state.
    /// Truncated to the Windows 10+ limit (127 chars) — anything longer is silently chopped
    /// by the shell, so chopping ourselves keeps the truncation point visible.</summary>
    public void SetTooltip(string text)
    {
        // Same anti-duplicate rule as the ctor: avoid a single-word "RemSound" tooltip on
        // the "RemSound" process, since some screen readers render that as "RemSound RemSound".
        if (string.IsNullOrEmpty(text)) text = "RemSound — running";
        if (text.Length > MaxTooltipLength)
        {
            text = text[..(MaxTooltipLength - 1)] + "…";
        }
        // NotifyIcon.Text throws on the same string-assigning path under some shell
        // conditions (rare race during a session-end). Best-effort: swallow.
        try { trayIcon.Text = text; } catch { /* harmless */ }
    }

    public void Toggle()
    {
        if (owner.Visible && owner.WindowState != FormWindowState.Minimized) Minimize();
        else Restore();
    }

    public void Restore()
    {
        owner.Show();
        if (owner.WindowState == FormWindowState.Minimized)
        {
            owner.WindowState = FormWindowState.Normal;
        }
        owner.BringToFront();
        owner.Activate();
        // WinForms' Activate() is best-effort: Windows' foreground-lock feature blocks
        // arbitrary processes from stealing focus, and Activate() doesn't always win even
        // for a process that's clearly user-initiated. Calling SetForegroundWindow directly
        // bypasses the lock because the caller (a tray-menu click handler) is on a UI
        // thread that received recent user input — which Windows recognises as the
        // legitimate "user asked for this" case. Without this fix, "Show RemSound" puts
        // the window on screen but doesn't focus it, leaving NVDA users having to Alt+Tab
        // to actually hear the new content.
        try { SetForegroundWindow(owner.Handle); } catch { /* harmless — Restore still mostly worked */ }
        trayIcon.Visible = false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void Minimize()
    {
        owner.Hide();
        trayIcon.Visible = true;
    }

    public void Dispose() => trayIcon.Dispose();

    private void RefreshMenuState()
    {
        // Live state of the two togglable items — read on demand from MainForm so a checkbox
        // change made via the main window or a global hotkey is reflected in the tray menu
        // the next time the user opens it.
        try { sendingItem.Checked = getSending(); } catch { sendingItem.Checked = false; }
        try { receivingItem.Checked = getReceiving(); } catch { receivingItem.Checked = false; }
    }

    private void RebuildProfilesSubmenu()
    {
        profilesItem.DropDownItems.Clear();
        IReadOnlyList<string> paths;
        try { paths = getRecentProfilePaths(); }
        catch { paths = Array.Empty<string>(); }

        var slot = 1;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path)) continue; // skip missing files; the AppConfig list keeps the entry in case it reappears
            var title = Path.GetFileNameWithoutExtension(path);
            // Mnemonic prefix matches the File menu's Recent profiles submenu (&1..&5) so
            // muscle-memory between the in-app menu and the tray menu carries over. The
            // visible Text carries the number; AccessibleName is just the profile name so
            // NVDA reads "MyProfile, menu item, one of five" rather than the noisier
            // "Recent profile 1: MyProfile" that the original code was reading out.
            var item = new ToolStripMenuItem($"&{slot} {title}")
            {
                AccessibleName = title,
                Tag = path,
            };
            item.Click += (s, _) =>
            {
                var sender = (ToolStripMenuItem)s!;
                var profilePath = (string)sender.Tag!;
                try { switchToProfile(profilePath); }
                catch { /* the switch path surfaces its own errors via MainForm */ }
            };
            profilesItem.DropDownItems.Add(item);
            slot++;
        }

        if (profilesItem.DropDownItems.Count == 0)
        {
            profilesItem.DropDownItems.Add(new ToolStripMenuItem("(No recent profiles)")
            {
                Enabled = false,
                AccessibleName = "No recent profiles",
            });
        }
    }
}
