namespace RemSound.Core;

/// <summary>
/// Audio format announcement carried in every Format packet. <see cref="Lane"/> was added
/// 2026-05-11 alongside the BothIndependent audio mode — see <see cref="RenderRoute"/> for
/// the semantics. The field is wire-backward-compatible: old receivers parse the first 32
/// bytes of the format payload and ignore the extra; new receivers reading a 32-byte
/// payload from an old sender default Lane to <see cref="RenderRoute.Mixed"/>.
/// </summary>
public sealed record AudioFormatInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int Encoding,
    int BlockAlign,
    int AverageBytesPerSecond,
    int Codec = (int)AudioTransportCodec.Pcm,
    int FrameDurationMilliseconds = 10,
    RenderRoute Lane = RenderRoute.Mixed)
{
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
            AudioTransportCodec.Opus => $" over Opus ({FrameDurationMilliseconds} ms)",
            _ => ""
        };
        var laneName = Lane == RenderRoute.Mixed ? "" : $" [{Lane}]";
        return $"{SampleRate} Hz, {Channels} channel(s), {BitsPerSample}-bit {encodingName}{codecName}{laneName}";
    }
}
