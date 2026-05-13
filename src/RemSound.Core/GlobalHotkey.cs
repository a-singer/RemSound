using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemSound.Core;

public sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private static int nextId = 0x5253;
    private readonly int id = Interlocked.Increment(ref nextId);
    private bool registered;

    public event Action? Pressed;

    public GlobalHotkey(Form owner) => AssignHandle(owner.Handle);

    /// <summary>Register the global hotkey. <paramref name="allowRepeat"/> controls whether
    /// holding the key down fires <see cref="Pressed"/> repeatedly at the OS keyboard
    /// auto-repeat rate. Default <c>false</c> = Windows' MOD_NOREPEAT flag is set, so each
    /// physical press fires exactly once (the right semantic for toggle hotkeys — mute,
    /// tray show/hide — where re-firing on hold would flip state back and forth). Pass
    /// <c>true</c> for step hotkeys where holding the key is meant to ramp a value
    /// (volume up/down, both local and remote/system variants).</summary>
    public bool Register(HotkeyInfo hotkey, bool allowRepeat = false)
    {
        Unregister();
        uint modifiers = allowRepeat ? 0 : ModNoRepeat;
        if (hotkey.Control) modifiers |= ModControl;
        if (hotkey.Shift) modifiers |= ModShift;
        if (hotkey.Alt) modifiers |= ModAlt;
        registered = RegisterHotKey(Handle, id, modifiers, (uint)hotkey.Key);
        LastWin32ErrorOnRegister = registered ? 0 : Marshal.GetLastWin32Error();
        return registered;
    }

    /// <summary>The Win32 GetLastError value captured immediately after the most recent
    /// failed <see cref="Register"/> call. 0 when the last register call succeeded. Useful
    /// for distinguishing "another app already owns this combo" (1409 ERROR_HOTKEY_ALREADY_REGISTERED)
    /// from other failure modes.</summary>
    public int LastWin32ErrorOnRegister { get; private set; }

    public void Unregister()
    {
        if (!registered) return;
        UnregisterHotKey(Handle, id);
        registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == id)
        {
            Pressed?.Invoke();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        ReleaseHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
