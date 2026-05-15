using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// Glue between MainForm's Record menu and the actual recording pipeline. Owns the
/// lifecycle of the currently-running <see cref="AudioRecorder"/> (if any) and wires
/// the sender / receiver taps to it. Reading the user's saved settings, persisting
/// changes after the settings dialog, opening / changing the recordings folder — all
/// flow through here so MainForm stays focused on UI wiring.
///
/// Threading: the public methods are called from the UI thread only. The recorder
/// itself runs on its own background thread (it owns a queue + writer); the controller
/// just constructs and disposes it.
/// </summary>
internal sealed class RecordingController
{
    private readonly AudioSender sender;
    private readonly AudioReceiver receiver;
    private readonly RemSoundSettingsStore settings;
    private readonly Action<string> diagnostic;
    private AudioRecorder? active;

    public RecordingController(AudioSender sender, AudioReceiver receiver, RemSoundSettingsStore settings, Action<string> diagnostic)
    {
        this.sender = sender;
        this.receiver = receiver;
        this.settings = settings;
        this.diagnostic = diagnostic;
    }

    public bool IsRecording => active is not null;

    /// <summary>Optional callback fired when the user starts or stops a recording. The
    /// MainForm hooks this to flip the menu item text "Start recording" ↔ "Stop recording"
    /// and announce the change to NVDA.</summary>
    public event Action<bool>? RecordingStateChanged;

    /// <summary>Start a new recording using the currently-saved profile settings. If a
    /// recording is already running this is a no-op (the menu shouldn't ever offer Start
    /// while recording, but the guard is here for safety).</summary>
    public void Start()
    {
        if (active is not null) return;
        var s = settings.LoadRecordingSettings();
        try
        {
            active = new AudioRecorder(s, diagnostic, OnRecorderFinished);
        }
        catch (Exception ex)
        {
            diagnostic($"recording: failed to start: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(
                $"Could not start recording:\n\n{ex.Message}",
                "RemSound — recording",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Wire taps. Each tap is independent — the recorder's source-mode filter decides
        // whether to actually write the samples.
        sender.OnSentSamples = active.WriteSent;
        receiver.OnReceivedSamples = active.WriteReceived;
        diagnostic($"recording: started → {active.FilePath} (source={s.Source}, format={s.FileFormat}, channels={s.ChannelMode})");
        RecordingStateChanged?.Invoke(true);
    }

    /// <summary>Stop the currently-running recording. Unhooks taps, flushes the writer
    /// queue, closes the file, and surfaces the resulting path in a brief MessageBox
    /// so the user knows where the file landed.</summary>
    public void Stop()
    {
        var recorder = active;
        if (recorder is null) return;

        // Unhook taps FIRST so no more audio gets queued during the drain.
        sender.OnSentSamples = null;
        receiver.OnReceivedSamples = null;

        active = null;
        try
        {
            recorder.Stop();
            recorder.Dispose();
        }
        catch (Exception ex)
        {
            diagnostic($"recording: stop threw {ex.GetType().Name}: {ex.Message}");
        }
        RecordingStateChanged?.Invoke(false);
    }

    private void OnRecorderFinished(string path, long bytes)
    {
        diagnostic($"recording: finished → {path} ({bytes:N0} bytes)");
    }

    /// <summary>Open the currently-configured recordings folder in Windows Explorer.
    /// Creates the folder if it doesn't yet exist (a fresh install hasn't recorded
    /// anything, so the folder won't be there). Surfaces filesystem errors to the user
    /// rather than swallowing them silently.</summary>
    public void OpenCurrentFolder(IWin32Window? owner)
    {
        var s = settings.LoadRecordingSettings();
        var folder = s.ResolvedFolder();
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            diagnostic($"recording: open folder failed: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(owner,
                $"Could not open recordings folder:\n\n{ex.Message}",
                "RemSound — recordings folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>Show a folder-picker rooted at the current recordings folder. If the
    /// user picks a different folder, save it on the profile and return true so the
    /// caller can flag the profile dirty.</summary>
    public bool ChangeFolder(IWin32Window? owner)
    {
        var s = settings.LoadRecordingSettings();
        var startFolder = s.ResolvedFolder();
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose a folder for RemSound recordings",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(startFolder) ? startFolder : RecordingSettings.DefaultFolder(),
            ShowNewFolderButton = true,
        };
        if (picker.ShowDialog(owner) != DialogResult.OK) return false;
        if (string.IsNullOrWhiteSpace(picker.SelectedPath)) return false;
        if (string.Equals(picker.SelectedPath, startFolder, StringComparison.OrdinalIgnoreCase)) return false;

        s.Folder = picker.SelectedPath;
        settings.SaveRecordingSettings(s);
        diagnostic($"recording: folder changed → {picker.SelectedPath}");
        return true;
    }
}
