namespace RemSound.Core;

/// <summary>
/// Per-stage sample-step "discontinuity detector" for diagnosing where in the audio pipeline
/// pops are being introduced. A single-sample step magnitude is the absolute difference
/// between consecutive samples of the same channel; a typical "click" in real audio shows
/// up as a step well above what naturally occurs in music or speech content.
///
/// 2026-05-21: split the max into TWO independent counters so we can tell the difference
/// between a sharp transient inside a buffer (natural-looking real audio content) and a
/// discontinuity at the buffer / packet boundary (samples that are not adjacent in time —
/// i.e. something we lost, duplicated, or mis-stitched in the pipeline). The plain
/// <see cref="TakeMax"/> still returns the larger of the two for back-compat, but the
/// <see cref="TakeMaxCrossBuffer"/> / <see cref="TakeMaxWithinBuffer"/> pair lets the diag
/// logger emit both, so a click event in the log clearly says which side it's on:
///
///   * <c>stepXB</c> — first sample of a new delivered buffer vs the last sample of the
///     previous one (i.e. the cross-buffer carry). A non-zero value here means our chain
///     received samples that aren't contiguous with what came before — driver bug, library
///     misalignment, or a sample drop/duplicate at a boundary. The buffer in question is
///     whatever the caller passes to <see cref="ScanStereo"/> / <see cref="ScanInterleavedChannel"/>:
///     ASIO/WASAPI capture callback in the raw probes, an OnMixedSamples callback in the
///     pre-encode probe, a PCM packet in the post-decode probe, and so on.
///   * <c>stepWB</c> — two samples that ARE adjacent in the same delivered buffer. A
///     non-zero value here is just real audio content with a sharp edge; loud transients,
///     percussion hits, the start of a syllable.
///
/// Each probe holds the maximum step observed since the last drain. The diag log polls the
/// drains once per second to emit the worst step at that pipeline stage. Comparing the max
/// across stages — sender pre-encode, receiver post-decode, receiver post-ring-read,
/// receiver post-resampler, final output — reveals which stage introduces the click.
///
/// Thread model: writes are lock-free CAS-update of a long-encoded float bit pattern (so
/// one probe can be hit from multiple threads if needed). Read-and-reset is also atomic.
/// Buffers are scanned cheaply — one subtract + abs + compare per sample — and the whole
/// scan is gated by <see cref="DiagnosticsGate.Enabled"/> so it pays nothing in production
/// when logging is off.
/// </summary>
public sealed class AudioStepProbe
{
    // Two separate maxes — see class comment. Both are float bits packed into a long so the
    // CAS-update loop can flip them atomically from any thread. Encoded as Int32 -> long
    // because BitConverter.SingleToInt32Bits is the cheapest float<->int32 bridge.
    private long maxCrossBufferStepBits;
    private long maxWithinBufferStepBits;
    // Remember the last sample on each channel so the next scan can compute the cross-buffer
    // step. Without this we'd miss any discontinuity at the buffer boundary (the most
    // suspicious place — that's where copies, format conversions and resampler hand-offs
    // happen).
    private float lastL;
    private float lastR;
    private bool hasLast;

    /// <summary>Scan a single-channel slice of an interleaved multi-channel float buffer and
    /// update the max step magnitude. <paramref name="channelCount"/> is the total number of
    /// interleaved channels; <paramref name="channelIndex"/> picks which one to scan. Used by
    /// the ASIO capture probe to look at raw driver-delivered samples on individual channels
    /// before any mixing or clamping happens. Cheap; safe to call from any thread; no-op when
    /// diagnostics are disabled.</summary>
    public void ScanInterleavedChannel(ReadOnlySpan<float> interleavedFloats, int channelCount, int channelIndex)
    {
        if (!DiagnosticsGate.Enabled) return;
        if (interleavedFloats.IsEmpty) return;
        if (channelCount <= 0 || channelIndex < 0 || channelIndex >= channelCount) return;
        var maxCross = ReadMax(ref maxCrossBufferStepBits);
        var maxWithin = ReadMax(ref maxWithinBufferStepBits);
        // Use lastL as the cross-buffer carry for single-channel scans. (We don't need a
        // separate "lastSingle" — every probe is consumed by exactly one caller at a time, so
        // reusing the field is fine. The cross-buffer step is what matters for buffer-boundary
        // glitches.)
        var prev = lastL;
        var seedFromPrev = hasLast;
        var samples = interleavedFloats.Length / channelCount;
        for (var i = 0; i < samples; i++)
        {
            var s = interleavedFloats[i * channelCount + channelIndex];
            if (i == 0)
            {
                // First sample of this buffer. If we have a carry from the previous scan,
                // compare it to that — that comparison IS the cross-buffer boundary step.
                // If we don't (first ever call), seed prev with this sample so the inner-loop
                // step calc starting at i=1 has a sensible reference.
                if (seedFromPrev)
                {
                    var step = s - prev;
                    if (step < 0f) step = -step;
                    if (step > maxCross) maxCross = step;
                }
                else
                {
                    prev = s;
                }
            }
            else
            {
                var step = s - prev;
                if (step < 0f) step = -step;
                if (step > maxWithin) maxWithin = step;
            }
            prev = s;
        }
        lastL = prev;
        hasLast = true;
        WriteMaxIfGreater(ref maxCrossBufferStepBits, maxCross);
        WriteMaxIfGreater(ref maxWithinBufferStepBits, maxWithin);
    }

