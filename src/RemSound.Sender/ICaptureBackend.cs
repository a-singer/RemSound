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

    void Start(IReadOnlyList<CaptureSourceSpec> specs);

    /// <summary>Live-update of the active source set without stopping the mix loop. Adds/removes
    /// only the sources that actually changed. Behaviour parity expected from both backends.</summary>
    void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs);

    void Stop();
}
