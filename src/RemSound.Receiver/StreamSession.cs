using System.Net;
using System.Runtime.InteropServices;
using Concentus;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Owns the per-sender decode pipeline. One sender = one StreamSession at a time. When a new
/// sender appears (different remote endpoint, or stream/codec change), the receiver swaps in a
/// new session — old buffered audio drains out of the playout buffer naturally during the
/// swap rather than being thrown away mid-playback.
///
/// All work runs on the network listener's thread. No locks; the only cross-thread interaction
/// is writing decoded float frames to the SPSC <see cref="AudioRingBuffer"/>.
/// </summary>
internal sealed class StreamSession : IDisposable
{
    private readonly SessionPlayout sessionPlayout;
    private readonly ReceiverDiagnostics diagnostics;
    private readonly Action<int> onFramesQueued;
    private readonly PcmFrameAssembler pcmAssembler = new();
    private IOpusDecoder? opusDecoder;
    // Sequence-tracking for Opus FEC recovery. uint, so wrap-around is naturally
    // handled by the (current - expected == 1U) comparison at gap detection.
    private uint? expectedNextSequence;
    /// <summary>Number of single-packet gaps recovered using inband FEC from the next packet.</summary>
    public long OpusFecRecoveries { get; private set; }
    /// <summary>Number of multi-packet gaps where FEC could not help (only logs once per occurrence).</summary>
    public long OpusUnrecoveredGaps { get; private set; }

    public IPEndPoint Endpoint { get; }
    public ushort StreamId { get; }
    public AudioFormatInfo Format { get; }
    public AudioTransportCodec Codec => (AudioTransportCodec)Format.Codec;

    /// <summary>UTC timestamp of the most recent decoded-audio write into this session's
    /// playout buffer. <see cref="AudioReceiver.PruneIdleSessions"/> reaps on this directly,
    /// rather than a cross-dictionary lookup into PlayoutEngine that could miss and strand
    /// the session forever — a reconnecting peer never reuses its old (endpoint, streamId)
    /// key, so its previous session is always an orphan that must be reaped by idle age.</summary>
    public DateTime LastWriteUtc => sessionPlayout.LastWriteUtc;

    /// <summary>For PCM streams: number of incoming packets the assembler rejected outright.</summary>
    public long PcmFrameRejections => pcmAssembler.RejectionCount;
    /// <summary>For PCM streams: number of partially-assembled frames discarded mid-flight.</summary>
    public long PcmFrameDiscardedPartials => pcmAssembler.DiscardedPartialCount;

    // Post-decode discontinuity probe. Scans the float buffer right after Int24LEToFloat
    // (PCM) or short-to-float (Opus) so we can compare to the sender's pre-encode probe and
    // detect any wire-level or decode-level artefacts. Same buffer is then handed to the
    // session playout, so the post-ring-read probe in SessionPlayout sees the exact same
    // samples a moment later (after riding through the ring buffer).
    private readonly AudioStepProbe postDecodeStepProbe = new();
    public float TakeMaxPostDecodeStep() => postDecodeStepProbe.TakeMax();

    // === Wire-level sequence tracking (Phase 5, 2026-05-14) ===
    // Every audio packet carries a wire sequence number that monotonically increases per
    // session (audioSequence in SenderLane). The Opus path uses this for FEC recovery. The
    // PCM path historically ignored it entirely. Now we track it to detect:
    //   * MISSING packets — sequence > expected (gap > 1 frames)
    //   * REORDERED packets — sequence < expected (a packet arrived after a later one)
    //   * DUPLICATE packets — sequence == previous (same packet delivered twice)
    //   * IN-ORDER packets — sequence == expected
    //
    // Any of MISSING / REORDERED / DUPLICATE on a healthy LAN would point straight at a
    // transport-level issue (NIC offload bug, switch buffer overflow, RSS hash collision
    // causing packets to take different queues). MISSING on PCM = silent audio drop at
    // the packet boundary = audible click. REORDERED = the receiver processes audio in
    // the wrong order = audible click. DUPLICATE = same audio played twice in a row =
    // audible click.
    private uint? expectedNextWireSequence;
    private long wireInOrderTotal;
    private long wireMissedTotal;     // sum of missing-packet counts (sequence > expected by N → +N)
    private long wireReorderedTotal;  // count of times a sequence < expected arrived
    private long wireDuplicatedTotal; // count of times a sequence == previous arrived
    public long WireInOrderCount => Interlocked.Read(ref wireInOrderTotal);
    public long WireMissedCount => Interlocked.Read(ref wireMissedTotal);
    public long WireReorderedCount => Interlocked.Read(ref wireReorderedTotal);
    public long WireDuplicatedCount => Interlocked.Read(ref wireDuplicatedTotal);

