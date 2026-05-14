using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace RemSound.Core;

/// <summary>
/// A saved snapshot of every user-controllable RemSound setting. Replaces the old
/// machine-wide "settings" file. Profiles live as one JSON file per profile under
/// <c>&lt;exe&gt;\profiles\&lt;machine name&gt;\&lt;title&gt;.json</c> and are portable —
/// copying a profile JSON to another machine's profiles folder makes it appear in that
/// machine's selection list. Device IDs stored in a profile (sound cards, ASIO drivers)
/// that don't exist on the loading machine are silently ignored on apply, so a profile
/// can roam between machines with different hardware without erroring out.
///
/// Design point: profiles capture EVERY UI control state, including device ticks. The
/// previous design rule was to NOT persist device selections (start unticked every
/// session). Profiles deliberately override that — the whole point is one-click
/// restoration. If a user wants the old "start fresh" behaviour, they pick the blank
/// template at startup.
/// </summary>
public sealed class Profile
{
    /// <summary>Display title and filename stem (sanitised). Required.</summary>
    public string Title { get; set; } = "";

    // === Main form: send / receive ===
    public bool ReceiveAudioOn { get; set; }
    public bool SendAudioOn { get; set; }
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }

    // === Audio backend ===
    /// <summary>The ASIO driver this profile uses. <c>null</c> or empty means "no ASIO" —
    /// the form runs in WASAPI-only mode. Any other value selects an ASIO driver and puts
    /// the form into the WASAPI + ASIO independent-lane mode. There is no separate audio-mode
    /// field on the profile any more: the mode is derived from this name alone (2026-05-11
    /// cleanup retired the old AudioMode listbox and its persisted enum). Old profile JSONs
    /// that still contain <c>"AudioModeRaw"</c> or <c>"BothModeWarningSuppressed"</c> simply
    /// have those keys ignored on deserialisation.</summary>
    public string? AsioDriverName { get; set; }

    // === Selected devices (raw device IDs, not display names) ===
    public List<string> SelectedWasapiReceiveOutputs { get; set; } = [];
    public List<string> SelectedAsioReceiveOutputs { get; set; } = [];
    public List<string> SelectedWasapiSendOutputs { get; set; } = [];   // loopback (system audio)
    public List<string> SelectedWasapiSendInputs { get; set; } = [];    // microphones / line-ins
    public List<string> SelectedAsioSendInputs { get; set; } = [];

    // === Connectivity & transport ===
    public int AudioPort { get; set; } = 47830;
    public int CodecRaw { get; set; } = (int)AudioTransportCodec.Pcm;
    public int OpusFrameMilliseconds { get; set; } = 10;
    public int SendRateRaw { get; set; } = (int)SendRate.Standard;
    public bool TightLatencyMode { get; set; }
    /// <summary>True if this profile asks Windows to keep the RemSound process in
    /// high-priority mode while it's running — CPU scheduling, power management, memory
    /// priority, working-set lock, and MMCSS thread priority all elevated. Off by default;
    /// the user opts in per profile when they want a "live session" feel where the
    /// cold-start CPU ramp doesn't audibly hurt latency. On laptops that drains the
    /// battery faster; on desktops it costs a couple of extra watts. See
    /// <c>PerformanceMode</c> in the App project for the full lever list. Saved per
    /// profile (not in AppConfig) because the right answer genuinely differs between
    /// profiles.</summary>
    public bool PriorityMode { get; set; }
    /// <summary>True suppresses the connect/disconnect sound cues. Off by default.</summary>
    public bool MuteConnectionCues { get; set; }
    public int MaxLatencyMs { get; set; } = 80;
    public int Smoothness { get; set; } = 3;
    public bool ContinuousAutoTuneEnabled { get; set; }
    public int ContinuousAutoTuneIntervalSec { get; set; } = 5;
    /// <summary>Per-route latency for the ASIO lane in AudioMode.BothIndependent. Default
    /// 10 ms because BothIndependent's value proposition is letting ASIO run at its native
    /// low latency; users who pick that mode almost always want ASIO closer to 10 than 80.
    /// Ignored in every classic mode.</summary>
    public int MaxLatencyMsAsio { get; set; } = 10;
    /// <summary>Continuous auto-tune toggle for the ASIO lane (BothIndependent only).
    /// Defaults false to match the WASAPI-lane default — symmetric off-by-default avoids
    /// the trap where the ASIO lane auto-inflates its target while WASAPI sits fixed at
    /// its slider, producing higher ASIO latency than WASAPI in the typical session.</summary>
    public bool ContinuousAutoTuneAsioEnabled { get; set; }
    // LoggingEnabled was retired from Profile — logging is a machine-local debug knob
    // (AppConfig.LoggingEnabled), not a per-profile setting. Old profile JSONs that still
    // contain "LoggingEnabled" just have the key ignored on load.
    /// <summary>Receiver-side concealment artifact, stored as raw int for JSON-stability
    /// across enum-reorderings. Defaults to <see cref="ConcealmentArtifact.NoiseBurst"/>
    /// (the cosine-tone variants were removed from the dropdown in Phase 3 cleanup —
    /// 2026-05-06 — but the enum values stay around so old profile JSONs still parse;
    /// the dialog coerces any cosine-tone value to NoiseBurst at load time).</summary>
    public int ConcealmentArtifactRaw { get; set; } = (int)ConcealmentArtifact.NoiseBurst;

    [JsonIgnore]
    public ConcealmentArtifact ConcealmentArtifact
    {
        get => (ConcealmentArtifact)ConcealmentArtifactRaw;
        set => ConcealmentArtifactRaw = (int)value;
    }

    // === Peers ===
    public List<string> RememberedPeers { get; set; } = [];
    /// <summary>Peer addresses (IP or host[:port]) the user had ticked in the connected
    /// list at save time. On load, RemSound auto-connects to any of these that resolve.</summary>
    public List<string> SelectedConnectedPeers { get; set; } = [];

    // === Hotkeys ===
    public HotkeyRecord? ReceiveMuteHotkey { get; set; }
    public HotkeyRecord? SendMuteHotkey { get; set; }
    public HotkeyRecord? TrayHotkey { get; set; }
    public HotkeyRecord? VolumeUpHotkey { get; set; }
    public HotkeyRecord? VolumeDownHotkey { get; set; }
    /// <summary>Hotkey that sends a "raise volume" command to every connected peer that has
    /// "Accept remote volume commands" enabled. The local volume slider on this machine is
    /// NOT touched. Use case: I'm NVDA-Remote'd into another machine and want to nudge the
    /// listening volume on the laptop I'm physically at without breaking out of the session.</summary>
    public HotkeyRecord? RemoteVolumeUpHotkey { get; set; }
    /// <summary>Mirror of RemoteVolumeUpHotkey for "lower volume" commands.</summary>
    public HotkeyRecord? RemoteVolumeDownHotkey { get; set; }
    /// <summary>Hotkey that sends a "toggle receive mute" command to every connected peer.</summary>
    public HotkeyRecord? RemoteMuteToggleHotkey { get; set; }
    /// <summary>Hotkey that sends a "raise Windows default-output-device volume by one step"
    /// command to every connected peer that has Accept remote volume commands enabled. Each
    /// press bumps the receiving peer's Windows master volume by the OS native step (~2%) —
    /// same as pressing the keyboard volume key on the receiver. System-wide on the receiver:
    /// affects every app on that machine including its screen reader.</summary>
    public HotkeyRecord? SystemVolumeUpHotkey { get; set; }
    /// <summary>Mirror of SystemVolumeUpHotkey for the down direction.</summary>
    public HotkeyRecord? SystemVolumeDownHotkey { get; set; }
    /// <summary>Hotkey that sends a "toggle Windows default-output-device mute" command to
    /// every connected peer.</summary>
    public HotkeyRecord? SystemMuteToggleHotkey { get; set; }
    /// <summary>When true, this machine honours incoming Control packets from connected
    /// peers — adjusts the local volume slider or toggles mute. Default false: receiving
    /// remote control is opt-in even though the audio allow-list already gates who's
    /// connected. Lets a user have one profile that's controllable (home setup, single
    /// trusted peer) and another that's not (one-off jam session, public-ish peer).</summary>
    public bool AcceptRemoteVolumeCommands { get; set; }

    // === JSON-friendly accessors (so callers don't deal with the raw int casts) ===
    // AudioMode accessor + AudioModeRaw backing field retired 2026-05-11. The runtime mode is
    // now derived from AsioDriverName; there is no separate persisted enum.
    [JsonIgnore]
    public AudioTransportCodec Codec
    {
        get => (AudioTransportCodec)CodecRaw;
        set => CodecRaw = (int)value;
    }

    [JsonIgnore]
    public SendRate SendRate
    {
        get => (SendRate)SendRateRaw;
        set => SendRateRaw = (int)value;
    }

    /// <summary>Returns a defaults-only profile — same shape as the "blank template"
    /// the user picks at startup. Title is empty (caller assigns when saving).</summary>
    public static Profile NewBlank() => new();
}

/// <summary>JSON-serialisable hotkey representation. Mirrors <see cref="HotkeyInfo"/>
/// but stores Key as a string to keep the JSON robust to enum reorganisations.</summary>
public sealed class HotkeyRecord
{
    public string Key { get; set; } = "M";
    public bool Control { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    public static HotkeyRecord From(HotkeyInfo hotkey) => new()
    {
        Key = hotkey.Key.ToString(),
        Control = hotkey.Control,
        Shift = hotkey.Shift,
        Alt = hotkey.Alt,
    };

    public HotkeyInfo ToHotkeyInfo()
    {
        if (!Enum.TryParse<Keys>(Key, out var parsedKey)) return HotkeyInfo.Default;
        var hotkey = new HotkeyInfo(parsedKey, Control, Shift, Alt);
        if (hotkey.IsUnset) return HotkeyInfo.Unset;
        return hotkey.IsValid ? hotkey : HotkeyInfo.Default;
    }
}
