using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Drives N WASAPI output devices from a single shared <see cref="PlayoutEngine"/>. A master
/// producer task running on a Stopwatch-based 10 ms tick reads mixed audio from the engine and
/// fans it out to each device's <see cref="BufferedWaveProvider"/>; each <see cref="WasapiOut"/>
/// consumes from its own buffer at its own device clock.
///
/// Why a master producer loop instead of letting one WasapiOut drive PlayoutEngine.Read directly:
///   - With multiple WasapiOuts, each render thread would call Read independently and only one
///     output would get each frame; the others would starve.
///   - The producer loop runs at the canonical 48 kHz / 10 ms cadence, decoupled from any one
///     device's clock. Per-device drift is absorbed by the BufferedWaveProvider's headroom.
///
/// Output-device set is diffed on <see cref="SetOutputDevices"/>: existing devices stay live,
/// removed ones are stopped, new ones are opened. No audio interruption to the unchanged ones.
/// </summary>
internal sealed class MultiOutputPlayout : IRenderBackend
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MixBytesPerFrame = MixChannels * sizeof(float);
    private const int FrameMs = 10;
    private const int FrameBytes = MixSampleRate * MixBytesPerFrame * FrameMs / 1000; // 3840 bytes
    private const int OutputBufferMs = 100; // per-device BufferedWaveProvider capacity

    // Source typed as IWaveProvider (rather than concrete PlayoutEngine) so the composite
    // backend can hand us a tee'd buffer instead of the engine directly. Single-backend usage
    // still passes the engine in unchanged.
    private readonly IWaveProvider source;
    private readonly Action<string>? onDiagnostic;
    private readonly object gate = new();
    private readonly Dictionary<string, OutputEntry> outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] frameScratch = new byte[FrameBytes];
    private readonly WaveFormat sharedFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels);

    private CancellationTokenSource? cts;
    private Task? produceTask;

    public MultiOutputPlayout(IWaveProvider source, Action<string>? onDiagnostic = null)
    {
        this.source = source;
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => produceTask is { IsCompleted: false };

    /// <summary>
    /// Friendly names of currently-active output devices, comma-joined. "(none)" when no
    /// device is enabled. Used by the snapshot log column.
    /// </summary>
    public string ActiveDeviceSummary
    {
        get
        {
            lock (gate)
            {
                if (outputs.Count == 0) return "(none)";
                if (outputs.Count <= 3) return string.Join(", ", outputs.Values.Select(o => o.Name));
                return $"({outputs.Count} outputs)";
            }
        }
    }

    public IReadOnlyList<string> ActiveDeviceIds
    {
        get { lock (gate) return outputs.Keys.ToList(); }
    }

    public void Start()
    {
        lock (gate)
        {
            if (IsRunning) return;
            cts = new CancellationTokenSource();
            produceTask = Task.Run(() => ProduceLoop(cts.Token));
            onDiagnostic?.Invoke("multi-output producer started");
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            try { cts?.Cancel(); } catch { /* ignore */ }
            try { produceTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignore */ }
            cts?.Dispose();
            cts = null;
            produceTask = null;

            foreach (var o in outputs.Values) DisposeOutput(o);
            outputs.Clear();
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Live-update of the output device set. Devices already present stay live (no audio
    /// interruption); removed devices are stopped + disposed; new devices are opened. Caller
    /// supplies device IDs (from MMDeviceEnumerator). An empty set means "render to nothing"
    /// — the producer loop keeps running so receive-side mixing/auto-tune state stays alive.
    /// </summary>
    public void SetOutputDevices(IReadOnlyList<string> deviceIds)
    {
        lock (gate)
        {
            var desired = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);

            // Remove outputs no longer wanted.
            foreach (var id in outputs.Keys.Where(k => !desired.Contains(k)).ToList())
            {
                if (outputs.Remove(id, out var o))
                {
                    onDiagnostic?.Invoke($"output removed: \"{o.Name}\"");
                    DisposeOutput(o);
                }
            }

            // Add new outputs.
            using var enumerator = new MMDeviceEnumerator();
            foreach (var id in deviceIds)
            {
                if (outputs.ContainsKey(id)) continue;
                MMDevice? device = null;
                WasapiOut? wasapi = null;
                try
                {
                    device = enumerator.GetDevice(id);
                    var name = device.FriendlyName;
                    var buffer = new BufferedWaveProvider(sharedFormat)
                    {
                        ReadFully = true,
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromMilliseconds(OutputBufferMs),
                    };
                    wasapi = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 15);
                    wasapi.Init(buffer);
                    wasapi.Play();
                    outputs[id] = new OutputEntry { Device = device, Output = wasapi, Buffer = buffer, Name = name };
                    onDiagnostic?.Invoke($"output added: \"{name}\"");
                }
                catch (Exception ex)
                {
                    onDiagnostic?.Invoke($"failed to open output \"{id}\": {ex.GetType().Name}: {ex.Message}");
                    try { wasapi?.Dispose(); } catch { /* ignore */ }
                    try { device?.Dispose(); } catch { /* ignore */ }
                }
            }
        }
    }

    private static void DisposeOutput(OutputEntry o)
    {
        try { o.Output.Stop(); } catch { /* ignore */ }
        try { o.Output.Dispose(); } catch { /* ignore */ }
        try { o.Device.Dispose(); } catch { /* ignore */ }
    }

    private async Task ProduceLoop(CancellationToken ct)
    {
        // Pro Audio MMCSS for the producer thread — it's the one feeding all WASAPI outputs.
        using var threadBoost = new WindowsAudioThreadBoost("Pro Audio");

        var ticksPerFrame = Stopwatch.Frequency * FrameMs / 1000;
        var nextTickStopwatch = Stopwatch.GetTimestamp() + ticksPerFrame;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = Stopwatch.GetTimestamp();
                if (nextTickStopwatch > now)
                {
                    var sleepMs = (int)Math.Clamp((nextTickStopwatch - now) * 1000 / Stopwatch.Frequency, 1, 50);
                    if (WaitHandle.WaitAny(new[] { ct.WaitHandle }, sleepMs) == 0) break;
                    continue;
                }

                if (now - nextTickStopwatch > ticksPerFrame * 4)
                {
                    nextTickStopwatch = now;
                }
                nextTickStopwatch += ticksPerFrame;

                // Snapshot the buffers under the gate so we don't iterate a mid-mutation dict.
                // Also skip the source.Read entirely when no outputs are ticked: in
                // BothIndependent mode the source is a FanOutSource view shared with the ASIO
                // lane, and pulling here when WASAPI has nothing ticked makes the FanOut
                // consume PlayoutEngine audio ~10 ms ahead of the ASIO consumer, leaving the
                // ASIO lane permanently reading from a cache 10 ms behind the source. That
                // showed up in test logs as fanCacheMs sustained at 12–14 ms with bufAvg=0,
                // and audibly as an extra 10 ms baked into the ASIO lane's perceived latency.
                // The gate-then-read order matters; the previous order (read first, then
                // check outputs.Count) was the bug.
                BufferedWaveProvider[] targets;
                lock (gate)
                {
                    if (outputs.Count == 0) continue;
                    targets = outputs.Values.Select(o => o.Buffer).ToArray();
                }

                var produced = source.Read(frameScratch, 0, FrameBytes);
                if (produced <= 0) continue;

                foreach (var buffer in targets)
                {
                    try { buffer.AddSamples(frameScratch, 0, produced); }
                    catch { /* per-output failure shouldn't kill the loop */ }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                onDiagnostic?.Invoke($"producer loop error: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class OutputEntry
    {
        public required MMDevice Device { get; init; }
        public required WasapiOut Output { get; init; }
        public required BufferedWaveProvider Buffer { get; init; }
        public required string Name { get; init; }
    }
}
