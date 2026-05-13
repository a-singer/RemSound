using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// ASIO render backend. Drives a single <see cref="AsioOut"/> for the chosen ASIO driver,
/// pulling the receiver's mixed stereo audio from <see cref="PlayoutEngine"/> and broadcasting
/// it across one or more output channel pairs of the driver. Same shape as
/// <see cref="MultiOutputPlayout"/>: <see cref="AudioReceiver"/> doesn't care which is active.
///
/// Spec identity: each output ID is a synthetic <c>"asio:&lt;channel-pair-index&gt;"</c>. Pair 0
/// = ASIO output channels 0+1, pair 1 = 2+3, etc. The driver itself is locked at construction.
///
/// Same simplifications as <see cref="AsioCaptureBackend"/>: 48 kHz fixed; driver is single
/// per session. Always opens the AsioOut with the driver's full output channel count so that
/// adding/removing channel pairs never requires reopening the driver — important when the
/// sender and receiver are both holding the same single-client driver (Komplete Audio etc.):
/// reopening one while the other is alive caused 15-second freezes.
/// </summary>
internal sealed class AsioRenderBackend : IRenderBackend
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;

    // Same reasoning as MultiOutputPlayout — source typed as IWaveProvider so the composite
    // backend can hand us a tee'd buffer.
    private readonly IWaveProvider source;
    private readonly Action<string>? onDiagnostic;
    private readonly string driverName;
    private readonly object gate = new();

    private AsioOut? asio;
    private List<int> activeChannelPairs = [];
    private BroadcastProvider? broadcaster;

    public AsioRenderBackend(string driverName, IWaveProvider source, Action<string>? onDiagnostic = null)
    {
        this.driverName = driverName;
        this.source = source;
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => asio is not null;

    public string ActiveDeviceSummary
    {
        get
        {
            lock (gate)
            {
                if (activeChannelPairs.Count == 0) return "(none)";
                var names = activeChannelPairs.Select(p => $"{driverName} ASIO {p * 2 + 1}/{p * 2 + 2}").ToList();
                if (names.Count <= 3) return string.Join(", ", names);
                return $"({names.Count} ASIO outputs)";
            }
        }
    }

    public IReadOnlyList<string> ActiveDeviceIds
    {
        get { lock (gate) return activeChannelPairs.Select(AsioDeviceId.Format).ToList(); }
    }

    public void Start()
    {
        // ASIO render starts lazily when SetOutputDevices is given a non-empty list. There's no
        // useful "open driver but render to nothing" state — that just locks the device with no
        // benefit. The MixingEngine equivalent (producer loop) for WASAPI runs continuously
        // even with zero outputs to keep state alive; ASIO doesn't need that since the AsioOut
        // *is* the output and there's nothing to keep alive when no channels are wanted.
        // Caller is expected to call SetOutputDevices first; this method is a no-op when empty.
        lock (gate)
        {
            if (IsRunning) return;
            if (activeChannelPairs.Count == 0) return;
            OpenAsioLocked();
        }
    }

    public void Stop()
    {
        lock (gate) StopInternal();
    }

    public void SetOutputDevices(IReadOnlyList<string> deviceIds)
    {
        lock (gate)
        {
            var newPairs = ParsePairs(deviceIds);
            if (newPairs.Count == 0)
            {
                if (IsRunning) StopInternal();
                activeChannelPairs = newPairs;
                return;
            }

            activeChannelPairs = newPairs;

            // First time we have any pairs → open the driver. Otherwise we never reopen on a
            // pair-set change, because we already opened with the driver's full channel count
            // at Start time. Just update the broadcaster's pair list and we're done.
            if (asio is null)
            {
                OpenAsioLocked();
                return;
            }
            broadcaster?.SetActivePairs(activeChannelPairs);
            onDiagnostic?.Invoke($"asio render: pairs updated to {string.Join(",", activeChannelPairs)} (no driver restart)");
        }
    }

    private void OpenAsioLocked()
    {
        try
        {
            asio = new AsioOut(driverName);
            // Always open with the driver's full output channel count. Channels we don't
            // immediately broadcast to are zero-filled by BroadcastProvider, which is
            // essentially free. Trades a tiny bit of buffer memory for a big stability win:
            // adding or removing an output pair never reopens the driver — see the type
            // doc-comment for why this matters with single-client drivers.
            var outputChannelCount = asio.DriverOutputChannelCount;
            if (outputChannelCount <= 0)
            {
                onDiagnostic?.Invoke($"asio render: driver \"{driverName}\" reports zero output channels");
                StopInternal();
                return;
            }
            // Sanity-check requested pairs are in range; warn if not but continue (out-of-range
            // pairs simply get no audio).
            var maxPair = activeChannelPairs.Max();
            var highestNeededChannel = (maxPair + 1) * 2;
            if (highestNeededChannel > outputChannelCount)
            {
                onDiagnostic?.Invoke($"asio render: driver \"{driverName}\" only has {outputChannelCount} output channels, but spec requests pair {maxPair} (channels {maxPair * 2 + 1}/{maxPair * 2 + 2})");
            }
            broadcaster = new BroadcastProvider(source, outputChannelCount, activeChannelPairs);
            asio.ChannelOffset = 0;
            asio.Init(broadcaster);
            asio.Play();
            onDiagnostic?.Invoke($"asio render started \"{driverName}\" {MixSampleRate} Hz, {outputChannelCount} output channel(s); pairs={string.Join(",", activeChannelPairs)}");
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"asio render start failed: {ex.GetType().Name}: {ex.Message}");
            StopInternal();
        }
    }

    private void StopInternal()
    {
        if (asio is not null)
        {
            try { asio.Stop(); } catch { /* ignore */ }
            try { asio.Dispose(); } catch { /* ignore */ }
            asio = null;
        }
        broadcaster = null;
    }

    public void Dispose() => Stop();

    private static List<int> ParsePairs(IReadOnlyList<string> deviceIds)
    {
        var result = new List<int>();
        foreach (var id in deviceIds)
        {
            if (AsioDeviceId.TryParse(id, out var pair) && pair >= 0)
            {
                result.Add(pair);
            }
        }
        result.Sort();
        return result.Distinct().ToList();
    }

    /// <summary>
    /// Wave provider that pulls stereo audio from <see cref="PlayoutEngine"/> and writes it to
    /// a multi-channel ASIO buffer at the requested channel pair positions, zero-filling the
    /// channels that aren't selected. Output is interleaved 32-bit float at 48 kHz, exactly
    /// what NAudio's AsioOut wants.
    /// </summary>
    private sealed class BroadcastProvider : IWaveProvider
    {
        private readonly IWaveProvider source;
        private readonly int outputChannelCount;
        private byte[] sourceScratchBytes = new byte[16384];
        private List<int> activePairs;

        public WaveFormat WaveFormat { get; }

        public BroadcastProvider(IWaveProvider source, int outputChannelCount, List<int> activePairs)
        {
            this.source = source;
            this.outputChannelCount = outputChannelCount;
            this.activePairs = new List<int>(activePairs);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, outputChannelCount);
        }

        public void SetActivePairs(IEnumerable<int> pairs)
        {
            // Atomic swap. Read side reads activePairs once per Read so a partial swap is
            // tolerable — at worst we get one tick of stale routing.
            activePairs = pairs.ToList();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            // Frame size in BYTES on the output side.
            var bytesPerOutputFrame = outputChannelCount * sizeof(float);
            var frames = count / bytesPerOutputFrame;
            if (frames <= 0) return 0;

            // Pull stereo from PlayoutEngine — its WaveFormat is 48k stereo float, so 8 bytes
            // per frame.
            var sourceBytes = frames * MixChannels * sizeof(float);
            if (sourceScratchBytes.Length < sourceBytes) sourceScratchBytes = new byte[sourceBytes];
            source.Read(sourceScratchBytes, 0, sourceBytes);

            // Interpret source bytes as float array, output bytes as float array, broadcast.
            var srcFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(sourceScratchBytes.AsSpan(0, sourceBytes));
            var dstFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            dstFloats.Clear();

            var pairs = activePairs;
            for (var f = 0; f < frames; f++)
            {
                var l = srcFloats[f * MixChannels];
                var r = srcFloats[f * MixChannels + 1];
                var dstFrameStart = f * outputChannelCount;
                foreach (var pair in pairs)
                {
                    var lCh = pair * 2;
                    var rCh = pair * 2 + 1;
                    if (lCh < outputChannelCount) dstFloats[dstFrameStart + lCh] = l;
                    if (rCh < outputChannelCount) dstFloats[dstFrameStart + rCh] = r;
                }
            }

            return count;
        }
    }
}