    public StreamSession(
        IPEndPoint endpoint,
        ushort streamId,
        AudioFormatInfo format,
        SessionPlayout sessionPlayout,
        ReceiverDiagnostics diagnostics,
        Action<int> onFramesQueued)
    {
        Endpoint = endpoint;
        StreamId = streamId;
        Format = format;
        this.sessionPlayout = sessionPlayout;
        this.diagnostics = diagnostics;
        this.onFramesQueued = onFramesQueued;

        if (Codec == AudioTransportCodec.Opus)
        {
            opusDecoder = OpusCodecFactory.CreateDecoder(format.SampleRate, format.Channels, TextWriter.Null);
        }
    }

    /// <summary>Returns true if this session matches the given format identity (codec/rate/channels/frame).</summary>
    public bool MatchesFormat(IPEndPoint endpoint, ushort streamId, AudioFormatInfo format) =>
        Endpoint.Equals(endpoint)
        && StreamId == streamId
        && Format.Codec == format.Codec
        && Format.SampleRate == format.SampleRate
        && Format.Channels == format.Channels
        && Format.FrameDurationMilliseconds == format.FrameDurationMilliseconds;

    public bool IsSameEndpoint(IPEndPoint endpoint) => Endpoint.Equals(endpoint);

    public bool HandleAudioPayload(uint sequence, ReadOnlySpan<byte> payload)
    {
        diagnostics.RecordPacketArrived();
        TrackWireSequence(sequence);
        return Codec switch
        {
            AudioTransportCodec.Pcm => HandlePcm(payload),
            AudioTransportCodec.Opus => HandleOpus(sequence, payload),
            _ => false,
        };
    }

    /// <summary>
    /// Classify each arriving packet against the expected next wire sequence:
    /// IN-ORDER (== expected), MISSING (> expected, diff sample frames), REORDERED (< expected
    /// but within a small sane window), DUPLICATE (== previous). On the very first packet we
    /// just seed expected and bail. On a wild jump (huge gap) we treat it as a re-sync rather
    /// than logging hundreds of thousands of "missing" packets — this can happen if the sender
    /// restarts mid-session or a router drops a long burst.
    /// All counters use Interlocked because the readers are on the UI thread.
    /// </summary>
    private void TrackWireSequence(uint sequence)
    {
        if (expectedNextWireSequence is not uint expected)
        {
            expectedNextWireSequence = sequence + 1U;
            Interlocked.Increment(ref wireInOrderTotal);
            return;
        }

        if (sequence == expected)
        {
            Interlocked.Increment(ref wireInOrderTotal);
            expectedNextWireSequence = sequence + 1U;
            return;
        }

        // Treat the gap as an unsigned forward gap. If it's small-ish (< 1M packets, well over
        // 10 minutes of audio at our packet rates) treat as forward MISSING. If it's huge,
        // assume sequence ran backwards (reorder or restart).
        uint forwardGap = sequence - expected;
        if (forwardGap < 1_000_000U)
        {
            // Forward jump → forwardGap packets we never saw at the expected slot.
            Interlocked.Add(ref wireMissedTotal, forwardGap);
            expectedNextWireSequence = sequence + 1U;
        }
        else
        {
            // Backward jump. Distance behind expected:
            uint backwardDistance = expected - sequence;
            if (backwardDistance == 1U)
            {
                // sequence == previous (the one just before expected) → duplicate.
                Interlocked.Increment(ref wireDuplicatedTotal);
            }
            else
            {
                // Out-of-order arrival from further back.
                Interlocked.Increment(ref wireReorderedTotal);
            }
            // Do NOT roll expectedNextWireSequence backwards — that would re-count the
            // already-missing packets when the originally-expected packet arrives.
        }
    }

