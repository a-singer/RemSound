using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// One outbound audio stream's worth of state. Each lane owns its own streamId, audio
/// sequence counter, frame accumulator, Opus encoder, format-resend timer and PCM frame id.
/// AudioSender holds one or more of these — in the three classic modes (WasapiOnly,
/// AsioOnly, Both) there is exactly one lane and behaviour is identical to the pre-refactor
/// monolithic AudioSender. The BothIndependent mode (Stage 4) instantiates two: a WASAPI
/// lane fed by the WASAPI capture child and an ASIO lane fed by the ASIO capture child, each
/// producing its own UDP stream on its own streamId, multiplexed by the receiver's
/// (endpoint, streamId) keying.
///
/// Threading: the hot-path methods (<see cref="OnMixedSamples"/> and below) are called from
/// the capture engine's callback thread. Each lane has exactly one such thread feeding it.
/// Cross-thread state read from AudioSender (codec, mute, opusFrameMs, etc.) goes through
/// volatile fields on the owner. Configuration mutations (<see cref="ConfigureCodec"/>,
/// <see cref="OnPcmFrameSizeChanged"/>) come from the UI thread; they take the same
/// configGate that AudioSender does to serialise streamId rotation against in-flight
/// accumulator writes — see AudioSender for the gate.
/// </summary>
internal sealed class SenderLane
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MaxFrameStereoSamples = MixSampleRate * 20 / 1000 * MixChannels; // 1920, Opus 20 ms
    private const int FormatResendIntervalMs = 250;

    private readonly AudioSender owner;
    private readonly int opusBitrate;

    // Hot-path scratch. Sized to the largest possible single frame (Opus 20 ms = 1920 stereo
    // samples). PCM 5 ms uses only the first 480, Opus 10 ms only the first 960. Reusing one
    // buffer means no realloc on codec change. outboundScratch is per-lane so two lanes don't
    // step on each other's packet construction.
    private readonly float[] frameAccumulator = new float[MaxFrameStereoSamples];
    private int frameAccumulatorWritten;
    private readonly byte[] outboundScratch = new byte[2048];

    // Per-stream sequence counters. audioSequence is what the receiver's gap-detector and Opus
    // FEC look at — it must stay monotonic per stream. formatSequence is used for the periodic
    // format-announce packet; receiver doesn't sequence-check format packets but having a
    // separate counter keeps the audio FEC clean (see AudioSender.audioSequence comment for
    // the original reasoning).
    private uint audioSequence;
    private uint pcmFrameId;
    private uint formatSequence;
    private ushort streamId;
    private DateTime lastFormatPacketUtc = DateTime.MinValue;

    private OpusEncoderState opusEncoder;
    private int opusFrameStereoSamples;

    // Per-lane pre-encode discontinuity probe. Moved here from AudioSender (2026-05-15) so
    // each lane has its OWN probe state and the cross-buffer step measurement (which carries
    // lastL/lastR across calls) only sees samples from one continuous audio stream. With the
    // earlier shared-probe design, BothIndependent mode mixed two unrelated streams' samples
    // into the same probe's cross-buffer carry, producing synthetic "steps" of arbitrary
    // magnitude every time the two lanes' callbacks interleaved — making the diag log unable
    // to tell a real capture glitch from instrumentation aliasing. Per-lane separation fixes
    // that without changing what the probe measures.
    private readonly AudioStepProbe preEncodeStepProbe = new();
    public float TakeMaxPreEncodeStep() => preEncodeStepProbe.TakeMax();
    public float TakeMaxPreEncodeStepCrossBuffer() => preEncodeStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxPreEncodeStepWithinBuffer() => preEncodeStepProbe.TakeMaxWithinBuffer();

    // Which render route this lane announces in its format packets. The receiver reads the
    // Lane byte on the wire and tags the matching SessionPlayout, which makes PlayoutEngine
    // route the lane's audio to the corresponding per-route IWaveProvider surface (lane
    // backends in BothIndependent mode; the legacy Mixed surface in every classic mode).
    // Default Mixed = classic-mode behaviour, indistinguishable from a pre-2026-05-11 sender.
    // BothIndependent assigns WasapiLane / AsioLane to the two SenderLanes at mode-change
    // time via SetRoute.
    private volatile RenderRoute route = RenderRoute.Mixed;
    public RenderRoute Route => route;

    public ushort StreamId => streamId;

    public SenderLane(AudioSender owner, int initialOpusFrameMs, int opusBitrate)
    {
        this.owner = owner;
        this.opusBitrate = opusBitrate;
        opusEncoder = new OpusEncoderState(initialOpusFrameMs, opusBitrate);
        opusFrameStereoSamples = opusEncoder.FrameSizePerChannel * MixChannels;
        streamId = NewStreamId();
    }

    private static ushort NewStreamId() => (ushort)Random.Shared.Next(1, ushort.MaxValue);

    /// <summary>
    /// Set this lane's render route. Called by AudioSender when audio-mode changes — e.g.
    /// switching into BothIndependent flips the default lane from Mixed to WasapiLane and
    /// activates the asio lane as AsioLane. Rotates streamId and forces an immediate format
    /// re-announce so the receiver opens a fresh session with the new Lane tag rather than
    /// continuing to route the existing session under the old tag.
    /// </summary>
    public void SetRoute(RenderRoute newRoute)
    {
        if (route == newRoute) return;
        route = newRoute;
        streamId = NewStreamId();
        lastFormatPacketUtc = DateTime.MinValue;
        frameAccumulatorWritten = 0;
    }

    /// <summary>Reset per-lane counters and pick a new streamId. Called from
    /// <see cref="AudioSender.Start"/> so the receiver sees a fresh session on each start.</summary>
    public void ResetForStart()
    {
        streamId = NewStreamId();
        audioSequence = 0;
        pcmFrameId = 0;
        formatSequence = 0;
        frameAccumulatorWritten = 0;
        lastFormatPacketUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Codec just changed. Rotates streamId (the receiver opens a fresh session at the new
    /// format), rebuilds the Opus encoder if Opus is in play, and zeroes the accumulator so
    /// any half-filled frame from the previous format doesn't leak into the new one.
    /// </summary>
    public void OnCodecChanged(AudioTransportCodec newCodec, int opusFrameMs)
    {
        if (newCodec == AudioTransportCodec.Opus)
        {
            opusEncoder = new OpusEncoderState(opusFrameMs, opusBitrate);
            opusFrameStereoSamples = opusEncoder.FrameSizePerChannel * MixChannels;
        }
        streamId = NewStreamId();
        lastFormatPacketUtc = DateTime.MinValue;
        frameAccumulatorWritten = 0;
    }

    /// <summary>PCM frame size just changed. Rotates streamId so the receiver sees a fresh
    /// session at the new packet cadence and resets the accumulator. No encoder rebuild —
    /// Opus is unaffected by the PCM send-rate setting.</summary>
    public void OnPcmFrameSizeChanged()
    {
        streamId = NewStreamId();
        lastFormatPacketUtc = DateTime.MinValue;
        frameAccumulatorWritten = 0;
    }

    // === hot path ===

    public void OnMixedSamples(ReadOnlyMemory<float> stereoFloats)
    {
        var span = stereoFloats.Span;
        if (span.IsEmpty) return;

        // Whole-callback timing — captures encode plus kernel send for the SNAP's emitMs
        // column. Skipped entirely when diagnostics are off so the audio thread doesn't pay
        // two Stopwatch reads + a CAS loop per callback for a number nobody is going to log.
        var diag = RemSound.Core.DiagnosticsGate.Enabled;
        var emitStart = diag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        EnsureFormatPacketSent();

        // Recording tap — the recorder gets the float audio about to be encoded. The lane
        // doesn't know whether the recorder is running; the dispatcher early-outs when no
        // callback is wired. Captured here (before encoding) so the recording is bit-clean
        // float, independent of which codec the wire is using. The lane tag is forwarded so
        // BothIndependent mode (where both WASAPI and ASIO SenderLanes fire on every capture
        // callback) can be correctly handled by the recorder — each lane writes into its own
        // ring, and the recorder mixes them rather than appending them sequentially (which
        // would double the file's effective sample rate). 2026-05-15 fix.
        owner.DispatchSentSamples(stereoFloats, route);

        // Discontinuity probe — what does the audio look like just before we encode it?
        // Compared to the receiver's per-stage probes, this tells us whether artefacts are
        // present at the sender side already (capture hardware glitch, mix-bus issue) or
        // introduced somewhere in the wire / decode / playout chain. Per-lane probe — see
        // <see cref="preEncodeStepProbe"/> field comment for why this isn't shared with the
        // other lane in BothIndependent.
        preEncodeStepProbe.ScanStereo(span);

        switch (owner.Codec)
        {
            case AudioTransportCodec.Pcm:
                ProcessPcm(span);
                break;
            case AudioTransportCodec.Opus:
                ProcessOpus(span);
                break;
        }
        if (diag) owner.RecordEmitTicks(System.Diagnostics.Stopwatch.GetTimestamp() - emitStart);
    }

    private void ProcessPcm(ReadOnlySpan<float> samples)
    {
        // Tight-latency mode: emit each delivered sample buffer as its own packet instead of
        // accumulating to the PCM frame size. Saves up to (frame_size_ms / 2) of average
        // accumulator delay. Variable packet size per call. Cap at 240 stereo-frames (5 ms =
        // 1440 bytes) to stay under MaxAudioPayloadBytes=1454; in normal ASIO buffer sizes
        // (64/128) this cap is never hit.
        if (owner.IsTightLatencyEnabled)
        {
            const int MaxStereoSamplesPerPacket = 240 * MixChannels;
            var pos = 0;
            while (pos < samples.Length)
            {
                var chunk = Math.Min(MaxStereoSamplesPerPacket, samples.Length - pos);
                EmitPcmFrame(samples.Slice(pos, chunk));
                pos += chunk;
            }
            return;
        }

        var pcmFrameStereoSamples = owner.PcmFrameStereoSamples;
        var idx = 0;
        while (idx < samples.Length)
        {
            var spaceLeftForPcmFrame = pcmFrameStereoSamples - frameAccumulatorWritten;
            var copy = Math.Min(spaceLeftForPcmFrame, samples.Length - idx);
            samples.Slice(idx, copy).CopyTo(frameAccumulator.AsSpan(frameAccumulatorWritten));
            frameAccumulatorWritten += copy;
            idx += copy;

            if (frameAccumulatorWritten == pcmFrameStereoSamples)
            {
                EmitPcmFrame(frameAccumulator.AsSpan(0, pcmFrameStereoSamples));
                frameAccumulatorWritten = 0;
            }
        }
    }

    private void ProcessOpus(ReadOnlySpan<float> samples)
    {
        var frameSamples = opusFrameStereoSamples;
        var idx = 0;
        while (idx < samples.Length)
        {
            var spaceLeft = frameSamples - frameAccumulatorWritten;
            var copy = Math.Min(spaceLeft, samples.Length - idx);
            samples.Slice(idx, copy).CopyTo(frameAccumulator.AsSpan(frameAccumulatorWritten));
            frameAccumulatorWritten += copy;
            idx += copy;

            if (frameAccumulatorWritten == frameSamples)
            {
                EmitOpusFrame(frameAccumulator.AsSpan(0, frameSamples));
                frameAccumulatorWritten = 0;
            }
        }
    }

    private void EmitPcmFrame(ReadOnlySpan<float> stereoFloats)
    {
        var bytesOnWire = stereoFloats.Length * 3;
        Span<byte> int24 = stackalloc byte[bytesOnWire];
        if (owner.IsMuted)
        {
            int24.Clear();
        }
        else
        {
            PcmPack.FloatToInt24LE(stereoFloats, int24);
        }
        pcmFrameId++;
        SendPcmPart(pcmFrameId, partIndex: 0, totalParts: 1, int24);
    }

    private void EmitOpusFrame(ReadOnlySpan<float> stereoFloats)
    {
        ReadOnlySpan<byte> opusBytes;
        if (owner.IsMuted)
        {
            Span<float> silence = stackalloc float[opusFrameStereoSamples];
            silence.Clear();
            var muteLen = opusEncoder.Encode(silence);
            opusBytes = opusEncoder.LastEncoded(muteLen);
        }
        else
        {
            var len = opusEncoder.Encode(stereoFloats);
            if (len <= 0) return;
            opusBytes = opusEncoder.LastEncoded(len);
        }
        SendAudio(opusBytes);
    }

    // === wire path ===

    private void EnsureFormatPacketSent()
    {
        if (DateTime.UtcNow - lastFormatPacketUtc < TimeSpan.FromMilliseconds(FormatResendIntervalMs)) return;
        lastFormatPacketUtc = DateTime.UtcNow;

        // PCM FrameDurationMilliseconds: receiver only uses this for buffer sizing and
        // diagnostics, not for decode. Round 2.5 ms up to ≥1 to keep the wire field integer.
        var pcmFrameMs = owner.PcmFrameSamplesPerChannel * 1000 / MixSampleRate;
        if (pcmFrameMs < 1) pcmFrameMs = 1;
        var codec = owner.Codec;
        var opusFrameMs = owner.OpusFrameMilliseconds;
        // Pass this lane's current Route as the Lane field. In classic-mode senders this is
        // Mixed and the receiver routes the session to its legacy mix bus; in BothIndependent
        // senders this is WasapiLane or AsioLane and the receiver routes to the matching
        // per-route IWaveProvider surface.
        var format = codec == AudioTransportCodec.Opus
            ? new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, opusFrameMs, route)
            : new AudioFormatInfo(48000, 2, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, pcmFrameMs, route);

        // Allocate the extended (36-byte) format payload — see RemPacket.FormatPayloadExtendedSize
        // for the backward-compat contract. Old receivers parse the first 32 bytes and ignore
        // the rest; new receivers read the Lane byte to decide which render route this stream
        // belongs to. The Lane value carried here comes from the AudioFormatInfo constructed
        // above, which currently always sets Mixed for the default lane; Stage 4 will set
        // WasapiLane / AsioLane on the second lane in BothIndependent mode.
        Span<byte> packet = stackalloc byte[RemPacket.HeaderSize + RemPacket.FormatPayloadExtendedSize];
        RemPacket.WriteHeader(packet, RemPacketType.Format, streamId, ++formatSequence);
        RemPacket.WriteFormatPayload(packet[RemPacket.HeaderSize..], format);
        owner.SendToAll(packet);
    }

    private void SendPcmPart(uint frameId, byte partIndex, byte totalParts, ReadOnlySpan<byte> partBytes)
    {
        var headerSize = RemPacket.HeaderSize;
        var subHeaderSize = RemPcmFrame.SubHeaderSize;
        var totalLen = headerSize + subHeaderSize + partBytes.Length;
        var dst = outboundScratch.AsSpan(0, totalLen);
        RemPacket.WriteHeader(dst, RemPacketType.Audio, streamId, ++audioSequence);
        RemPcmFrame.WriteSubHeader(dst.Slice(headerSize, subHeaderSize), frameId, partIndex, totalParts);
        partBytes.CopyTo(dst[(headerSize + subHeaderSize)..]);
        owner.SendToAll(dst);
    }

    private void SendAudio(ReadOnlySpan<byte> opusBytes)
    {
        var totalLen = RemPacket.HeaderSize + opusBytes.Length;
        var dst = outboundScratch.AsSpan(0, totalLen);
        RemPacket.WriteHeader(dst, RemPacketType.Audio, streamId, ++audioSequence);
        opusBytes.CopyTo(dst[RemPacket.HeaderSize..]);
        owner.SendToAll(dst);
    }
}
