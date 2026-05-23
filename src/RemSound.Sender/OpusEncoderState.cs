using Concentus;
using Concentus.Enums;

namespace RemSound.Sender;

/// <summary>
/// Wraps a Concentus Opus encoder configured for real-time low-latency 48 kHz stereo audio.
/// Frame size is selectable at construction (10 ms or 20 ms). Receiver auto-handles whatever
/// frame size the sender announces in the format packet — no coordination required.
///
/// 2026-05-23 — switched from the <c>Encode(ReadOnlySpan&lt;short&gt;...)</c> overload to the
/// float overload after the first allocation-rate measurement (Part C, item 51 of
/// RemSoundefficiency.md). The float overload skips one internal float→short→float round trip
/// inside Concentus (CELT runs in float natively in RESTRICTED_LOWDELAY mode), and lets us
/// drop our own per-sample Math.Clamp + cast loop — Concentus' float overload does its own
/// out-of-range clipping per its XML docs. Same encoder configuration, same bitrate, same
/// frame size, same audio output bit-for-bit.
/// </summary>
internal sealed class OpusEncoderState : IDisposable
{
    public const int Channels = 2;
    private const int PacketBufferBytes = 4000;

    private readonly IOpusEncoder encoder;
    private readonly byte[] packetScratch = new byte[PacketBufferBytes];

    public int FrameMilliseconds { get; }
    public int FrameSizePerChannel { get; }

    public OpusEncoderState(int frameMilliseconds, int bitrate)
    {
        // RESTRICTED_LOWDELAY supports 2.5/5/10/20 ms frames. 10 ms = lowest practical latency,
        // 20 ms = same bitrate but more robust to packet loss (each lost packet is half the audio
        // share). We expose 10 and 20 as the user-selectable choices.
        FrameMilliseconds = Math.Clamp(frameMilliseconds, 5, 60);
        FrameSizePerChannel = 48000 * FrameMilliseconds / 1000;

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
