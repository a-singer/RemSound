namespace RemSound.Core;

/// <summary>
/// Tag carried in the per-stream <see cref="AudioFormatInfo.Lane"/> wire field, telling the
/// receiver which render backend a particular stream's audio belongs to. In the three classic
/// audio modes (WasapiOnly, AsioOnly, Both) every stream from a sender carries
/// <see cref="Mixed"/> and the receiver's <c>PlayoutEngine</c> mixes them all into one bus
/// that is fanned out to every configured render backend — identical to the pre-2026-05-11
/// behaviour.
///
/// The BothIndependent mode (added 2026-05-11) is the reason this exists: in that mode the
/// sender emits *two* streams in parallel — a WASAPI lane at WASAPI's native latency and an
/// ASIO lane at ASIO's native latency. The sender tags each lane with <see cref="WasapiLane"/>
/// or <see cref="AsioLane"/>; the receiver routes each lane's audio to a separate
/// <c>SessionPlayout</c> group, and each render backend reads only the group it owns. No
/// cross-clock resampler, no tee — each lane stays at its own native latency end-to-end.
///
/// Wire format: stored as a single byte at offset 32 of the format payload. Receivers that
/// don't understand the field (pre-2026-05-11 builds) parse only the first 32 bytes and
/// behave exactly as before — the new field is purely additive. Receivers that do understand
/// it but receive a 32-byte payload (because the sender is old) default to <see cref="Mixed"/>,
/// also matching the pre-2026-05-11 behaviour.
/// </summary>
public enum RenderRoute : byte
{
    /// <summary>Legacy / classic behaviour: stream is mixed with every other stream and sent
    /// to all render backends. Used by every classic-mode sender lane and is the default
    /// when the format-packet Lane field is missing or zero.</summary>
    Mixed = 0,

    /// <summary>Stream belongs to the WASAPI render lane and should only reach WASAPI output
    /// devices, bypassing the cross-backend mix. Only emitted by senders in BothIndependent
    /// mode.</summary>
    WasapiLane = 1,

    /// <summary>Stream belongs to the ASIO render lane and should only reach ASIO outputs,
    /// bypassing the cross-backend mix. Only emitted by senders in BothIndependent mode.</summary>
    AsioLane = 2,
}
