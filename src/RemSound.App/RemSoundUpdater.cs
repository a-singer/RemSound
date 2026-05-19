using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Self-updater. Polls the GitHub Releases API for the latest published version, compares it
/// to the running assembly's version, and (optionally) downloads and installs the new build.
///
/// Update install flow (Windows-only): RemSound.exe can't overwrite itself while it's running,
/// so a successful install does the swap via a detached <c>cmd.exe</c> helper:
/// <list type="number">
///   <item>Download the release ZIP to <c>%TEMP%\RemSound-update-&lt;tag&gt;.zip</c>.</item>
///   <item>Extract to <c>&lt;exe&gt;\_update\</c>.</item>
///   <item>Write a one-shot batch file at <c>&lt;exe&gt;\_apply-update.cmd</c> that waits for
///         RemSound.exe to exit, robocopies the staged folder over the publish folder, deletes
///         the staging area, restarts RemSound.exe, and removes itself.</item>
///   <item>Start the batch with <c>CreateNoWindow</c> + detached, then call
///         <see cref="Application.Exit"/>.</item>
/// </list>
/// The batch survives RemSound's exit because <c>cmd.exe</c> is its own process. Robocopy's
/// retry/wait flags handle the brief moment between RemSound exit and the file unlock.
///
/// The GitHub repo to poll is hard-coded — the App was designed to be redistributed from a
/// single canonical release stream, not to be re-pointed at a fork. If you need to publish
/// from a different repo, change <see cref="RepoOwner"/> / <see cref="RepoName"/>.
/// </summary>
internal sealed class RemSoundUpdater : IDisposable
{
    public const string RepoOwner = "Ednunp";
    public const string RepoName = "RemSound";

    /// <summary>Asset name on the GitHub release that the updater downloads. The release
    /// publisher's <c>gh release create</c> command must attach exactly this filename for
    /// the auto-install path to work; other assets in the release are ignored. The literal
    /// "{tag}" placeholder is replaced with the release's <c>tag_name</c> at runtime.</summary>
    public const string AssetNameTemplate = "RemSound-{tag}.zip";

    private static readonly HttpClient http = CreateClient();

    /// <summary>Sink for diagnostic lines — the App wires this to <c>logFile.Event</c> so an
    /// admin can see what the updater did (which version it saw, whether it downloaded, why
    /// an install attempt failed). Updater output never goes to a popup unless the user
    /// triggered a manual check.</summary>
    public Action<string>? Log { get; set; }

    public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public void Dispose()
    {
        // HttpClient is static and shared across the process; nothing to dispose here.
    }

    /// <summary>Hit the GitHub Releases API, parse the latest release, return a struct
    /// describing what was found. Returns null if the request fails (network down, rate
    /// limited, repo not found) or if the latest version is not newer than the running
    /// assembly. Caller decides whether to surface "you're up to date" vs silently doing
    /// nothing — both paths get null back.</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken token = default)
    {
        try
        {
            // List releases — NOT /releases/latest. The repo also hosts the relay server's
            // own "server-vX.Y" releases, and /releases/latest is repo-wide: it hands back
            // whichever release is newest by date, server or client. A server release would
            // then be fed to ParseTag ("server-v2.3" -> a bogus 0.0.3) and the updater would
            // wrongly conclude "up to date". We pull the list and consider ONLY releases
            // whose tag is a RemSound client tag (see IsClientReleaseTag). 2026-05-18.
            // per_page=100 (vs the API default of 30): the repo holds both client (vX.Y) and
            // relay-server (server-vX.Y) releases, so a burst of server releases could push the
            // newest client release off a 30-item first page. 100 keeps it comfortably in view.
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100";
            Log?.Invoke($"updater: GET {url}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await http.SendAsync(req, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log?.Invoke($"updater: HTTP {(int)resp.StatusCode} from GitHub");
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOpts, token).ConfigureAwait(false);
            if (releases is null || releases.Count == 0)
            {
                Log?.Invoke("updater: releases list was empty");
                return null;
            }

            // Highest-versioned RemSound client release. Skip drafts, prereleases, and any
            // tag that isn't a client tag (notably the server-vX.Y relay releases).
            GitHubRelease? release = null;
            var latest = new Version(0, 0, 0);
            foreach (var r in releases)
            {
                if (r.TagName is null || r.Draft || r.Prerelease) continue;
                if (!IsClientReleaseTag(r.TagName)) continue;
                var v = ParseTag(r.TagName);
                if (v > latest) { latest = v; release = r; }
            }
            if (release?.TagName is null)
            {
                Log?.Invoke("updater: no RemSound client release found in the releases list");
                return null;
            }

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            Log?.Invoke($"updater: current={current.ToString(3)} latest={latest.ToString(3)} ({release.TagName})");
            if (latest <= current) return null;

            var expectedAsset = AssetNameTemplate.Replace("{tag}", release.TagName);
            var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, expectedAsset, StringComparison.OrdinalIgnoreCase));
            if (asset?.BrowserDownloadUrl is null)
            {
                Log?.Invoke($"updater: latest release has no asset named '{expectedAsset}'");
                return null;
            }

