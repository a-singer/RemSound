namespace RemSound.Core;

/// <summary>
/// Per-stage sample-step "discontinuity detector" for diagnosing where in the audio pipeline
/// pops are being introduced. A single-sample step magnitude is the absolute difference
/// between consecutive samples of the same channel; a typical "click" in real audio shows
/// up as a step well above what naturally occurs in music or speech content.
///
/// Each probe holds the maximum step observed across all calls to <see cref="ScanStereo"/>
/// since the last <see cref="TakeMax"/>. The diag log polls TakeMax once per second to
/// emit the worst step at that pipeline stage. Comparing the max across stages — sender
/// pre-encode, receiver post-decode, receiver post-ring-read, receiver post-resampler,
/// final output — reveals which stage introduces the click.
///
/// Thread model: writes are lock-free CAS-update of a long-encoded float bit pattern (so
/// one probe can be hit from multiple threads if needed). Read-and-reset is also atomic.
/// Buffers are scanned cheaply — one subtract + abs + compare per sample — and the whole
/// scan is gated by <see cref="DiagnosticsGate.Enabled"/> so it pays nothing in production
/// when logging is off.
/// </summary>
public sealed class AudioStepProbe
{
    private long maxStepBits;
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
        var max = ReadMax();
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
            if (i == 0 && !seedFromPrev) prev = s;
            var step = s - prev;
            if (step < 0f) step = -step;
            if (step > max) max = step;
            prev = s;
        }
        lastL = prev;
        hasLast = true;
        WriteMaxIfGreater(max);
    }

    /// <summary>Scan an interleaved stereo float span and update the max step. Cheap; safe
    /// to call from any thread. No-op if diagnostics are disabled.</summary>
    public void ScanStereo(ReadOnlySpan<float> stereoFloats)
    {
        if (!DiagnosticsGate.Enabled) return;
        if (stereoFloats.IsEmpty) return;
        var max = ReadMax();
        var prevL = lastL;
        var prevR = lastR;
        var seedFromPrev = hasLast;
        // Pair walk. For samples after the first, compare to the previous sample of the
        // same channel from THIS buffer. For the first pair, compare to the saved
        // last-sample-from-the-previous-buffer if available.
        for (var i = 0; i + 1 < stereoFloats.Length; i += 2)
        {
            var l = stereoFloats[i];
            var r = stereoFloats[i + 1];
            float stepL, stepR;
            if (i == 0)
            {
                if (!seedFromPrev) { prevL = l; prevR = r; }
                stepL = l - prevL;
                stepR = r - prevR;
            }
            else
            {
                stepL = l - stereoFloats[i - 2];
                stepR = r - stereoFloats[i - 1];
            }
            var absL = stepL < 0f ? -stepL : stepL;
            var absR = stepR < 0f ? -stepR : stepR;
            if (absL > max) max = absL;
            if (absR > max) max = absR;
        }
        // Save the last sample of this buffer for the next scan.
        var lastIdx = stereoFloats.Length - 2;
        lastL = stereoFloats[lastIdx];
        lastR = stereoFloats[lastIdx + 1];
        hasLast = true;
        WriteMaxIfGreater(max);
    }

    /// <summary>Atomic snapshot of the current max + reset to zero. Returns the value as
    /// a float in the same units as the input (i.e. 0.5 = a 0.5-magnitude single-sample
    /// step, which is a 6 dB jump and definitely audible).</summary>
    public float TakeMax()
    {
        var bits = Interlocked.Exchange(ref maxStepBits, 0);
        return BitConverter.Int32BitsToSingle((int)bits);
    }

    private float ReadMax() => BitConverter.Int32BitsToSingle((int)Volatile.Read(ref maxStepBits));

    private void WriteMaxIfGreater(float candidate)
    {
        var candidateBits = (long)BitConverter.SingleToInt32Bits(candidate);
        long current;
        do
        {
            current = Volatile.Read(ref maxStepBits);
            var currentValue = BitConverter.Int32BitsToSingle((int)current);
            if (candidate <= currentValue) return;
        } while (Interlocked.CompareExchange(ref maxStepBits, candidateBits, current) != current);
    }
}
