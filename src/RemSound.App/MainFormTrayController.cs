namespace RemSound.App;

internal sealed class MainFormTrayController : IDisposable
{
    private readonly Form owner;
    private readonly NotifyIcon trayIcon = new();

    public MainFormTrayController(Form owner, Action enableSending, Action enableReceiving, Action exit)
    {
        this.owner = owner;
        trayIcon.Text = "RemSound";
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Visible = false;
        trayIcon.DoubleClick += (_, _) => Restore();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => Restore());
        menu.Items.Add("Enable sending", null, (_, _) => enableSending());
        menu.Items.Add("Enable receiving", null, (_, _) => enableReceiving());
        menu.Items.Add("Exit", null, (_, _) => exit());
        trayIcon.ContextMenuStrip = menu;
    }

    public void Toggle()
    {
        if (owner.Visible && owner.WindowState != FormWindowState.Minimized) Minimize();
        else Restore();
    }

    public void Restore()
    {
        owner.Show();
        owner.WindowState = FormWindowState.Normal;
        owner.Activate();
        trayIcon.Visible = false;
    }

    public void Minimize()
    {
        owner.Hide();
        trayIcon.Visible = true;
    }

    public void Dispose() => trayIcon.Dispose();
}
