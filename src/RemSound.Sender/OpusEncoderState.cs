using Concentus;
using Concentus.Enums;

namespace RemSound.Sender;

/// <summary>
/// Wraps a Concentus Opus encoder configured for real-time low-latency 48 kHz stereo audio.
/// Frame size is selectable at construction as samples-per-channel. Receiver auto-handles
/// whatever frame size the sender announces in the format packet — no coordination required.
///
/// 2026-05-23 (a) — switched from the <c>Encode(ReadOnlySpan&lt;short&gt;...)</c> overload to
/// the float overload after the first allocation-rate measurement (Part C, item 51 of
/// RemSoundefficiency.md). The float overload skips one internal float→short→float round
/// trip inside Concentus (CELT runs in float natively in RESTRICTED_LOWDELAY mode), and
/// lets us drop our own per-sample Math.Clamp + cast loop — Concentus' float overload does
/// its own out-of-range clipping per its XML docs. Same encoder configuration, same bitrate,
/// same audio output bit-for-bit.
///
/// 2026-05-23 (b) — constructor parameter switched from milliseconds to samples-per-channel
/// as part of the v3.0 wire-format refactor. Lets us express the 2.5 ms (= 120 samples)
/// experimental low-latency mode cleanly without floating-point ms, and removes the
/// <c>48000 * ms / 1000</c> conversion (which lost precision below 1 ms boundaries). Encoder
/// configuration is otherwise identical.
/// </summary>
internal sealed class OpusEncoderState : IDisposable
{
    public const int Channels = 2;
    private const int PacketBufferBytes = 4000;

    private readonly IOpusEncoder encoder;
    private readonly byte[] packetScratch = new byte[PacketBufferBytes];

    public int FrameSizePerChannel { get; }

    public OpusEncoderState(int frameSamplesPerChannel, int bitrate)
    {
        // RESTRICTED_LOWDELAY supports 2.5/5/10/20 ms frames at 48 kHz = 120/240/480/960
        // samples-per-channel. 120 (2.5 ms) is the lowest standard-Opus frame size. We clamp
        // to the legal Opus range; the UI never offers a value outside it but a corrupt
        // setting can't crash the encoder constructor.
        FrameSizePerChannel = Math.Clamp(frameSamplesPerChannel, 120, 2880);

        encoder = OpusCodecFactory.CreateEncoder(48000, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY, TextWriter.Null);
        encoder.Bitrate = bitrate;
        encoder.Complexity = 10;
        encoder.UseVBR = true;
        // Inband forward error correction. Each encoded packet carries a low-bitrate
        // copy of the PREVIOUS packet's audio. The receiver only uses it when it
        // detects a single-packet gap, so on a clean line FEC costs almost nothing
        // (the encoder gets a few extra bytes of headroom from VBR). On a lossy
        // link it lets the receiver fill a single missing packet without waiting
        // — recovery without buffering.
        encoder.UseInbandFEC = true;
        // Tells the encoder how aggressively to bias FEC redundancy. 10% is a
        // sensible value for an internet link via Tailscale: enough redundancy to
        // recover most one-packet drops, not so much that we sacrifice quality on
        // a clean network. Concentus accepts 0..100.
        encoder.PacketLossPercent = 10;
    }

    /// <summary>Encode one frame at the configured frame size. Returns bytes written.</summary>
    public int Encode(ReadOnlySpan<float> stereoFloats)
    {
        if (stereoFloats.Length != FrameSizePerChannel * Channels)
        {
            throw new ArgumentException($"Expected {FrameSizePerChannel * Channels} samples, got {stereoFloats.Length}", nameof(stereoFloats));
        }

        // Direct float→Opus path. Concentus' float-input Encode overload normalises and clips
        // out-of-range samples internally (per its XML doc) — so the Math.Clamp loop we used
        // to run on every sample before calling the int16 overload is no longer needed. That
        // also lets us delete the pcm16Scratch field entirely.
        return encoder.Encode(stereoFloats, FrameSizePerChannel, packetScratch.AsSpan(), packetScratch.Length);
    }

    public ReadOnlySpan<byte> LastEncoded(int length) => packetScratch.AsSpan(0, length);

    public void Dispose() { /* IOpusEncoder is finalized by GC, no Dispose */ }
}
