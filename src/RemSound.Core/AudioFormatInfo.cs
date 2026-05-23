namespace RemSound.Core;

/// <summary>
/// Audio format announcement carried in every Format packet. <see cref="Lane"/> was added
/// 2026-05-11 alongside the BothIndependent audio mode — see <see cref="RenderRoute"/> for
/// the semantics. The field is wire-backward-compatible: old receivers parse the first 32
/// bytes of the format payload and ignore the extra; new receivers reading a 32-byte
/// payload from an old sender default Lane to <see cref="RenderRoute.Mixed"/>.
///
/// 2026-05-23 — <c>FrameDurationMilliseconds</c> renamed to <c>FrameSamplesPerChannel</c>
/// (v3.0 wire-format change). Previously held an int millisecond value; now holds the exact
/// sample-count per channel at the announced <see cref="SampleRate"/>. Conversion is
/// <c>ms = FrameSamplesPerChannel * 1000 / SampleRate</c> for display. The change exists
/// because Opus's 2.5 ms RESTRICTED_LOWDELAY frame (= 120 samples at 48 kHz) can't be
/// expressed cleanly in integer milliseconds; using sample-count also removes a lossy
/// conversion that the encoder/decoder pipeline previously did at every announcement.
/// v2.x receivers reading this field will misinterpret the value as milliseconds and
/// over-size their internal buffers; the actual decode still works because the Opus
/// decoder is self-describing from the packet TOC byte. Within-major-version (v3.x ↔ v3.x)
/// the field is unambiguous.
/// </summary>
public sealed record AudioFormatInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int Encoding,
    int BlockAlign,
    int AverageBytesPerSecond,
    int Codec = (int)AudioTransportCodec.Pcm,
    int FrameSamplesPerChannel = 480,
    RenderRoute Lane = RenderRoute.Mixed)
{
    /// <summary>Human-friendly frame duration in milliseconds, derived from
    /// <see cref="FrameSamplesPerChannel"/> and <see cref="SampleRate"/>. May be a fraction
    /// (2.5 ms at 48 kHz / 120 samples). For string formatting only — the encoder/decoder
    /// hot path uses <see cref="FrameSamplesPerChannel"/> directly.</summary>
    public double FrameDurationMs => SampleRate > 0 ? FrameSamplesPerChannel * 1000.0 / SampleRate : 0;

    public override string ToString()
    {
        var encodingName = Encoding switch
        {
            1 => "PCM",
            3 => "IEEE float",
            _ => $"encoding {Encoding}"
        };
        var codecName = (AudioTransportCodec)Codec switch
        {
            AudioTransportCodec.Opus => $" over Opus ({FrameDurationMs:0.##} ms)",
            _ => ""
        };
        var laneName = Lane == RenderRoute.Mixed ? "" : $" [{Lane}]";
        return $"{SampleRate} Hz, {Channels} channel(s), {BitsPerSample}-bit {encodingName}{codecName}{laneName}";
    }
}
