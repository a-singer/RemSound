using System.Runtime;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // SustainedLowLatency tells the GC to avoid full (gen 2) collections while audio is streaming.
        // Gen 0/1 collections still happen but are sub-millisecond; the long pauses that were causing
        // the receiver to fall behind in clusters of 4-5 underruns at a time were almost certainly
        // gen 2 sweeps. This trades a bit of memory headroom (the GC will hold on to garbage longer)
        // for dramatically more predictable timing — exactly the trade real-time audio wants.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        ApplicationConfiguration.Initialize();

        // F1 anywhere = open the bundled manual. Installed *before* the first ShowDialog so
        // it works on the profile picker (the very first thing the user sees). The filter
        // is per-thread and modifier-aware: bare F1 only, so Shift/Ctrl/Alt+F1 stay free.
        HelpLauncher.Install();

        // Outer loop: lets ProfileManagementDialog change the profiles folder mid-session.
        // When that happens, MainForm sets ReloadFromScratch=true, we re-read AppConfig, build
        // a fresh ProfileStore, and re-show ProfileSelectionDialog so the user picks a profile
        // (or blank template) from the *new* folder. Inner loop handles the cheaper "switch to
        // a profile in the same folder" case.
        while (true)
        {
            var appConfig = AppConfig.Load();
            var store = appConfig.CreateStore();

            Profile? profile;
            string? title;
            // Auto-load shortcut: if AppConfig.StartWithProfileTitle is set and the named
            // profile actually exists in the current store, skip the picker entirely and
            // load that profile directly. This is what the Startup behaviour dialog's
            // "Start with a specific profile" toggle drives. Combined with the Windows
            // auto-start registry entry and the StartMinimised flag, it lets the user
            // boot a machine and have RemSound up and streaming with no clicks. Falls
            // through to the normal picker if the configured profile no longer exists
            // (deleted since it was selected, or the profiles folder changed) so the user
            // isn't stuck.
            Profile? autoLoaded = null;
            string? autoLoadedTitle = null;
            if (!string.IsNullOrWhiteSpace(appConfig.StartWithProfileTitle))
            {
                try
                {
                    autoLoaded = store.Load(appConfig.StartWithProfileTitle!);
                    if (autoLoaded is not null) autoLoadedTitle = appConfig.StartWithProfileTitle;
                }
                catch { /* fall back to picker */ }
            }

            if (autoLoaded is not null)
            {
                profile = autoLoaded;
                title = autoLoadedTitle;
            }
            else
            {
                using var dialog = new ProfileSelectionDialog(store);
                if (dialog.ShowDialog() != DialogResult.OK) return;
                // ProfileSelectionDialog can have changed the folder via its Browse button;
                // if so, it's already saved AppConfig and rebuilt its internal store. Pick up
                // its post-Browse store reference for the rest of the session.
                store = dialog.Store;
                profile = dialog.SelectedProfile;
                title = dialog.SelectedTitle;
            }

            // Switch-profile loop: when the user clicks "Switch to profile" in the Manage
            // Profiles dialog, the form sets NextProfileTitleToLoad and closes; we re-open
            // MainForm under the newly chosen profile. Null = user closed the form normally
            // → exit. ReloadFromScratch = the user changed the profiles FOLDER mid-session,
            // so we break out of this inner loop and let the outer loop redo the selection
            // dialog under the new folder.
            var reloadFromScratch = false;
            string? nextPath = null;
            while (true)
            {
                using var form = new MainForm(store, profile, title, nextPath);
                Application.Run(form);

                if (form.ReloadFromScratch)
                {
                    reloadFromScratch = true;
                    break;
                }

                // Path-based reload (File → Open profile from a path that may be outside
                // the active store's BaseDirectory) takes precedence — read JSON directly
                // from that path. Falls back to title-based store.Load when no path is set
                // (e.g. legacy switch-by-title flows that pre-date the path tracking).
                nextPath = form.NextProfilePathToLoad;
                var nextTitle = form.NextProfileTitleToLoad;
                if (!string.IsNullOrEmpty(nextPath))
                {
                    try
                    {
                        var json = File.ReadAllText(nextPath);
                        profile = System.Text.Json.JsonSerializer.Deserialize<Profile>(json) ?? Profile.NewBlank();
                        title = !string.IsNullOrEmpty(nextTitle)
                            ? nextTitle
                            : Path.GetFileNameWithoutExtension(nextPath);
                    }
                    catch
                    {
                        // Malformed / unreadable JSON. Fall back to blank template under
                        // whatever title we have, rather than crashing the loop.
                        profile = Profile.NewBlank();
                        title = !string.IsNullOrEmpty(nextTitle)
                            ? nextTitle
                            : Path.GetFileNameWithoutExtension(nextPath);
                        nextPath = null;
                    }
                }
                else if (!string.IsNullOrEmpty(nextTitle))
                {
                    title = nextTitle;
                    profile = store.Load(nextTitle) ?? Profile.NewBlank();
                }
                else
                {
                    return; // form closed normally — exit app
                }
            }

            if (!reloadFromScratch) return;
        }
    }
}
