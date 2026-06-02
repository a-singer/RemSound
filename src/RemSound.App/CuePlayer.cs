using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RemSound.App;

/// <summary>
/// Plays a short cue WAV reliably, regardless of its format. Replaces System.Media.SoundPlayer,
/// which only handles bog-standard 16-bit / 44.1–48 kHz PCM and silently fails (plays nothing,
/// intermittently) on anything else — which is exactly why RemSound's own 96 kHz / 24-bit cue
/// files, and any custom WAV a user browses for, played unpredictably (2026-06-02 investigation
/// of Ed's "didn't hear the disconnect cue" report).
///
/// Each Play() reads the file with NAudio (which copes with 16/24/32-bit and any sample rate),
/// resamples to a universally-safe 48 kHz / 16-bit with a pure-managed resampler (no Media
/// Foundation, so it works on Windows 7 too), and renders it through WaveOut to the default
/// output device. Setup happens on a background thread so a cue never hitches the UI, and the
/// output + reader are disposed when playback finishes. Cues are occasional and short, so a
/// fresh device-open per play is fine and keeps the class stateless and overlap-safe (two cues
/// close together simply mix). 2026-06-02.
/// </summary>
internal sealed class CuePlayer : IDisposable
{
    private readonly string filePath;

    public CuePlayer(string filePath) => this.filePath = filePath;

    public void Play()
    {
        var path = filePath;
        Task.Run(() =>
        {
            AudioFileReader? reader = null;
            WaveOutEvent? output = null;
            try
            {
                reader = new AudioFileReader(path);
                ISampleProvider source = reader;
                if (reader.WaveFormat.SampleRate != 48000)
                {
                    source = new WdlResamplingSampleProvider(source, 48000);
                }
                output = new WaveOutEvent();
                var capturedReader = reader;
                var capturedOutput = output;
                output.PlaybackStopped += (_, _) =>
                {
                    try { capturedOutput.Dispose(); } catch { /* best-effort */ }
                    try { capturedReader.Dispose(); } catch { /* best-effort */ }
                };
                output.Init(new SampleToWaveProvider16(source));
                output.Play();
            }
            catch
            {
                // A cue that won't load or play must never disturb anything. Clean up and move on.
                try { output?.Dispose(); } catch { /* ignore */ }
                try { reader?.Dispose(); } catch { /* ignore */ }
            }
        });
    }

    public void Dispose()
    {
        // Nothing persistent to release — each Play owns and disposes its own reader + output.
    }
}
