using System.Buffers.Binary;

namespace RemSound.Core;

public enum RemPacketType : byte
{
    Format = 1,
    Audio = 2,
    KeepAlive = 3,
    Heartbeat = 4,
    /// <summary>
    /// Remote-control message from one connected peer to another. Currently used to let a
    /// peer adjust the receiver-side volume slider on a peer it's connected to (so a user
    /// who's NVDA-Remote'd into another machine can still nudge the listening volume on
    /// the machine they're physically at). Wire format: 1 byte <see cref="RemoteControlKind"/>
    /// + 1 byte signed delta (interpreted as signed sbyte; range -128..127, percent points).
    /// Old peers see "unknown packet type" and silently drop, so adding this is wire-safe.
    /// </summary>
    Control = 5,
}

public enum HeartbeatKind : byte
{
    Ping = 0,
    Pong = 1,
}

/// <summary>
/// What a Control packet is asking the receiver to do.
///
/// Two families of commands:
///   * <see cref="VolumeUp"/> / <see cref="VolumeDown"/> / <see cref="MuteToggle"/> — adjust
///     the receiver's RemSound app volume slider (in-app, only affects RemSound's own audio).
///     Delta byte carries a percent-point step (typically ±5).
///   * <see cref="SystemVolumeUp"/> / <see cref="SystemVolumeDown"/> / <see cref="SystemMuteToggle"/>
///     — adjust the receiver's Windows default-output-device master volume (system-wide,
///     affects every app on the receiving machine, including its screen reader). Each call
///     issues exactly one Windows native volume-step (typically ~2%), matching what the
///     keyboard volume keys do. Delta byte ignored.
///
/// Kept narrow on purpose: remote control is a small audio convenience, not a generic
/// "do anything to that peer" channel. Adding new commands later means adding enum values;
/// old receivers see them as invalid and ignore the packet.
/// </summary>
public enum RemoteControlKind : byte
{
    VolumeUp = 0,
    VolumeDown = 1,
    MuteToggle = 2,
    SystemVolumeUp = 3,
    SystemVolumeDown = 4,
    SystemMuteToggle = 5,
}

[Flags]
public enum KeepAliveCapabilities : byte
{
    None = 0,
    CanSend = 1,
    CanReceive = 2,
}

public enum KeepAliveKind : byte
{
    Heartbeat = 1,
    Ack = 2,
}

public readonly record struct KeepAliveInfo(
    Guid SessionId,
    KeepAliveKind Kind,
    KeepAliveCapabilities Capabilities,
    AudioTransportCodec Codec,
    long UnixTimeMilliseconds);

/// <summary>
/// Wire format for RemSound packets. Header is 12 bytes; body length is implied by the UDP datagram.
/// Header layout (little-endian):
///   uint32 magic    'RMND'
///   uint8  version  1
///   uint8  type     RemPacketType
///   uint16 streamId
///   uint32 sequence
/// </summary>
public static class RemPacket
{
    public const int HeaderSize = 12;
    /// <summary>Minimum format payload size. Builds older than 2026-05-11 only emit this
    /// many bytes; readers must accept this length as a valid (but unextended) format
    /// packet and default any post-32-byte fields. See <see cref="FormatPayloadExtendedSize"/>.</summary>
    public const int FormatPayloadSize = 32;
    /// <summary>Extended format payload size (32 base + 4 extension). The extension carries
    /// the <see cref="AudioFormatInfo.Lane"/> byte at offset 32 plus 3 reserved-zero bytes for
    /// future growth. Receivers must check <c>payload.Length &gt;= FormatPayloadExtendedSize</c>
    /// before reading the Lane field; payloads shorter than that default Lane to
    /// <see cref="RenderRoute.Mixed"/>. Senders newer than 2026-05-11 always write this size.</summary>
    public const int FormatPayloadExtendedSize = 36;
    public const int KeepAlivePayloadSize = 28;
    /// <summary>
    /// Heartbeat payload: 1 byte <see cref="HeartbeatKind"/> + 8 bytes originator-monotonic
    /// timestamp (Stopwatch.ElapsedMilliseconds at the time the originating Ping was sent).
    /// Pongs copy the originator's timestamp verbatim — sender computes RTT against its own
    /// clock, so no clock sync is needed between peers (RFC 3550 RTCP DLSR pattern, simplified).
    /// </summary>
    public const int HeartbeatPayloadSize = 9;
    /// <summary>
    /// Control payload: 1 byte <see cref="RemoteControlKind"/> + 1 signed byte delta. Total
    /// 2 bytes, plus the 12-byte header = 14 bytes on the wire. See <see cref="RemPacketType.Control"/>
    /// for the rationale.
    /// </summary>
    public const int ControlPayloadSize = 2;
    /// <summary>
    /// Single canonical port for everything: receiver bind, LAN peer-to-peer dials, and the
    /// public RemSound relay. Was 47820 (audio receiver) + 47830 (relay) in the old design;
    /// unified to 47830 on 2026-05-05 so users never have to type `:port` after a hostname or
    /// IP. Any peer the user adds — Tailscale IP, LAN IP, or relay hostname — defaults to
    /// this port. The +1 (discovery) and +2 (heartbeat) derived ports follow accordingly.
    /// </summary>
    public const int DefaultPort = 47830;
    /// <summary>
    /// Kept as an alias for the single canonical port so existing call sites that distinguish
    /// "the local bind" from "the dial default" still compile. They point at the same value
    /// now — there is no longer a separate dial port.
    /// </summary>
    public const int DefaultPeerDialPort = DefaultPort;
    public const int Magic = 0x444E4D52; // 'RMND' little-endian
    public const byte Version = 1;

