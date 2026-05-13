namespace RemSound.Core;

/// <summary>
/// How often the sender cuts the audio stream into a packet for transmission. Smaller frames
/// = more packets per second = lower send-side latency, but more network/CPU overhead per
/// second.
///
/// Mapping per codec:
///   * PCM:        Standard = 5 ms (240 samples), Tight = 2.5 ms (120 samples).
///   * Opus 20 ms: Standard = 20 ms, Tight = 10 ms.
///   * Opus 10 ms: Standard = 10 ms, Tight = 5 ms.
///
/// "Tight" is documented as LAN-only because the smaller frame size means less time for the
/// network to absorb jitter before the next packet arrives. On a stable LAN it cuts ~2.5 ms
/// off the send-side accumulator latency without audible cost; over WAN with typical jitter
/// it'll glitch.
/// </summary>
public enum SendRate
{
    Standard = 0,
    Tight = 1,
}