    public void Dispose() { /* IOpusDecoder has no Dispose; nothing else to free */ }

    // === PCM ===

    private bool HandlePcm(ReadOnlySpan<byte> payload)
    {
        if (!RemPcmFrame.TryReadSubHeader(payload, out var frameId, out var partIndex, out var totalParts))
        {
            return false;
        }

        var partBytes = payload[RemPcmFrame.SubHeaderSize..];
        if (!pcmAssembler.TryAssemble(partBytes, frameId, partIndex, totalParts, out var assembled))
        {
            return true; // pending or dropped due to mismatch — not an error condition
        }

        // assembled is signed int24 LE, stereo. Convert to float32 and queue.
        var sampleCount = assembled.Length / 3;
        var floatBytes = sampleCount * sizeof(float);
        Span<byte> floatScratch = floatBytes <= 16 * 1024 ? stackalloc byte[floatBytes] : new byte[floatBytes];
        var floatSpan = MemoryMarshal.Cast<byte, float>(floatScratch);
        PcmPack.Int24LEToFloat(assembled, floatSpan);

        // Discontinuity probe — what does the audio look like right after we decode it?
        // Compared to the sender's pre-encode probe, a higher value here would mean the
        // wire codec roundtrip introduced steps. Same probe is also useful as a baseline
        // for the post-ring-read probe in SessionPlayout.
        postDecodeStepProbe.ScanStereo(floatSpan);

        sessionPlayout.Write(floatScratch);
        onFramesQueued(sampleCount / Format.Channels);
        return true;
    }

    // === Opus ===

    private bool HandleOpus(uint sequence, ReadOnlySpan<byte> payload)
    {
        if (opusDecoder is null) return false;

        var frameSize = Math.Max(1, Format.SampleRate * Math.Max(5, Format.FrameDurationMilliseconds) / 1000);
        var totalShorts = frameSize * Format.Channels;
        Span<short> shortScratch = totalShorts <= 4096 ? stackalloc short[totalShorts] : new short[totalShorts];

        // Detect a single-packet gap. If the previous packet was N and this is N+2,
        // we know N+1 was lost; this packet's payload contains FEC redundancy for
        // it. Decode the FEC frame first (so audio plays in order), then the
        // current frame. Wrap-around with uint subtraction is intentional.
        bool useFecRecovery = false;
        if (expectedNextSequence is uint expected)
        {
            uint gap = sequence - expected; // 0 = exactly expected, 1 = one missing, 2+ = multi-loss
            if (gap == 1)
            {
                useFecRecovery = true;
            }
            else if (gap > 1 && gap < 1_000_000)
            {
                // Multi-packet loss — FEC can only recover one. Don't try.
                OpusUnrecoveredGaps++;
            }
            // gap == 0 OR a wild jump (gap >= 1M, e.g. stream reset) → no recovery
        }

        if (useFecRecovery)
        {
            try
            {
                var fecDecoded = opusDecoder.Decode(payload, shortScratch, frameSize, true);
                if (fecDecoded > 0)
                {
                    EmitDecoded(shortScratch, fecDecoded);
                    OpusFecRecoveries++;
                }
            }
            catch
            {
                // FEC recovery is best-effort; if it fails, fall through to the
                // normal decode and accept a single click rather than crashing.
            }
        }

        int decoded;
        try
        {
            decoded = opusDecoder.Decode(payload, shortScratch, frameSize, false);
        }
        catch
        {
            return false;
        }
        if (decoded <= 0) return false;

        EmitDecoded(shortScratch, decoded);
        expectedNextSequence = sequence + 1U;
        return true;
    }

    private void EmitDecoded(ReadOnlySpan<short> shortScratch, int sampleCountPerChannel)
    {
        var floatCount = sampleCountPerChannel * Format.Channels;
        var floatBytes = floatCount * sizeof(float);
        Span<byte> floatScratch = floatBytes <= 16 * 1024 ? stackalloc byte[floatBytes] : new byte[floatBytes];
        var floatSpan = MemoryMarshal.Cast<byte, float>(floatScratch);
        for (var i = 0; i < floatCount; i++) floatSpan[i] = shortScratch[i] / 32768f;

        sessionPlayout.Write(floatScratch);
        onFramesQueued(sampleCountPerChannel);
    }
}