    /// <summary>
    /// Maximum payload bytes guaranteed to fit a typical Ethernet path without IP fragmentation
    /// (1500 - 20 IP - 8 UDP - 12 RemPacket header - 6 PCM-multipart sub-header).
    /// </summary>
    public const int MaxAudioPayloadBytes = 1454;

    public static int WriteHeader(Span<byte> destination, RemPacketType type, ushort streamId, uint sequence)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException("Header destination too small", nameof(destination));
        }

        BinaryPrimitives.WriteInt32LittleEndian(destination, Magic);
        destination[4] = Version;
        destination[5] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], streamId == 0 ? (ushort)1 : streamId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], sequence);
        return HeaderSize;
    }

    /// <summary>
    /// Writes the format payload. Always emits <see cref="FormatPayloadExtendedSize"/> bytes
    /// (36): the 32 legacy fields followed by a Lane byte and 3 reserved-zero bytes. Old
    /// receivers that only read 32 bytes will still parse the legacy block correctly and
    /// ignore the trailing 4 — see the <see cref="FormatPayloadSize"/> doc comment for the
    /// compatibility contract.
    /// </summary>
    public static int WriteFormatPayload(Span<byte> destination, AudioFormatInfo format)
    {
        if (destination.Length < FormatPayloadExtendedSize)
        {
            throw new ArgumentException("Format payload destination too small", nameof(destination));
        }

        BinaryPrimitives.WriteInt32LittleEndian(destination, format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], format.BitsPerSample);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], format.Encoding);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], format.BlockAlign);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..], format.AverageBytesPerSecond);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], format.Codec);
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..], format.FrameDurationMilliseconds);
        // Extension: 1 byte Lane + 3 reserved-zero bytes. Zero-fill the reserved slot so a
        // future receiver doesn't accidentally read stale stack data if WriteFormatPayload
        // is called on an uninitialised buffer.
        destination[32] = (byte)format.Lane;
        destination[33] = 0;
        destination[34] = 0;
        destination[35] = 0;
        return FormatPayloadExtendedSize;
    }

    public static int WriteKeepAlivePayload(Span<byte> destination, KeepAliveInfo info)
    {
        if (destination.Length < KeepAlivePayloadSize)
        {
            throw new ArgumentException("KeepAlive payload destination too small", nameof(destination));
        }

        destination[0] = (byte)info.Kind;
        destination[1] = (byte)info.Codec;
        destination[2] = (byte)info.Capabilities;
        destination[3] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..], info.UnixTimeMilliseconds);
        if (!info.SessionId.TryWriteBytes(destination.Slice(12, 16)))
        {
            return 0;
        }
        return KeepAlivePayloadSize;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out RemPacketType type, out ushort streamId, out uint sequence)
    {
        type = default;
        streamId = 0;
        sequence = 0;
        if (packet.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(packet) != Magic) return false;
        if (packet[4] != Version) return false;
        type = (RemPacketType)packet[5];
        streamId = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        if (streamId == 0) streamId = 1;
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        return true;
    }

    /// <summary>
    /// Reads the format payload. Accepts both the legacy 32-byte and the extended 36-byte
    /// layouts: the legacy layout defaults <see cref="AudioFormatInfo.Lane"/> to
    /// <see cref="RenderRoute.Mixed"/>, which is exactly what an old sender (pre-2026-05-11)
    /// would have meant. Lane values outside the defined enum range are clamped to Mixed
    /// rather than rejected — better to play the audio in the default route than drop a
    /// stream because a future sender sent an unknown value.
    /// </summary>
    public static bool TryReadFormat(ReadOnlySpan<byte> payload, out AudioFormatInfo format)
    {
        format = new AudioFormatInfo(48000, 2, 32, 3, 8, 384000);
        if (payload.Length < FormatPayloadSize) return false;

        var lane = RenderRoute.Mixed;
        if (payload.Length >= FormatPayloadExtendedSize)
        {
            var laneRaw = payload[32];
            lane = laneRaw switch
            {
                (byte)RenderRoute.Mixed => RenderRoute.Mixed,
                (byte)RenderRoute.WasapiLane => RenderRoute.WasapiLane,
                (byte)RenderRoute.AsioLane => RenderRoute.AsioLane,
                _ => RenderRoute.Mixed, // forward-compat: unknown lane → safe default
            };
        }

        format = new AudioFormatInfo(
            BinaryPrimitives.ReadInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[28..]),
            lane);
        return true;
    }

    public static int WriteHeartbeatPayload(Span<byte> destination, HeartbeatKind kind, long originatorTickMs)
    {
        if (destination.Length < HeartbeatPayloadSize)
        {
            throw new ArgumentException("Heartbeat payload destination too small", nameof(destination));
        }
        destination[0] = (byte)kind;
        BinaryPrimitives.WriteInt64LittleEndian(destination[1..], originatorTickMs);
        return HeartbeatPayloadSize;
    }

    public static bool TryReadHeartbeat(ReadOnlySpan<byte> payload, out HeartbeatKind kind, out long originatorTickMs)
    {
        kind = HeartbeatKind.Ping;
        originatorTickMs = 0;
        if (payload.Length < HeartbeatPayloadSize) return false;
        var raw = payload[0];
        if (raw != (byte)HeartbeatKind.Ping && raw != (byte)HeartbeatKind.Pong) return false;
        kind = (HeartbeatKind)raw;
        originatorTickMs = BinaryPrimitives.ReadInt64LittleEndian(payload[1..]);
        return true;
    }

    public static int WriteControlPayload(Span<byte> destination, RemoteControlKind kind, sbyte delta)
    {
        if (destination.Length < ControlPayloadSize)
        {
            throw new ArgumentException("Control payload destination too small", nameof(destination));
        }
        destination[0] = (byte)kind;
        destination[1] = (byte)delta;
        return ControlPayloadSize;
    }

    public static bool TryReadControl(ReadOnlySpan<byte> payload, out RemoteControlKind kind, out sbyte delta)
    {
        kind = RemoteControlKind.VolumeUp;
        delta = 0;
        if (payload.Length < ControlPayloadSize) return false;
        var raw = payload[0];
        // Reject unknown kinds rather than coercing — keeps the door open to future kinds
        // without an old receiver guessing wrong on an unfamiliar value.
        if (raw != (byte)RemoteControlKind.VolumeUp
            && raw != (byte)RemoteControlKind.VolumeDown
            && raw != (byte)RemoteControlKind.MuteToggle
            && raw != (byte)RemoteControlKind.SystemVolumeUp
            && raw != (byte)RemoteControlKind.SystemVolumeDown
            && raw != (byte)RemoteControlKind.SystemMuteToggle) return false;
        kind = (RemoteControlKind)raw;
        delta = (sbyte)payload[1];
        return true;
    }

    public static bool TryReadKeepAlive(ReadOnlySpan<byte> payload, out KeepAliveInfo info)
    {
        info = default;
        if (payload.Length < KeepAlivePayloadSize) return false;
        if (!Enum.IsDefined((KeepAliveKind)payload[0])) return false;
        info = new KeepAliveInfo(
            new Guid(payload.Slice(12, 16)),
            (KeepAliveKind)payload[0],
            (KeepAliveCapabilities)payload[2],
            Enum.IsDefined((AudioTransportCodec)payload[1]) ? (AudioTransportCodec)payload[1] : AudioTransportCodec.Pcm,
            BinaryPrimitives.ReadInt64LittleEndian(payload[4..]));
        return true;
    }
}