            return new UpdateInfo(
                Tag: release.TagName,
                Version: latest,
                DownloadUrl: asset.BrowserDownloadUrl,
                ReleaseNotes: release.Body ?? "",
                ReleaseUrl: release.HtmlUrl ?? "");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"updater: check failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Download the update ZIP, stage it next to RemSound.exe, spawn the detached
    /// install helper, and ask the App to exit so the helper can take over. Returns true if
    /// the helper was launched (caller should Application.Exit immediately afterwards);
    /// false on any failure earlier in the pipeline. A false return leaves the running
    /// instance untouched.</summary>
    public async Task<bool> DownloadAndStageInstallAsync(UpdateInfo info, CancellationToken token = default)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var stagingDir = Path.Combine(baseDir, "_update");
            var zipPath = Path.Combine(Path.GetTempPath(), $"RemSound-update-{info.Tag}.zip");
            var batchPath = Path.Combine(baseDir, "_apply-update.cmd");

            // Tidy any leftover from a previous failed attempt before we start. Also clear
            // the failure marker — the new attempt starts clean and only re-creates the
            // marker if THIS run fails.
            TryDelete(zipPath);
            TryDeleteDirectory(stagingDir);
            TryDelete(Path.Combine(baseDir, "update-failed.txt"));

            Log?.Invoke($"updater: downloading {info.DownloadUrl}");
            await using (var src = await http.GetStreamAsync(info.DownloadUrl, token).ConfigureAwait(false))
            await using (var dst = File.Create(zipPath))
            {
                await src.CopyToAsync(dst, token).ConfigureAwait(false);
            }

