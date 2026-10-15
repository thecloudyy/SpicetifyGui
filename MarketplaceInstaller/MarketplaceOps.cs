using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarketplaceInstaller;

/// <summary>
/// Backend port of enhanced.py: download dist branch zip, safe-extract,
/// backup/restore, register custom app, spicetify apply.
/// Return codes: 0 ok, else failure. Updates always reinstall the latest
/// build — "already up to date" is a display-only badge, never a skip.
/// </summary>
public static class MarketplaceOps
{
    private static readonly Regex AnsiRe = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    // One shared client per role: allocating an HttpClient per call exhausts
    // sockets on repeated status checks, and the two roles need different timeouts.
    private static readonly HttpClient ApiClient = CreateClient(TimeSpan.FromSeconds(15));
    private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(10));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(MarketplacePaths.UserAgent);
        c.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static string StripAnsi(string s) => AnsiRe.Replace(s, "");
    private static void Say(IProgress<string>? log, string msg)
    {
        try { log?.Report(msg); } catch { }
    }

    public static bool IsSpicetifyInstalled() => SpicetifyCli.IsInstalled();

    public static string SpicetifyVersion() => SpicetifyCli.Version();

    /// <summary>
    /// Presence check. manifest.json has no version field, so a folder
    /// containing it (plus index.js) counts as installed.
    /// </summary>
    public static bool IsPresent(string marketplaceDir)
    {
        try
        {
            return File.Exists(Path.Combine(marketplaceDir, "manifest.json"));
        }
        catch
        {
            return false;
        }
    }

    public static string? LocalVersion(string marketplaceDir)
    {
        try
        {
            string manifest = Path.Combine(marketplaceDir, "manifest.json");
            if (File.Exists(manifest))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("version", out var v)
                    && v.GetString()?.Trim() is string s && s != "")
                    return s;
            }
        }
        catch { }
        // Manifest carries no version field — fall back to the tag we recorded
        // when this app last installed it.
        return InstallState.GetRecordedTag();
    }

    public static Task<string?> LatestReleaseVersionAsync(CancellationToken ct) =>
        LatestReleaseTagAsync(MarketplacePaths.LatestReleaseApi, ct);

    /// <summary>Latest spicetify/cli release tag (the Spicetify CLI itself), e.g. "2.45.3".</summary>
    public static Task<string?> LatestSpicetifyCliVersionAsync(CancellationToken ct) =>
        LatestReleaseTagAsync(MarketplacePaths.LatestSpicetifyCliApi, ct);

    private static async Task<string?> LatestReleaseTagAsync(string api, CancellationToken ct)
    {
        try
        {
            string json = await ApiClient.GetStringAsync(api, ct);
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? t.GetString() ?? "" : "";
            tag = tag.TrimStart('v').Trim();
            return tag == "" ? null : tag;
        }
        catch
        {
            return null;
        }
    }

    public static async Task DownloadFileAsync(string url, string dest,
        IProgress<string>? log, IProgress<double>? progress, CancellationToken ct)
    {
        double delay = 1.0;
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Say(log, $"Downloading... (attempt {attempt}/3)");
                using var resp = await DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(dest);
                byte[] buf = new byte[64 * 1024];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total is > 0) progress?.Report(done * 100.0 / total.Value);
                }
                Say(log, $"Downloaded {done / 1024} KB.");
                Say(log, "Download verified.");
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Say(log, $"Download attempt {attempt} failed.");
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                delay *= 2;
            }
        }
        throw new IOException($"Download failed: {last?.Message}");
    }

    public static void SafeExtract(string zipPath, string destDir)
    {
        string dest = Path.GetFullPath(destDir);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
            if (!target.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && target != dest)
                throw new InvalidDataException("Unsafe path in archive: " + entry.FullName);
        }
        zip.ExtractToDirectory(dest, overwriteFiles: true);
    }

    public static void ExtractAndInstall(string zipPath, string tmpDir,
        string marketplaceDir, IProgress<string>? log)
    {
        SafeExtract(zipPath, tmpDir);

        string? extracted = null;
        foreach (string d in Directory.GetDirectories(tmpDir))
        {
            if (File.Exists(Path.Combine(d, "manifest.json"))
                || File.Exists(Path.Combine(d, "index.js")))
            {
                extracted = d;
                break;
            }
        }
        extracted ??= Directory.GetDirectories(tmpDir).FirstOrDefault();

        Say(log, "Installing files...");
        Directory.CreateDirectory(Path.GetDirectoryName(marketplaceDir) ?? marketplaceDir);
        if (Directory.Exists(marketplaceDir))
            Directory.Delete(marketplaceDir, recursive: true);

        if (extracted != null)
        {
            Directory.Move(extracted, marketplaceDir);
        }
        else if (File.Exists(Path.Combine(tmpDir, "manifest.json"))
            || File.Exists(Path.Combine(tmpDir, "index.js")))
        {
            // Zip carried the build at its root — move the files over.
            Directory.Move(tmpDir, marketplaceDir);
        }
        else
        {
            throw new InvalidDataException("Could not find extracted folder.");
        }
    }

    public static string? BackupExisting(string marketplaceDir, IProgress<string>? log)
    {
        if (!Directory.Exists(marketplaceDir)) return null;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backup = marketplaceDir + $".backup-{stamp}";
        Say(log, "Backing up existing install.");
        Directory.Move(marketplaceDir, backup);
        return backup;
    }

    public static void RestoreBackup(string? backup, string marketplaceDir, IProgress<string>? log)
    {
        if (backup == null || !Directory.Exists(backup)) return;
        if (Directory.Exists(marketplaceDir))
            Directory.Delete(marketplaceDir, recursive: true);
        try
        {
            Directory.Move(backup, marketplaceDir);
            Say(log, "Restored previous install.");
        }
        catch (Exception ex)
        {
            Say(log, "Restore failed: " + ex.Message);
        }
    }

    public static void UpgradeCli(IProgress<string>? log, CancellationToken ct = default)
    {
        Say(log, "Updating Spicetify CLI...");
        var (code, stdout) = Run(SpicetifyExe(), "upgrade", ct);
        foreach (string line in stdout.Split('\n'))
        {
            string t = StripAnsi(line).Trim();
            if (t != "") Say(log, t);
        }
        if (code != 0)
            Say(log, "Spicetify upgrade reported an issue; continuing.");
    }

    /// <summary>Registers the custom app in config-xpui.ini. Deliberately not
    /// cancellable: killing a config write mid-run can leave the INI half-written.</summary>
    public static void RegisterCustomApp(IProgress<string>? log)
    {
        Say(log, "Registering Marketplace app...");
        try
        {
            string ini = MarketplacePaths.ConfigIni;
            if (File.Exists(ini))
            {
                string content = File.ReadAllText(ini);
                var m = Regex.Match(content, @"^\s*custom_apps\s*=\s*(.*)$",
                    RegexOptions.Multiline);
                if (m.Success)
                {
                    var apps = m.Groups[1].Value.Split('|')
                        .Select(a => a.Trim()).Where(a => a != "");
                    if (apps.Contains(MarketplacePaths.AppName))
                    {
                        Say(log, "Marketplace already registered.");
                        return;
                    }
                }
            }
        }
        catch { }
        // Same registration the official Marketplace install script performs:
        // drop any legacy entry, add "marketplace", and make sure the CSS/color
        // injection the Marketplace UI relies on is on.
        Run(SpicetifyExe(), "config custom_apps spicetify-marketplace- -q");
        Run(SpicetifyExe(), $"config custom_apps {MarketplacePaths.AppName}");
        Run(SpicetifyExe(), "config inject_css 1");
        Run(SpicetifyExe(), "config replace_colors 1");
    }

    /// <summary>Runs `spicetify apply`. Deliberately not cancellable: killing it
    /// mid-run could leave the patched xpui inconsistent.</summary>
    public static bool ApplySpicetify(IProgress<string>? log)
    {
        Say(log, "Applying changes to Spotify...");
        var (code, stdout) = Run(SpicetifyExe(), "apply");
        foreach (string line in stdout.Split('\n'))
        {
            string t = StripAnsi(line).Trim();
            if (t != "") Say(log, t);
        }
        if (code != 0)
        {
            Say(log, "spicetify apply failed.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// spicetify apply starts Spotify on its own — give it a moment, and only
    /// launch it ourselves if it never shows up.
    /// </summary>
    public static void EnsureSpotifyReopened(IProgress<string>? log, CancellationToken ct)
    {
        for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
        {
            if (Spotify.IsRunning()) break;
            Thread.Sleep(500);
        }
        if (Spotify.IsRunning())
            Say(log, "Spotify restarted.");
        else
            LaunchSpotify(log);
    }

    public static void LaunchSpotify(IProgress<string>? log)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                string? exe = FindSpotifyExe();
                psi = exe != null
                    ? new ProcessStartInfo(exe) { UseShellExecute = true }
                    : new ProcessStartInfo("Spotify.exe") { UseShellExecute = true };
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new ProcessStartInfo("open", "-a Spotify")
                    { UseShellExecute = false, CreateNoWindow = true };
            }
            else
            {
                psi = new ProcessStartInfo("spotify", "")
                    { UseShellExecute = false, CreateNoWindow = true };
            }
            Process.Start(psi);
            Say(log, "Spotify restarted.");
        }
        catch (Exception ex)
        {
            Say(log, "Could not restart Spotify: " + ex.Message);
        }
    }

    private static string? FindSpotifyExe()
    {
        try
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Spotify", "Spotify.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Spotify", "Spotify.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Spotify", "Spotify.exe"),
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
        }
        catch { }
        return null;
    }
    public static void EnsureSpotifyClosed(IProgress<string>? log, CancellationToken ct)
    {
        Thread.Sleep(1500);
        if (ct.IsCancellationRequested) return;
        if (Spotify.IsRunning())
            Spotify.Close(log, ct);
        Thread.Sleep(1000);
        if (!ct.IsCancellationRequested && Spotify.IsRunning())
        {
            Say(log, "Spotify restarted itself — closing it again.");
            Spotify.Close(log, ct);
        }
        if (!Spotify.IsRunning())
            Say(log, "Spotify left closed — start it yourself when ready.");
    }

    /// <summary>Resolved spicetify executable — a CLI installed during this run
    /// lives in %LOCALAPPDATA%\spicetify and is not on the process PATH yet.</summary>
    private static string SpicetifyExe() => SpicetifyCli.FindExecutable() ?? "spicetify";

    /// <summary>Runs a CLI child with a bounded lifetime. Both output streams are
    /// drained concurrently — reading only stdout deadlocks as soon as the child
    /// fills the unread stderr pipe (its own writes block, stdout never closes).
    /// stderr is folded into the returned text so failures are visible in the log.
    /// A child still alive after <paramref name="timeoutMs"/>, or when <paramref
    /// name="ct"/> fires, is killed instead of being orphaned.</summary>
    private static (int code, string stdout) Run(string file, string args,
        CancellationToken ct = default, int timeoutMs = 120000)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (1, "");
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            using var killOnCancel = ct.Register(
                () => { try { if (!p.HasExited) p.Kill(); } catch { } });

            bool exited = p.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                p.WaitForExit(5000);
            }
            try { Task.WaitAll(new Task[] { outTask, errTask }, 5000); }
            catch { /* a faulted read yields partial output below */ }

            string stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
            string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";
            string combined = stderr.Trim().Length == 0
                ? stdout
                : stdout + (stdout.EndsWith('\n') || stdout.Length == 0 ? "" : "\n") + stderr;

            ct.ThrowIfCancellationRequested();
            if (!exited) return (1, $"{Path.GetFileName(file)} {args} timed out.");
            return (p.ExitCode, combined);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static async Task<int> RunUpdateAsync(string marketplaceDir,
        IProgress<string>? log, IProgress<double>? progress,
        CancellationToken ct)
    {
        progress?.Report(-1);
        Say(log, "Starting update.");

        if (!IsSpicetifyInstalled())
        {
            // Install support: set the CLI up instead of bailing out.
            if (!await SpicetifyCli.InstallAsync(log, ct) || !IsSpicetifyInstalled())
            {
                Say(log, "Spicetify CLI not found.");
                return 2;
            }
        }

        string? backup = null;
        string? latestTag = null;
        bool wasRunning = false;
        bool spotifyHandled = false;
        try
        {
            Say(log, "Checking for updates...");
            UpgradeCli(log, ct);

            latestTag = await LatestReleaseVersionAsync(ct);

            // Always reinstall the latest build — the Update button never
            // skips on "already up to date"; freshness is display-only.
            wasRunning = Spotify.IsRunning();
            if (wasRunning)
            {
                Say(log, "Spotify is open — it will be restarted when done.");
                await Task.Delay(1200, ct);
                Spotify.Close(log, ct);
                ct.ThrowIfCancellationRequested();
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(marketplaceDir) ?? marketplaceDir);

            if (Directory.Exists(marketplaceDir))
                backup = BackupExisting(marketplaceDir, log);

            string tmp = Path.Combine(Path.GetTempPath(),
                "spicetify-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string zip = Path.Combine(tmp, "marketplace.zip");
                Say(log, "Downloading update... fetching latest build.");
                try
                {
                    // Official release asset first (same source as the official
                    // install script), dist-branch build as a fallback.
                    string url = latestTag != null
                        ? MarketplacePaths.ReleaseZipUrl(latestTag)
                        : MarketplacePaths.LatestReleaseZipUrl;
                    try
                    {
                        await DownloadFileAsync(url, zip, log, progress, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // a cancel must read as "Cancelled.", not a download failure
                    }
                    catch (Exception first) when (url != MarketplacePaths.DistZipUrl)
                    {
                        Say(log, "Release download failed: " + first.Message);
                        Say(log, "Falling back to the dist branch build...");
                        await DownloadFileAsync(MarketplacePaths.DistZipUrl, zip, log, progress, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Say(log, "Download failed: " + ex.Message);
                    RestoreBackup(backup, marketplaceDir, log);
                    return 5;
                }

                ct.ThrowIfCancellationRequested();
                Say(log, "Installing... extracting files.");
                progress?.Report(-1);
                try
                {
                    ExtractAndInstall(zip, Path.Combine(tmp, "x"), marketplaceDir, log);
                }
                catch (Exception ex)
                {
                    Say(log, "Install failed: " + ex.Message);
                    RestoreBackup(backup, marketplaceDir, log);
                    return 6;
                }
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }

            RegisterCustomApp(log);

            Say(log, "Applying changes... almost done.");
            if (!ApplySpicetify(log))
            {
                RestoreBackup(backup, marketplaceDir, log);
                return 7;
            }
            if (wasRunning)
                EnsureSpotifyReopened(log, ct);
            else
                EnsureSpotifyClosed(log, ct);
            spotifyHandled = true;
        }
        catch (OperationCanceledException)
        {
            RestoreBackup(backup, marketplaceDir, log);
            throw;
        }
        catch (Exception ex)
        {
            Say(log, "Unexpected error: " + ex.Message);
            RestoreBackup(backup, marketplaceDir, log);
            return 1;
        }
        finally
        {
            // Spotify was closed with a promise to restart it — a failed or
            // cancelled update still owes the user their player back.
            if (wasRunning && !spotifyHandled)
                EnsureSpotifyReopened(log, ct);
        }

        string ver = latestTag ?? LocalVersion(marketplaceDir) ?? "latest";
        if (latestTag != null)
            InstallState.RecordTag(latestTag);
        Say(log, $"Updated to v{ver}.");
        Say(log, "Update complete.");
        return 0;
    }

    public static Task<int> InstallFlowAsync(string marketplaceDir,
        IProgress<string>? log = null, IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        RunUpdateAsync(marketplaceDir, log, progress, ct);

    public static Task<int> UpdateFlowAsync(string marketplaceDir,
        IProgress<string>? log = null, IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        RunUpdateAsync(marketplaceDir, log, progress, ct);

    public static int UninstallFlow(string marketplaceDir, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        Say(log, "Uninstalling Marketplace...");
        bool wasRunning = Spotify.IsRunning();
        bool finished = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (wasRunning)
                Spotify.Close(log, ct);
            ct.ThrowIfCancellationRequested();

            if (Directory.Exists(marketplaceDir))
            {
                string backup = marketplaceDir +
                    ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Directory.Move(marketplaceDir, backup);
                Say(log, "Moved install to backup: " + Path.GetFileName(backup));
            }
            // Not interrupted on cancel: apply is fast and local, and killing it
            // mid-write could leave the patched xpui inconsistent.
            ApplySpicetify(log);
            EnsureSpotifyClosed(log, ct);
            InstallState.Clear();
            Say(log, "Uninstall done. Original Spotify restored.");
            finished = true;
            return 0;
        }
        finally
        {
            // A successful uninstall deliberately leaves Spotify closed; a
            // cancelled or failed one must give back the player it just closed.
            if (!finished && wasRunning)
                EnsureSpotifyReopened(log, ct);
        }
    }
}
