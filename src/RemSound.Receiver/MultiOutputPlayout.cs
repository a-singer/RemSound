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
    // Snapshot of the current output buffers, rebuilt only when SetOutputDevices changes the
    // device set (rare — typically once per user action, minutes apart). The producer loop
    // reads this with a single volatile load per tick instead of taking the gate and
    // rebuilding `outputs.Values.Select(o => o.Buffer).ToArray()` on every 10 ms tick.
    // Item 7 of RemSoundefficiency.md — eliminates ~100 array allocations per second on the
    // receive side whenever any output device is ticked. Empty array is a singleton via
    // Array.Empty<T>(), so the default value costs nothing.
    private volatile BufferedWaveProvider[] outputBufferSnapshot = Array.Empty<BufferedWaveProvider>();

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
            // Reset the snapshot the producer loop reads so any subsequent Start sees the
            // empty state cleanly (not a stale snapshot from the previous session). Empty
            // array is a cached singleton, no allocation.
            outputBufferSnapshot = Array.Empty<BufferedWaveProvider>();
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

            // Refresh the snapshot the producer loop reads. Under the gate, so the producer
            // sees a consistent view; once published via the volatile field, the loop reads
            // it without taking the gate every tick. Empty case uses the cached singleton
            // so it's allocation-free. Item 7 of RemSoundefficiency.md.
            outputBufferSnapshot = outputs.Count == 0
                ? Array.Empty<BufferedWaveProvider>()
                : outputs.Values.Select(o => o.Buffer).ToArray();
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
                    // Item 6 of RemSoundefficiency.md — see matching change in
                    // MixingEngine.MixLoop for the rationale. WaitOne is allocation-free
                    // and semantically equivalent to WaitAny on a 1-element array.
                    if (ct.WaitHandle.WaitOne(sleepMs)) break;
                    continue;
                }

                if (now - nextTickStopwatch > ticksPerFrame * 4)
                {
                    nextTickStopwatch = now;
                }
                nextTickStopwatch += ticksPerFrame;

                // Read the pre-built snapshot. Volatile load — no lock, no allocation per
                // tick. SetOutputDevices rebuilds the snapshot under the gate whenever the
                // device set changes (rare event), so reads here see a consistent view.
                // Skip the source.Read entirely when no outputs are ticked: in BothIndependent
                // mode the source is shared between WASAPI and ASIO, and pulling here when
                // WASAPI has nothing ticked would consume PlayoutEngine audio ahead of the
                // ASIO consumer. Pre-2026-05-23 this whole block ran under `lock (gate)` and
                // rebuilt the array on every tick — fixed as item 7 of RemSoundefficiency.md.
                var targets = outputBufferSnapshot;
                if (targets.Length == 0) continue;

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
