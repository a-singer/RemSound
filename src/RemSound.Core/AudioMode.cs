namespace RemSound.Core;

/// <summary>
/// Selects which audio backends RemSound runs. Two values are produced by the UI today:
/// <list type="bullet">
///   <item><b>WasapiOnly</b> — MixingEngine ⇄ AudioSender direct, MultiOutputPlayout reads
///         PlayoutEngine direct. No ASIO code path runs at all. Used when the user has the
///         ASIO driver picker set to "(none)" or no ASIO drivers are installed.</item>
///   <item><b>BothIndependent</b> — WASAPI and ASIO both active, but each runs in its own
///         end-to-end pipeline at its own native latency. The sender emits two UDP streams
///         in parallel: a WASAPI lane carrying WASAPI-captured audio (tagged
///         <see cref="RenderRoute.WasapiLane"/>) and an ASIO lane carrying ASIO audio
///         (<see cref="RenderRoute.AsioLane"/>). The receiver routes each lane to the
///         matching render backend with no cross-backend mix. Used when the user has picked
///         a real ASIO driver in the picker.</item>
/// </list>
/// <b>AsioOnly</b> and <b>Both</b> are legacy values kept for back-compat with code paths
/// that take an <see cref="AudioMode"/> as input. No UI path produces them any more, and the
/// composite backends coerce them into <b>WasapiOnly</b> or <b>BothIndependent</b> on receipt.
/// Old profile JSONs that still contain <c>"AudioModeRaw"</c> simply have the key ignored
/// (the field was removed from <see cref="Profile"/> in the 2026-05-11 cleanup).
/// </summary>
public enum AudioMode
{
    WasapiOnly = 0,
    AsioOnly = 1,       // Legacy, no UI path produces this any more.
    Both = 2,           // Legacy classic-Both (tee). No UI path produces this any more.
    BothIndependent = 3,
}
