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
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
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
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOpts, token).ConfigureAwait(false);
            if (release?.TagName is null)
            {
                Log?.Invoke("updater: response had no tag_name");
                return null;
            }

            var latest = ParseTag(release.TagName);
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
        robocopy "{stagingRoot}" "{installDir}" /E /IS /IT /NFL /NDL /NJH /NJS /R:60 /W:1 /XF _apply-update.cmd /XF _update-helper.log /XF update-failed.txt /LOG+:"%LOG%"
        set "ROBO_EXIT=%ERRORLEVEL%"
        echo %DATE% %TIME% robocopy exit=%ROBO_EXIT% >> "%LOG%"

        if %ROBO_EXIT% GEQ 8 (
          echo Update copy FAILED. > "%MARKER%"
          echo Robocopy exit code = %ROBO_EXIT% ^(anything ^>= 8 is a real failure^). >> "%MARKER%"
          echo Staged files are intact at: {stagingDir} >> "%MARKER%"
          echo Helper log: %LOG% >> "%MARKER%"
          echo. >> "%MARKER%"
          echo Most common cause: Dropbox or another file-sync app was holding write locks >> "%MARKER%"
          echo on the existing RemSound binaries during the update window. Close RemSound, >> "%MARKER%"
          echo wait 30 seconds for the sync to settle, then either: >> "%MARKER%"
          echo   * Re-launch RemSound and try Help -^> Check for updates again, OR >> "%MARKER%"
          echo   * Manually copy everything from the staged folder above into this folder. >> "%MARKER%"
          echo %DATE% %TIME% FAILURE: leaving staging intact, NOT restarting RemSound >> "%LOG%"
          del "%~f0"
          exit /b %ROBO_EXIT%
        )

        rmdir /S /Q "{stagingDir}" 2>nul
        echo %DATE% %TIME% staging removed, restarting RemSound >> "%LOG%"
        start "" "{remsoundExe}"
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
