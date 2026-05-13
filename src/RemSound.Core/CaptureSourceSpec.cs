namespace RemSound.Core;

/// <summary>
/// Whether a capture source pulls audio via WASAPI loopback (rendering side of an output device,
/// e.g. system audio / a soundcard's playback) or via direct WASAPI capture (microphones,
/// line-ins, USB capture inputs).
/// </summary>
public enum CaptureKind
{
    Loopback,
    Input,
}

/// <summary>
/// Identifies one source the sender should mix into the outgoing stream. <see cref="Name"/> is
/// purely for diagnostic logging; <see cref="DeviceId"/> is either a WASAPI MMDevice ID or a
/// synthetic ASIO id of the form <c>"asio:&lt;channel-pair-index&gt;"</c>.
/// </summary>
public sealed record CaptureSourceSpec(string DeviceId, CaptureKind Kind, string Name);

/// <summary>
/// Helpers for the synthetic ASIO device-id format used by both sender and receiver backends.
/// Each ASIO channel pair (stereo) is identified by its zero-based pair index — pair 0 is ASIO
/// channels 0+1, pair 1 is 2+3, etc. The driver itself isn't encoded in the id; only one ASIO
/// driver is active per session and it's configured separately.
/// </summary>
public static class AsioDeviceId
{
    public static string Format(int channelPair) => $"asio:{channelPair}";

    public static bool TryParse(string deviceId, out int channelPair)
    {
        channelPair = -1;
        if (string.IsNullOrEmpty(deviceId)) return false;
        if (!deviceId.StartsWith("asio:", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(deviceId.AsSpan("asio:".Length), out channelPair) && channelPair >= 0;
    }
}
