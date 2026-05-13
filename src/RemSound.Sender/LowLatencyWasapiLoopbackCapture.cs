using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RemSound.Sender;

/// <summary>
/// WasapiLoopbackCapture variant that uses event-sync (interrupt-driven) callbacks with a
/// short audio buffer. NAudio's default <see cref="WasapiLoopbackCapture"/> polls every
/// half-buffer (~50 ms with the default 100 ms buffer), which delivers audio in noticeable
/// bursts and blows up the receiver's playout buffer headroom requirement.
///
/// With event sync + 10 ms buffer, callbacks fire at the device period (~10 ms typical),
/// each carrying ~10 ms of audio. Far smoother. This is what the older RSound build used,
/// minus the layers of subsequent abstraction.
/// </summary>
internal sealed class LowLatencyWasapiLoopbackCapture : WasapiCapture
{
    public LowLatencyWasapiLoopbackCapture(MMDevice device, int audioBufferMilliseconds = 10)
        : base(device, useEventSync: true, audioBufferMillisecondsLength: Math.Clamp(audioBufferMilliseconds, 5, 200))
    {
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}
