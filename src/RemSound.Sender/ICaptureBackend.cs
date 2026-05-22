using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// Abstraction over the capture-side audio backend so <see cref="AudioSender"/> can be wired
/// to either a WASAPI implementation (today's <see cref="MixingEngine"/>) or an ASIO
/// implementation (<see cref="AsioCaptureBackend"/>) without caring which is in use.
///
/// Both backends produce 48 kHz stereo float frames via the constructor-supplied
/// <c>onMixedSamples</c> callback and accept the same <see cref="CaptureSourceSpec"/> identity
/// model. ASIO specs use a synthetic <see cref="CaptureSourceSpec.DeviceId"/> of the form
/// <c>"asio:&lt;driver-name&gt;|&lt;channel-pair-index&gt;"</c>; WASAPI specs use the
/// MMDevice ID.
/// </summary>
internal interface ICaptureBackend : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Total capture callback count across all active sources (WASAPI) or the ASIO
    /// driver's input-callback count.</summary>
    long TotalCaptureCallbacks { get; }

    long TotalCaptureBytes { get; }

    /// <summary>Brief format description of the first source (e.g. "96000 Hz, 2 ch, 32-bit
    /// float"). Used in diagnostic logs.</summary>
    string? FirstCaptureFormatDescription { get; }

    string? FirstCaptureLastError { get; }

    /// <summary>Cumulative count of samples that hit the soft-limiter / hard-clamp at the
    /// encoder boundary. Helpful to know if the source mix is hot enough to need attenuation.</summary>
    long ClippedSampleCount { get; }

    /// <summary>Friendly names of currently-active sources for diagnostic columns.</summary>
    IReadOnlyList<string> ActiveSourceNames { get; }

    /// <summary>Largest observed gap (in milliseconds) between consecutive capture callbacks
    /// since the last call. Resets to zero on read. Exposed so the periodic sender diagnostic
    /// can surface "the audio capture path stalled for 19 ms" — which on a smoothly-running
    /// backend should be ≈ buffer-period, but spikes up when GC, USB, or scheduler hiccups
    /// pause the capture thread. A receiver-side gap > 5–10 ms with otherwise clean network
    /// is almost always traceable to this value spiking on the sender. Backends that don't
    /// support per-callback timing (e.g. trivial test backends) may return 0.</summary>
    int TakeMaxCallbackGapMs();

    /// <summary>Worst single-sample step magnitude observed in the raw capture buffer since
    /// the last call; resets on read. Each backend owns its own probe instance so the
    /// cross-buffer step measurement doesn't get fooled by another backend's interleaved
    /// callbacks (which is what produced spurious 0.4-0.5 readings in BothIndependent mode
    /// before 2026-05-15). Backends that can't sensibly expose raw samples return 0.
    /// Returns the max-of-(cross-buffer, within-buffer); for the split values use
    /// <see cref="TakeMaxRawCaptureStepCrossBuffer"/> + <see cref="TakeMaxRawCaptureStepWithinBuffer"/>
    /// and do NOT also call this in the same drain window.</summary>
    float TakeMaxRawCaptureStep();

    /// <summary>Worst CROSS-BUFFER (boundary) raw-capture step since the last call. Non-zero =
    /// the first sample of some delivered capture buffer didn't continue smoothly from the
    /// last sample of the previous one. The clicks-at-buffer-boundary signal we're hunting.
    /// Resets on read. Backends that can't sensibly expose raw samples return 0.</summary>
    float TakeMaxRawCaptureStepCrossBuffer();

    /// <summary>Worst WITHIN-BUFFER raw-capture step since the last call. Non-zero = a sharp
    /// edge between two consecutive samples inside the same delivered buffer — typically
    /// real audio content (percussion, syllable onset) rather than a pipeline glitch. The
    /// "this is just music" baseline against which the cross-buffer reading is interpreted.
    /// Resets on read. Backends that can't sensibly expose raw samples return 0.</summary>
    float TakeMaxRawCaptureStepWithinBuffer();

    void Start(IReadOnlyList<CaptureSourceSpec> specs);

    /// <summary>Live-update of the active source set without stopping the mix loop. Adds/removes
    /// only the sources that actually changed. Behaviour parity expected from both backends.</summary>
    void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs);

    void Stop();
}
