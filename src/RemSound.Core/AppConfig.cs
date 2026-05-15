using System.Text.Json;

namespace RemSound.Core;

/// <summary>How often the self-updater polls GitHub Releases for a newer build. Values are
/// stable: don't reorder; deserialisation reads the underlying int from <c>remsound.config.json</c>.</summary>
public enum UpdateCheckFrequency
{
    Never = 0,
    EveryHour = 1,
    Every6Hours = 2,
    Every24Hours = 3,
}

/// <summary>
/// App-level configuration that lives next to the exe as <c>remsound.config.json</c>.
/// Distinct from <see cref="Profile"/>: profiles are user-chosen sets of audio /
/// connectivity / device settings; the app config is the *meta* layer that holds
/// preferences that should be sticky regardless of which profile is loaded. Profiles are
/// per-setup; this file is per-installation.
///
/// What lives here:
///   * <see cref="ProfilesDirectory"/> — where the profile JSONs are read from.
///
/// (Pre-2026-05-11 also held <c>BothModeWarningSuppressed</c> — the "do not show me again"
/// tick on the WASAPI+ASIO latency popup. The popup was retired along with the audio-mode
/// listbox; old config JSONs that still contain the key just have it ignored.)
///
/// Persisted location: <c>&lt;exe&gt;\remsound.config.json</c>. If the file is missing or
/// malformed, defaults are used and the app behaves exactly as it did pre-2026-05-05
/// (per-machine subfolder under the exe). The file is only written when the user
/// explicitly changes a setting.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Filesystem path to the directory the app should read profiles from. When
    /// null, RemSound uses the legacy default: <c>&lt;exe&gt;\profiles\&lt;machine&gt;\</c>.
    /// When set to an explicit folder, that folder IS the profiles folder — no per-machine
    /// subfolder is appended (the user picked it, they meant it; that also lets a user point
    /// at a Dropbox folder shared between machines).</summary>
    public string? ProfilesDirectory { get; set; }

    /// <summary>True if the user has ticked "do not show me this message again" on the
    /// confirmation popup that fires when Save (Ctrl+S / File → Save) successfully
    /// overwrites the currently-loaded profile. Lives here (not in Profile) so the
    /// preference sticks across profile switches — once you've decided you don't need
    /// the "Profile saved" nag, you don't expect it to come back when you load a
    /// different profile. The Save-As path doesn't use this flag: the Save-As dialog
    /// itself is the user-visible confirmation, so a follow-up popup is redundant.</summary>
    public bool SaveProfileConfirmationSuppressed { get; set; }

    /// <summary>If true, RemSound minimises to the system tray immediately after the main
    /// window finishes loading. Lets the user "boot up the machine and have RemSound
    /// already running quietly". Default false.</summary>
    public bool StartMinimised { get; set; }

    /// <summary>If true, RemSound writes a tab-separated diagnostic log to
    /// <c>&lt;exe&gt;\logs\</c>. Lives here (not in <see cref="Profile"/>) because logging
    /// is a debugging affordance for the installation, not a user-facing audio preference —
    /// switching profiles shouldn't accidentally re-enable a flood of writes the user had
    /// turned off, and a one-machine "yes log everything" decision shouldn't have to ride
    /// along on every saved profile. Default false: no log file is created until the user
    /// ticks <em>Enable logs</em> in the Preferences dialog.</summary>
    public bool LoggingEnabled { get; set; }

    /// <summary>If non-null and a profile with this title exists, RemSound skips the
    /// startup profile picker and loads this profile directly. Combine with
    /// <see cref="StartMinimised"/> + the Windows auto-start registry entry
    /// (see <c>StartupAutoStart</c>) to get a fully unattended boot-into-streaming flow.
    /// To re-show the picker temporarily, untick "Start with a specific profile" in the
    /// Startup behaviour dialog. Null = always show the picker (legacy behaviour).</summary>
    public string? StartWithProfileTitle { get; set; }

    /// <summary>How often RemSound polls the GitHub Releases API for a newer build. Default
    /// <see cref="UpdateCheckFrequency.Every24Hours"/>. Set to <see cref="UpdateCheckFrequency.Never"/>
    /// to disable background checks entirely (the user can still trigger a manual check via
    /// the Preferences button or the Help menu).</summary>
    public UpdateCheckFrequency UpdateCheckFrequency { get; set; } = UpdateCheckFrequency.Every24Hours;

    /// <summary>If true, RemSound downloads and applies a new release without prompting:
    /// the running instance writes the new files to a staging folder, spawns a small
    /// detached helper that waits for the exe to exit, swaps in the new files, and restarts
    /// RemSound. Default false — the user gets a confirmation dialog before each install.</summary>
    public bool SilentlyInstallUpdates { get; set; }

    /// <summary>UTC timestamp of the last successful update check. Used by the background
    /// update timer to space out polls across launches — if you set the frequency to
    /// "every 24 hours" and re-launch the app three times that day, it still hits the API
    /// only once. Null on a fresh install.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Most-recently-opened profile paths, newest first, capped at
    /// <see cref="MaxRecentProfiles"/>. Populated by <see cref="NoteRecentProfile"/> every
    /// time a profile is loaded, surfaced in the File → Recent profiles submenu. Stored as
    /// full paths so profiles saved outside the canonical profiles folder are also
    /// reachable (Save-As to an arbitrary path stays in the recents list).</summary>
    public List<string> RecentProfiles { get; set; } = new();

    /// <summary>Cap on how many entries we keep in <see cref="RecentProfiles"/>. Five is the
    /// most that fits comfortably as 1–5 single-digit mnemonics inside a submenu without
    /// the user needing to read the names to remember which row they want.</summary>
    public const int MaxRecentProfiles = 5;

    /// <summary>Push a profile path to the front of the recents list. Removes any existing
    /// entry that matches (case-insensitive) so a recently re-opened profile rises to the
    /// top instead of being duplicated. Caps the list at <see cref="MaxRecentProfiles"/>.
    /// Caller must <see cref="Save"/> after mutating.</summary>
    public void NoteRecentProfile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RecentProfiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProfiles.Insert(0, path);
        while (RecentProfiles.Count > MaxRecentProfiles)
        {
            RecentProfiles.RemoveAt(RecentProfiles.Count - 1);
        }
    }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "remsound.config.json");

    /// <summary>Reads the app config from disk. Always returns a non-null instance — a missing
    /// or malformed file becomes a defaults-only AppConfig rather than throwing.</summary>
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            // Corrupt config file shouldn't keep RemSound from launching. Fall back to
            // defaults; the user can re-pick a folder via the dialog and we'll overwrite
            // the bad file on the next save.
            return new AppConfig();
        }
    }

    /// <summary>Writes this config to disk. Throws on filesystem failures (caller should
    /// surface a MessageBox — failure to persist a directory choice is user-visible).</summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>Convenience: build the appropriate <see cref="ProfileStore"/> for the
    /// current config. Falls back to the default store (per-machine subfolder) if the
    /// configured folder is missing, blank, or doesn't exist on disk.</summary>
    public ProfileStore CreateStore()
    {
        if (!string.IsNullOrWhiteSpace(ProfilesDirectory) && Directory.Exists(ProfilesDirectory))
        {
            return new ProfileStore(ProfilesDirectory);
        }
        return new ProfileStore();
    }
}