    /// <summary>Scan an interleaved stereo float span and update the max step. Cheap; safe
    /// to call from any thread. No-op if diagnostics are disabled.</summary>
    public void ScanStereo(ReadOnlySpan<float> stereoFloats)
    {
        if (!DiagnosticsGate.Enabled) return;
        if (stereoFloats.IsEmpty) return;
        var maxCross = ReadMax(ref maxCrossBufferStepBits);
        var maxWithin = ReadMax(ref maxWithinBufferStepBits);
        var prevL = lastL;
        var prevR = lastR;
        var seedFromPrev = hasLast;
        // Pair walk. For samples after the first, compare to the previous sample of the
        // same channel from THIS buffer (within-buffer). For the first pair, compare to the
        // saved last-sample-from-the-previous-buffer if available (cross-buffer).
        for (var i = 0; i + 1 < stereoFloats.Length; i += 2)
        {
            var l = stereoFloats[i];
            var r = stereoFloats[i + 1];
            if (i == 0)
            {
                if (seedFromPrev)
                {
                    var stepL = l - prevL;
                    var stepR = r - prevR;
                    var absL = stepL < 0f ? -stepL : stepL;
                    var absR = stepR < 0f ? -stepR : stepR;
                    if (absL > maxCross) maxCross = absL;
                    if (absR > maxCross) maxCross = absR;
                }
                // If we don't have a carry, simply skip the comparison — the next iteration's
                // within-buffer step (i=2 vs i=0) will be the first real measurement.
            }
            else
            {
                var stepL = l - stereoFloats[i - 2];
                var stepR = r - stereoFloats[i - 1];
                var absL = stepL < 0f ? -stepL : stepL;
                var absR = stepR < 0f ? -stepR : stepR;
                if (absL > maxWithin) maxWithin = absL;
                if (absR > maxWithin) maxWithin = absR;
            }
        }
        // Save the last sample of this buffer for the next scan.
        var lastIdx = stereoFloats.Length - 2;
        lastL = stereoFloats[lastIdx];
        lastR = stereoFloats[lastIdx + 1];
        hasLast = true;
        WriteMaxIfGreater(ref maxCrossBufferStepBits, maxCross);
        WriteMaxIfGreater(ref maxWithinBufferStepBits, maxWithin);
    }

    /// <summary>Atomic snapshot of the current maxes + reset to zero. Returns the larger of
    /// the cross-buffer and within-buffer maxes — preserves the pre-2026-05-21 semantics for
    /// callers that just want "the worst step we saw at this stage". For the cross/within
    /// split, use <see cref="TakeMaxCrossBuffer"/> + <see cref="TakeMaxWithinBuffer"/>
    /// instead; calling either of those drains its own counter independently of this one,
    /// so a caller wanting the split must NOT also call <c>TakeMax</c> in the same
    /// 1-second window.</summary>
    public float TakeMax()
    {
        var c = TakeMaxCrossBuffer();
        var w = TakeMaxWithinBuffer();
        return c > w ? c : w;
    }

    /// <summary>Atomic snapshot + reset of the cross-buffer max. A non-zero value here means
    /// the first sample of some delivered buffer did NOT continue smoothly from the last
    /// sample of the previous buffer — i.e. samples not adjacent in time. Strong signal that
    /// the pipeline lost, duplicated, or mis-stitched a buffer boundary.</summary>
    public float TakeMaxCrossBuffer()
    {
        var bits = Interlocked.Exchange(ref maxCrossBufferStepBits, 0);
        return BitConverter.Int32BitsToSingle((int)bits);
    }

    /// <summary>Atomic snapshot + reset of the within-buffer max. A non-zero value here just
    /// means there was a sharp transient INSIDE a delivered buffer — almost always real audio
    /// content (percussion hit, syllable onset, etc.). Useful as the "this is just music"
    /// baseline against which the cross-buffer max is interpreted.</summary>
    public float TakeMaxWithinBuffer()
    {
        var bits = Interlocked.Exchange(ref maxWithinBufferStepBits, 0);
        return BitConverter.Int32BitsToSingle((int)bits);
    }

    private static float ReadMax(ref long field) =>
        BitConverter.Int32BitsToSingle((int)Volatile.Read(ref field));

    private static void WriteMaxIfGreater(ref long field, float candidate)
    {
        var candidateBits = (long)BitConverter.SingleToInt32Bits(candidate);
        long current;
        do
        {
            current = Volatile.Read(ref field);
            var currentValue = BitConverter.Int32BitsToSingle((int)current);
            if (candidate <= currentValue) return;
        } while (Interlocked.CompareExchange(ref field, candidateBits, current) != current);
    }
}