            Log?.Invoke($"updater: extracting to {stagingDir}");
            Directory.CreateDirectory(stagingDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            // Some release zips wrap everything in a single top-level folder
            // (e.g. "RemSound-v1.1/RemSound.exe"). Flatten if that's the case so the
            // robocopy step copies the right tree over the install location.
            var stagingRoot = ResolveStagingRoot(stagingDir);

            Log?.Invoke($"updater: writing install helper {batchPath}");
            File.WriteAllText(batchPath, BuildInstallScript(stagingRoot, baseDir));

            var pid = System.Environment.ProcessId;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{batchPath}\" {pid}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = baseDir,
            };
            Log?.Invoke($"updater: launching install helper, parent PID {pid}");
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"updater: install failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>One-shot installer batch. Waits for the supplied PID to exit (so file locks
    /// release), robocopies the staged folder over the install folder, removes the staging
    /// area, restarts RemSound.exe, and self-deletes.
    ///
    /// History: v1.0 of this helper used <c>/R:5 /W:1</c> on robocopy and unconditionally
    /// restarted RemSound regardless of whether the copy actually succeeded. On
    /// Dropbox-installed copies this failed silently — Dropbox held write locks on the
    /// existing install files for ~10–30 seconds after extraction kicked the sync off, robocopy
    /// gave up after 5 seconds, and the helper relaunched the OLD binary. The user saw the same
    /// version after "update".
    ///
    /// v1.3 hardening (2026-05-15):
    ///   * Robocopy retries bumped to <c>/R:60 /W:1</c> — up to 60 seconds per file. Dropbox
    ///     locks reliably release inside that window.
    ///   * Robocopy exit code is captured and checked. Anything ≥ 8 is a true failure; the
    ///     helper writes <c>update-failed.txt</c> to the install dir with diagnostic detail,
    ///     does NOT relaunch the old binary, and leaves the staging folder intact so the
    ///     user (or a re-run of the updater) can recover. Codes 0–7 are robocopy's
    ///     "success-ish" range (0 = nothing changed, 1 = copied, 2 = extras, 3 = both, etc).
    ///   * Helper writes a step-by-step log to <c>_update-helper.log</c> in the install dir
    ///     for post-mortem when the copy goes wrong. Robocopy's own output is appended via
    ///     <c>/LOG+:</c>.
    ///
    /// 2026-05-18 changes:
    ///   * Robocopy now also excludes <c>remsound.config.json</c> (the user's machine-local
    ///     config) and the <c>logs</c> / <c>profiles</c> / <c>recordings</c> folders, so an
    ///     update can never overwrite the user's own state — only app files are replaced.
    ///   * On SUCCESS the helper now also deletes <c>_update-helper.log</c> and any stale
    ///     <c>update-failed.txt</c> (the <c>_update</c> folder was already removed), leaving
    ///     a tidy install folder. The FAILURE branch still keeps all of them for diagnosis.
    ///
    /// The helper is detached from RemSound at start time, so it survives the parent's exit.</summary>
    private static string BuildInstallScript(string stagingRoot, string installDir)
    {
        var helperLog = Path.Combine(installDir, "_update-helper.log");
        var failureMarker = Path.Combine(installDir, "update-failed.txt");
        var stagingDir = Path.Combine(installDir, "_update");
        var remsoundExe = Path.Combine(installDir, "RemSound.exe");
        return $"""
        @echo off
        setlocal
        rem RemSound auto-installer helper. Generated by RemSoundUpdater. Self-deleting on success.
        set "PID=%~1"
        set "LOG={helperLog}"
        set "MARKER={failureMarker}"

        echo. >> "%LOG%"
        echo === %DATE% %TIME% update helper started, parent PID=%PID% === >> "%LOG%"

        :wait_loop
        tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
        if not errorlevel 1 (
          timeout /t 1 /nobreak >nul
          goto wait_loop
        )

        echo %DATE% %TIME% parent exited, starting robocopy (R:60 W:1) >> "%LOG%"
        rem /XF + /XD keep the update from ever overwriting the USER's own state: their
        rem machine-local config (remsound.config.json — holds the profiles-folder choice and
        rem startup settings) and their data folders (logs / profiles / recordings). An update
        rem replaces APP files only. build-release.ps1 already keeps those out of the release
        rem zip; this is the second line of defence so a bad zip still can't clobber them.
        robocopy "{stagingRoot}" "{installDir}" /E /IS /IT /NFL /NDL /NJH /NJS /R:60 /W:1 /XF _apply-update.cmd /XF _update-helper.log /XF update-failed.txt /XF remsound.config.json /XD logs profiles recordings _update /LOG+:"%LOG%"
        set "ROBO_EXIT=%ERRORLEVEL%"
        echo %DATE% %TIME% robocopy exit=%ROBO_EXIT% >> "%LOG%"

        if %ROBO_EXIT% GEQ 8 (
          echo RemSound could not finish updating. > "%MARKER%"
          echo. >> "%MARKER%"
          echo The new version downloaded correctly, but RemSound could not >> "%MARKER%"
          echo replace its program files with it. Nothing is broken - your >> "%MARKER%"
          echo current version still works and has been left as it was. >> "%MARKER%"
          echo. >> "%MARKER%"
          echo What to do: >> "%MARKER%"
          echo. >> "%MARKER%"
          echo   1. Close RemSound completely. >> "%MARKER%"
          echo   2. Wait about 30 seconds. A file-syncing, backup or antivirus >> "%MARKER%"
          echo      program may have been using RemSound's files; this gives it >> "%MARKER%"
          echo      time to finish and let go of them. >> "%MARKER%"
          echo   3. Start RemSound again, open the Help menu, and choose >> "%MARKER%"
          echo      Check for updates to try once more. It usually works on the >> "%MARKER%"
          echo      second attempt. >> "%MARKER%"
          echo. >> "%MARKER%"
          echo If it still will not update: the new version's files are ready >> "%MARKER%"
          echo and waiting in the folder named _update, next to RemSound.exe. >> "%MARKER%"
          echo You can finish the update yourself by copying everything from >> "%MARKER%"
          echo inside that _update folder into this folder, replacing the older >> "%MARKER%"
          echo files when asked. >> "%MARKER%"
          echo. >> "%MARKER%"
          echo Once RemSound has updated successfully you can delete this file. >> "%MARKER%"
          echo Technical details for support are in _update-helper.log in this folder. >> "%MARKER%"
          echo %DATE% %TIME% FAILURE: robocopy exit=%ROBO_EXIT%, update folder kept, NOT restarting RemSound >> "%LOG%"
          del "%~f0"
          exit /b %ROBO_EXIT%
        )

        rmdir /S /Q "{stagingDir}" 2>nul
        echo %DATE% %TIME% update applied OK, cleaning up and restarting RemSound >> "%LOG%"
        del "%MARKER%" 2>nul
        start "" "{remsoundExe}"
        rem Success cleanup: the staged _update folder is already gone (rmdir above). Now drop
        rem the helper log and the failure marker too, so a clean update leaves the install
        rem folder tidy with no _update / _update-helper.log / update-failed.txt left behind.
        rem (The FAILURE branch above deliberately keeps all of these for diagnosis.)
        rem The helper log is deleted last, after the final line is written to it.
        del "%LOG%" 2>nul
        del "%~f0"
        """;
    }

