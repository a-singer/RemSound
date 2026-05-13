namespace RemSound.Core;

/// <summary>
/// Float32 ↔ packed signed 24-bit little-endian conversions. The 24-bit format is what we put on the wire
/// (3 bytes per sample, no padding) — same quality as float32 in the audible range, 25% less bandwidth.
/// </summary>
public static class PcmPack
{
    /// <summary>
    /// Pack a span of float samples (range −1..+1) into signed 24-bit little-endian PCM.
    /// Destination must be at least <c>source.Length * 3</c> bytes.
    /// </summary>
    public static void FloatToInt24LE(ReadOnlySpan<float> source, Span<byte> destination)
    {
        if (destination.Length < source.Length * 3)
        {
            throw new ArgumentException("Destination too small", nameof(destination));
        }

        for (int i = 0, j = 0; i < source.Length; i++, j += 3)
        {
            var clamped = Math.Clamp(source[i], -1f, 1f);
            // Signed-symmetric: scale by 2^23 - 1 then truncate. Round-to-nearest avoided here on purpose
            // because the audio path is already band-limited; the extra ULP is inaudible and the cost matters.
            var sample = (int)(clamped * 8388607f);
            destination[j] = (byte)(sample & 0xFF);
            destination[j + 1] = (byte)((sample >> 8) & 0xFF);
            destination[j + 2] = (byte)((sample >> 16) & 0xFF);
        }
    }

    /// <summary>
    /// Unpack signed 24-bit little-endian PCM into floats in [−1, +1].
    /// </summary>
    public static void Int24LEToFloat(ReadOnlySpan<byte> source, Span<float> destination)
    {
        var sampleCount = source.Length / 3;
        if (destination.Length < sampleCount)
        {
            throw new ArgumentException("Destination too small", nameof(destination));
        }

        for (int i = 0, j = 0; i < sampleCount; i++, j += 3)
        {
            // Sign-extend by shifting left to bit 31 then arithmetic right back.
            int packed = (source[j]) | (source[j + 1] << 8) | (source[j + 2] << 16);
            int signed = (packed << 8) >> 8;
            destination[i] = signed / 8388607f;
        }
    }
}
