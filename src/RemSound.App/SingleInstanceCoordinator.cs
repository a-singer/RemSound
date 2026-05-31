using System.ComponentModel;
using System.Diagnostics;

namespace RemSound.App;

/// <summary>
/// Enforces "only one RemSound at a time" and provides the plumbing to either surface the
/// already-running copy or force a stuck one to close. Created once in Program.Main and held
/// for the whole app lifetime.
///
/// Why this exists: RemSound had no single-instance guard at all — it only ever NOTICED a
/// second copy when a global hotkey failed to register. With the auto-updater relaunching the
/// app, a copy that didn't exit cleanly could leave two (then more) copies running at once,
/// each playing received audio. Andre hit exactly that on 2026-05-30: copies "stacked and
/// stacked", the audio got deafening, and the only way out was force-killing them all from a
/// terminal. A real lock makes that structurally impossible.
///
/// Mechanism:
///   * A named system <see cref="Mutex"/> is the lock. The first copy to start owns it; a
///     later copy fails to acquire it and so KNOWS another copy is live.
///   * A named auto-reset <see cref="EventWaitHandle"/> is the "come to the front" signal.
///     The owning copy runs a background thread waiting on it; when a second copy sets it
///     (the user chose "switch to the running copy"), the thread raises
///     <see cref="ActivateRequested"/>, which Program.Main routes to the live window.
///   * <see cref="ForceCloseOtherInstances"/> terminates any other RemSound process with
///     Process.Kill (TerminateProcess), so a hung copy dies regardless of its message-loop
///     state. If a copy is running elevated and we are not, the kill is retried via an
///     elevated taskkill (one UAC prompt).
///
/// Names are in the per-session (Local) namespace and are fixed strings — not version- or
/// path-derived — so ANY RemSound.exe blocks ANY other, which is the "refuses to load unless
/// previous copies are gone" guarantee Andre asked for. 2026-05-31.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "RemSound.SingleInstance.Mutex.v1";
    private const string ActivateEventName = "RemSound.SingleInstance.Activate.v1";

    private readonly Mutex mutex;
    private bool ownsMutex;
    private EventWaitHandle? activateEvent;
    private Thread? listenerThread;
    private volatile bool stopListener;

    /// <summary>Raised on a background thread when another copy asks this one to surface.
    /// Program.Main marshals it onto the running window.</summary>
    public event Action? ActivateRequested;

    public SingleInstanceCoordinator()
    {
        mutex = new Mutex(initiallyOwned: false, MutexName);
    }

    /// <summary>True once we hold the single-instance lock.</summary>
    public bool IsPrimaryInstance => ownsMutex;

    /// <summary>Try to take the single-instance lock, waiting up to <paramref name="timeout"/>.
    /// An abandoned mutex (previous owner crashed or was force-killed) counts as acquired — we
    /// become the new owner.</summary>
    public bool TryAcquire(TimeSpan timeout)
    {
        if (ownsMutex) return true;
        try
        {
            ownsMutex = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing. Ownership passes to us.
            ownsMutex = true;
        }
        return ownsMutex;
    }

    /// <summary>Start the background listener that surfaces this copy when a later copy
    /// signals it. Only meaningful on the primary instance.</summary>
    public void StartActivationListener()
    {
        try
        {
            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch
        {
            // Can't create the signal — "switch to the running copy" just won't surface this
            // window automatically. Not fatal; the user can still reach it via the tray.
            activateEvent = null;
            return;
        }
        listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "RemSound-Activation" };
        listenerThread.Start();
    }

    private void ListenLoop()
    {
        var ev = activateEvent;
        if (ev is null) return;
        while (!stopListener)
        {
            try
            {
                // Short timeout so Dispose can stop us promptly even if no signal arrives.
                if (ev.WaitOne(500))
                {
                    if (stopListener) return;
                    ActivateRequested?.Invoke();
                }
            }
            catch
            {
                return;
            }
        }
    }

    /// <summary>Signal whichever copy currently owns the lock to bring itself to the front.
    /// Called by a SECOND copy that chose "switch to the running copy".</summary>
    public static void SignalExistingToActivate()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var ev))
            {
                using (ev) ev.Set();
            }
        }
        catch
        {
            // Best-effort — if the signal can't be delivered the user can click the tray icon.
        }
    }

    /// <summary>Force every OTHER RemSound process to terminate. Returns true if no other
    /// RemSound process remains after the attempt. Uses Process.Kill (TerminateProcess) so a
    /// hung copy dies regardless of its state; PIDs we can't reach (an elevated copy while we
    /// run normally) are retried via an elevated taskkill.</summary>
    public static bool ForceCloseOtherInstances()
    {
        var me = Environment.ProcessId;
        var deniedPids = new List<int>();

        foreach (var p in OtherInstances(me))
        {
            try
            {
                p.Kill();
                p.WaitForExit(4000);
            }
            catch (Win32Exception)
            {
                // Access denied — almost always an elevated target we can't reach unelevated.
                deniedPids.Add(p.Id);
            }
            catch (InvalidOperationException)
            {
                // Already exited between enumeration and Kill — fine.
            }
            catch
            {
                // Ignore — the post-check below is the source of truth.
            }
            finally
            {
                p.Dispose();
            }
        }

        if (deniedPids.Count > 0)
        {
            TryElevatedKill(deniedPids);
        }

        // Source of truth: is the field actually clear now?
        var remaining = OtherInstances(me).ToList();
        var clear = remaining.Count == 0;
        foreach (var p in remaining) p.Dispose();
        return clear;
    }

    private static List<Process> OtherInstances(int selfPid)
    {
        Process[] all;
        try { all = Process.GetProcessesByName("RemSound"); }
        catch { return []; }

        var others = new List<Process>(all.Length);
        foreach (var p in all)
        {
            if (p.Id == selfPid) { p.Dispose(); continue; }
            others.Add(p);
        }
        return others;
    }

    private static void TryElevatedKill(List<int> pids)
    {
        try
        {
            // Target exact PIDs, never /IM RemSound.exe — an image-name kill would also take
            // out this very process. Verb=runas raises the one UAC prompt that lets a normal
            // process terminate an elevated one.
            var args = "/F " + string.Join(" ", pids.ConvertAll(id => $"/PID {id}"));
            var psi = new ProcessStartInfo("taskkill.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            proc?.Dispose();
        }
        catch
        {
            // User declined UAC, or taskkill wasn't available. The caller's post-check reports
            // the field still isn't clear and the UI surfaces a message.
        }
    }

    public void Dispose()
    {
        stopListener = true;
        try { activateEvent?.Set(); } catch { /* wake the listener so it can exit */ }
        try { listenerThread?.Join(1000); } catch { /* ignore */ }
        try { activateEvent?.Dispose(); } catch { /* ignore */ }
        if (ownsMutex)
        {
            try { mutex.ReleaseMutex(); } catch { /* ignore */ }
        }
        try { mutex.Dispose(); } catch { /* ignore */ }
    }
}