    /// <summary>If the zip extracted to a single subfolder (typical when GitHub zips a tag),
    /// return that subfolder so the copy works from the inner level. Otherwise return the
    /// staging dir itself.</summary>
    private static string ResolveStagingRoot(string stagingDir)
    {
        var subdirs = Directory.GetDirectories(stagingDir);
        var files = Directory.GetFiles(stagingDir);
        if (files.Length == 0 && subdirs.Length == 1) return subdirs[0];
        return stagingDir;
    }

    /// <summary>Parses a release tag like <c>v1.2</c> or <c>1.2.3</c> into a <see cref="Version"/>.
    /// Leading "v" is stripped. Missing minor/build parts get filled with zeros so the result
    /// always compares meaningfully against <see cref="Assembly.GetName"/>.Version.</summary>
    /// <summary>True if <paramref name="tag"/> is a RemSound client release tag — e.g.
    /// <c>v1.6</c>, <c>1.6</c>, <c>1.6.0</c> — rather than something else hosted in the same
    /// GitHub repo, notably the relay server's <c>server-vX.Y</c> releases. Test: after an
    /// optional leading <c>v</c>, the first character must be a digit. <c>server-v2.3</c>
    /// starts with 's' and is rejected; <c>v1.6</c> is accepted. The updater must filter on
    /// this because it lists all repo releases and the server publishes into the same repo.</summary>
    public static bool IsClientReleaseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var trimmed = tag.TrimStart('v', 'V').Trim();
        return trimmed.Length > 0 && char.IsDigit(trimmed[0]);
    }

    public static Version ParseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return new Version(0, 0, 0);
        var trimmed = tag.TrimStart('v', 'V').Trim();
        var parts = trimmed.Split('.', '-', '+');
        var nums = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            int.TryParse(parts[i], out nums[i]);
        }
        return new Version(nums[0], nums[1], nums[2]);
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        // GitHub rejects API requests without a User-Agent. The header doubles as a way for
        // their abuse team to contact us if our polling misbehaves at scale.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemSound-Updater", "1.0"));
        return c;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* ignore */ } }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}

/// <summary>What <see cref="RemSoundUpdater.CheckForUpdateAsync"/> returns when there's a
/// newer release available. <see cref="ReleaseNotes"/> is the raw Markdown body of the
/// release on GitHub — show it directly in a confirmation dialog if the install isn't
/// silent.</summary>
internal sealed record UpdateInfo(
    string Tag,
    Version Version,
    string DownloadUrl,
    string ReleaseNotes,
    string ReleaseUrl);
