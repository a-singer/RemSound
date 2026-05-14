using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemSound.Core;

/// <summary>
/// qWAVE flow attachment for RemSound's outbound UDP socket. Asks Windows' built-in QoS
/// service (Quality Windows Audio/Video Experience, <c>qwave.dll</c>) to prioritise our
/// audio packets ahead of best-effort traffic on the local hop.
///
/// What this actually buys us:
/// <list type="bullet">
///   <item>The NIC scheduler sends our packets first when there's contention with other
///         outbound traffic (browser downloads, video uploads, OS-level background
///         transfers). Locally, our audio always goes first.</item>
///   <item>Windows marks the outbound packets with DSCP bits (Voice = 46/EF) that user-mode
///         code is normally not allowed to set. The marking propagates across routers that
///         honour DSCP (most consumer kit on a LAN does).</item>
///   <item>On Wi-Fi, the DSCP marking maps to WMM Voice access category — gives our packets
///         the shortest medium-contention window the standard defines. Real win on a busy
///         access point with other Wi-Fi clients fighting for airtime.</item>
/// </list>
///
/// What it doesn't buy us: priority across the public internet. Most ISPs strip or rewrite
/// DSCP at the network edge, so qWAVE markings rarely survive past your local ISP's first
/// hop. LAN and same-house Wi-Fi: tangible benefit. Across the internet: neutral. We do
/// this anyway because the LAN/Wi-Fi half is genuine and there's no cost to attaching the
/// flow on every launch — always-on.
///
/// Lifecycle: attach to the socket once after it's bound, detach on Dispose. The qWAVE
/// flow handle is per-process-per-socket; closing it cleanly releases the OS-side flow
/// state. If anything fails (qwave.dll missing on a stripped-down Windows image, QoS
/// service disabled), the methods log and return false — the socket continues to work
/// without prioritisation.
/// </summary>
public sealed class NetworkPriority : IDisposable
{
    private IntPtr qosHandle = IntPtr.Zero;
    private uint flowId;
    private Socket? attachedSocket;
    private bool flowAdded;

    /// <summary>Attach the supplied socket to a Voice-priority qWAVE flow. Returns true on
    /// success. On failure (logged via <paramref name="onDiagnostic"/>) the socket is
    /// untouched and continues to work without prioritisation — the caller does not need
    /// to special-case the failure path. The socket must already be bound.</summary>
    public bool TryAttach(Socket socket, Action<string>? onDiagnostic = null)
    {
        if (attachedSocket is not null) return true; // already attached
        try
        {
            var version = new QOS_VERSION { MajorVersion = 1, MinorVersion = 0 };
            if (!QOSCreateHandle(ref version, out qosHandle))
            {
                var err = Marshal.GetLastWin32Error();
                onDiagnostic?.Invoke($"qwave: QOSCreateHandle failed (win32={err})");
                qosHandle = IntPtr.Zero;
                return false;
            }

            // No DestAddr (IntPtr.Zero) — flow applies to any destination from this socket.
            // QOS_NON_ADAPTIVE_FLOW = don't let qWAVE adjust our priority downward if it
            // thinks the link is congested; we want consistent Voice priority always.
            uint id = 0;
            if (!QOSAddSocketToFlow(qosHandle, socket.Handle, IntPtr.Zero,
                    QOS_TRAFFIC_TYPE_VOICE, QOS_NON_ADAPTIVE_FLOW, ref id))
            {
                var err = Marshal.GetLastWin32Error();
                onDiagnostic?.Invoke($"qwave: QOSAddSocketToFlow failed (win32={err})");
                QOSCloseHandle(qosHandle);
                qosHandle = IntPtr.Zero;
                return false;
            }
            flowId = id;
            attachedSocket = socket;
            flowAdded = true;
            onDiagnostic?.Invoke($"qwave: attached send socket to Voice-priority flow (flowId={flowId})");
            return true;
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"qwave: attach threw {ex.GetType().Name}: {ex.Message}");
            if (qosHandle != IntPtr.Zero)
            {
                try { QOSCloseHandle(qosHandle); } catch { /* ignore */ }
                qosHandle = IntPtr.Zero;
            }
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (flowAdded && attachedSocket is not null && qosHandle != IntPtr.Zero)
            {
                QOSRemoveSocketFromFlow(qosHandle, attachedSocket.Handle, flowId, 0);
            }
            if (qosHandle != IntPtr.Zero)
            {
                QOSCloseHandle(qosHandle);
            }
        }
        catch { /* shutdown is best-effort */ }
        finally
        {
            flowAdded = false;
            attachedSocket = null;
            qosHandle = IntPtr.Zero;
            flowId = 0;
        }
    }

    // === Native interop ===
    // qWAVE traffic-type ordering (higher = more priority): BestEffort < Background <
    // ExcellentEffort < AudioVideo < Voice < Control. We pick Voice rather than
    // AudioVideo because Voice has the most aggressive jitter requirements in qWAVE's
    // model, which matches RemSound's sub-50 ms expectations.
    private const int QOS_TRAFFIC_TYPE_VOICE = 4;
    private const uint QOS_NON_ADAPTIVE_FLOW = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct QOS_VERSION
    {
        public ushort MajorVersion;
        public ushort MinorVersion;
    }

    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSCreateHandle(ref QOS_VERSION Version, out IntPtr QOSHandle);

    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSCloseHandle(IntPtr QOSHandle);

    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSAddSocketToFlow(IntPtr QOSHandle, IntPtr Socket,
        IntPtr DestAddr, int TrafficType, uint Flags, ref uint FlowID);

    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSRemoveSocketFromFlow(IntPtr QOSHandle, IntPtr Socket,
        uint FlowID, uint Flags);
}
