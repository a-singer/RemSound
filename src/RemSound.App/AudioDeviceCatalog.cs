using NAudio.CoreAudioApi;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Enumerates currently active Windows audio endpoints, separately for output (render — used
/// for loopback capture) and input (capture — mics, line-ins) devices. Used by the App to
/// populate the two send-device check-lists.
///
/// Selection state is intentionally NOT persisted: every session starts with all checkboxes
/// unticked and nothing being sent. The user re-ticks once per session. Stops the
/// "wrong-device-still-checked" surprise after a card unplug, ID change, etc.
/// </summary>
internal static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioDeviceChoice> LoadOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var choices = devices
            .Select(d => new AudioDeviceChoice(d.FriendlyName, d.ID, CaptureKind.Loopback))
            .ToList();
        foreach (var device in devices) device.Dispose();
        return choices;
    }

    public static IReadOnlyList<AudioDeviceChoice> LoadInputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var choices = devices
            .Select(d => new AudioDeviceChoice(d.FriendlyName, d.ID, CaptureKind.Input))
            .ToList();
        foreach (var device in devices) device.Dispose();
        return choices;
    }
}
