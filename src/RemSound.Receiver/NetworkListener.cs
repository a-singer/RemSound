using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Owns the UDP receive socket and a single dedicated foreground thread that drains it.
/// Hands raw packets (byte buffer + length + remote endpoint) up to a callback supplied by the
/// owner — has no idea what's inside the packets.
///
/// Allocation-free in steady state: one fixed receive buffer reused across calls,
/// <see cref="Socket.ReceiveFrom"/> with <see cref="SocketAddress"/> avoids the per-call
/// IPEndPoint boxing that <see cref="UdpClient.ReceiveAsync"/> incurred.
/// </summary>
internal sealed class NetworkListener : IDisposable
{
    private readonly Action<byte[], int, IPEndPoint> onPacket;
    private readonly Action<string> onDiagnostic;
    private CancellationTokenSource? cts;
    private Socket? socket;
    private Thread? thread;

    // Time-in-user-handler instrumentation. We measure from "ReceiveFrom returned" to
    // "onPacket returned" so the SNAP can split observed inter-packet jitter at the
    // receiver. If this metric is consistently in the multi-ms range, the receiver's
    // own processing chain is the source of the gap (lock contention with the audio
    // thread, GC, decode work backing up) rather than the network or the sender.
    private long maxOnPacketTicks;
    public int TakeMaxOnPacketMs() =>
        (int)(Interlocked.Exchange(ref maxOnPacketTicks, 0) * 1000 / Stopwatch.Frequency);

    public NetworkListener(Action<byte[], int, IPEndPoint> onPacket, Action<string> onDiagnostic)
    {
        this.onPacket = onPacket;
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => socket is not null;

    public void Start(int udpPort)
    {
        Stop();
        cts = new CancellationTokenSource();
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.ReceiveBufferSize = 512 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Any, udpPort));

        var startedSocket = socket;
        var token = cts.Token;
        thread = new Thread(() => ReceiveLoop(startedSocket, token))
        {
            IsBackground = true,
            Name = "RemSound.Receive",
        };
        thread.Start();
        onDiagnostic($"network listener bound to UDP :{udpPort}");
    }

    public void Stop()
    {
        cts?.Cancel();
        try { socket?.Close(); } catch { /* ignore */ }
        socket = null;
        try { thread?.Join(500); } catch { /* ignore */ }
        thread = null;
        cts?.Dispose();
        cts = null;
    }

    public void Dispose() => Stop();

    private void ReceiveLoop(Socket activeSocket, CancellationToken token)
    {
        using var threadBoost = new WindowsAudioThreadBoost("Capture");
        var buffer = new byte[2048];
        EndPoint anyEndpoint = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            int received;
            try
            {
                received = activeSocket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref anyEndpoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            catch (OperationCanceledException) { break; }

            if (received <= 0) continue;
            if (anyEndpoint is not IPEndPoint remote) continue;

            try
            {
                // Dispatch timing feeds the SNAP's rxDispMs column. Skipped when diagnostics
                // are off so the receive loop isn't paying two Stopwatch reads + a CAS loop
                // per packet for a number nobody is going to log.
                if (RemSound.Core.DiagnosticsGate.Enabled)
                {
                    var dispatchStart = Stopwatch.GetTimestamp();
                    onPacket(buffer, received, remote);
                    var elapsed = Stopwatch.GetTimestamp() - dispatchStart;
                    long current;
                    do { current = Volatile.Read(ref maxOnPacketTicks); }
                    while (elapsed > current && Interlocked.CompareExchange(ref maxOnPacketTicks, elapsed, current) != current);
                }
                else
                {
                    onPacket(buffer, received, remote);
                }
            }
            catch (Exception ex)
            {
                onDiagnostic($"packet handler threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
