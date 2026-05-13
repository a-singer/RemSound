namespace RemSound.Receiver;

/// <summary>
/// Abstraction over the render-side audio backend so <see cref="AudioReceiver"/> can be wired
/// to either a WASAPI implementation (today's <see cref="MultiOutputPlayout"/>) or an ASIO
/// implementation (<see cref="AsioRenderBackend"/>) without caring which is in use.
///
/// Both backends pull mixed audio from <see cref="PlayoutEngine"/>'s <see cref="IWaveProvider"/>
/// surface and route it to one or more output destinations. WASAPI destinations are MMDevice
/// IDs; ASIO destinations are synthetic IDs of the form
/// <c>"asio:&lt;driver-name&gt;|&lt;channel-pair-index&gt;"</c>.
/// </summary>
internal interface IRenderBackend : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Friendly summary for the snapshot log column. "(none)" when nothing is
    /// configured, comma-joined names for ≤3 outputs, "(N outputs)" otherwise.</summary>
    string ActiveDeviceSummary { get; }

    IReadOnlyList<string> ActiveDeviceIds { get; }

    void Start();

    void Stop();

    /// <summary>Live-update of the output set. Devices already present stay live; removed ones
    /// are torn down; new ones are opened. Empty list = render to nothing without stopping the
    /// mixer (so receive-side state stays alive).</summary>
    void SetOutputDevices(IReadOnlyList<string> deviceIds);
}