/// <summary>
/// PCM transport sub-header. PCM frames are larger than a UDP datagram (10 ms × 48 kHz × 2 ch × 3 byte = 2880 B)
/// so they're split into multi-part chunks. The receiver assembles parts back into a complete frame
/// before queueing for playout. Sub-header (6 bytes) is prepended to the audio bytes:
///   uint32 frameId
///   uint8  partIndex
///   uint8  totalParts
/// </summary>
public static class RemPcmFrame
{
    public const int SubHeaderSize = 6;

    public static int WriteSubHeader(Span<byte> destination, uint frameId, byte partIndex, byte totalParts)
    {
        if (destination.Length < SubHeaderSize)
        {
            throw new ArgumentException("PCM sub-header destination too small", nameof(destination));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(destination, frameId);
        destination[4] = partIndex;
        destination[5] = totalParts;
        return SubHeaderSize;
    }

    public static bool TryReadSubHeader(ReadOnlySpan<byte> source, out uint frameId, out byte partIndex, out byte totalParts)
    {
        frameId = 0;
        partIndex = 0;
        totalParts = 0;
        if (source.Length < SubHeaderSize) return false;
        frameId = BinaryPrimitives.ReadUInt32LittleEndian(source);
        partIndex = source[4];
        totalParts = source[5];
        return totalParts > 0 && partIndex < totalParts;
    }
}
