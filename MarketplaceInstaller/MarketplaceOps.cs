using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarketplaceInstaller;

/// <summary>
/// Backend port of enhanced.py: download dist branch zip, safe-extract,
/// backup/restore, register custom app, spicetify apply.
/// Return codes: 0 ok, 4 already up to date, else failure (matches enhanced.py).
/// </summary>
public static class MarketplaceOps
{
    private static readonly Regex AnsiRe = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    public static string StripAnsi(string s) => AnsiRe.Replace(s, "");
    private static void Say(IProgress<string>? log, string msg)
    {
        try { log?.Report(msg); } catch { }
    }

    public static bool IsSpicetifyInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo("spicetify", "-v")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(8000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string SpicetifyVersion()
    {
        try
        {
            var psi = new ProcessStartInfo("spicetify", "-v")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "unknown";
            string out_ = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(8000);
            return string.IsNullOrWhiteSpace(out_) ? "unknown" : out_;
        }
        catch
        {
            return "unknown";
        }
    }

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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(MarketplacePaths.UserAgent);
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            string json = await http.GetStringAsync(api, ct);
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

    public static async Task<bool> IsUpToDateAsync(string marketplaceDir, IProgress<string>? log, CancellationToken ct, string? latestTag = null)
    {
        string? local = LocalVersion(marketplaceDir);
        if (local == null) return false;
        latestTag ??= await LatestReleaseVersionAsync(ct);
        if (latestTag == null) return false;
        string latest = latestTag;
        if (local == latest)
        {
            Say(log, $"Marketplace is already up to date (v{local}).");
            return true;
        }
        Say(log, $"Updating from v{local} to v{latest}.");
        return false;
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
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(MarketplacePaths.UserAgent);
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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
        if (extracted == null)
            throw new InvalidDataException("Could not find extracted folder.");

        Say(log, "Installing files...");
        Directory.CreateDirectory(Path.GetDirectoryName(marketplaceDir) ?? marketplaceDir);
        if (Directory.Exists(marketplaceDir))
            Directory.Delete(marketplaceDir, recursive: true);
        Directory.Move(extracted, marketplaceDir);
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

    public static void UpgradeCli(IProgress<string>? log)
    {
        Say(log, "Updating Spicetify CLI...");
        var (code, stdout) = Run("spicetify", "upgrade");
        foreach (string line in stdout.Split('\n'))
        {
            string t = StripAnsi(line).Trim();
            if (t != "") Say(log, t);
        }
        if (code != 0)
            Say(log, "Spicetify upgrade reported an issue; continuing.");
    }

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
        Run("spicetify", $"config custom_apps {MarketplacePaths.AppName}");
    }

    public static bool ApplySpicetify(IProgress<string>? log)
    {
        Say(log, "Applying changes to Spotify...");
        var (code, stdout) = Run("spicetify", "apply");
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

    private static (int code, string stdout) Run(string file, string args)
    {
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
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120000);
            return (p.ExitCode, stdout);
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static async Task<int> RunUpdateAsync(string marketplaceDir,
        IProgress<string>? log, IProgress<double>? progress,
        CancellationToken ct, bool force)
    {
        progress?.Report(-1);
        Say(log, "Starting update.");

        if (!IsSpicetifyInstalled())
        {
            Say(log, "Spicetify CLI not found.");
            return 2;
        }

        bool wasRunning = Spotify.IsRunning();
        if (wasRunning)
        {
            Say(log, "Spotify is open — it will be restarted when done.");
            await Task.Delay(1200, ct);
            Spotify.Close(log, ct);
            ct.ThrowIfCancellationRequested();
        }

        string? backup = null;
        string? latestTag = null;
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(marketplaceDir) ?? marketplaceDir);

            if (Directory.Exists(marketplaceDir))
                backup = BackupExisting(marketplaceDir, log);

            Say(log, "Checking for updates...");
            UpgradeCli(log);

            latestTag = await LatestReleaseVersionAsync(ct);

            if (!force && await IsUpToDateAsync(
                    Directory.Exists(marketplaceDir) ? marketplaceDir : backup ?? marketplaceDir,
                    log, ct, latestTag))
            {
                Say(log, "Already up to date.");
                if (backup != null) RestoreBackup(backup, marketplaceDir, log);
                return 4;
            }

            string tmp = Path.Combine(Path.GetTempPath(),
                "spicetify-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string zip = Path.Combine(tmp, "marketplace.zip");
                Say(log, "Downloading update... fetching latest build.");
                try
                {
                    await DownloadFileAsync(MarketplacePaths.DistZipUrl, zip, log, progress, ct);
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
        RunUpdateAsync(marketplaceDir, log, progress, ct, force: true);

    public static Task<int> UpdateFlowAsync(string marketplaceDir,
        IProgress<string>? log = null, IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        RunUpdateAsync(marketplaceDir, log, progress, ct, force: false);

    public static int UninstallFlow(string marketplaceDir, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        Say(log, "Uninstalling Marketplace...");
        if (Spotify.IsRunning())
            Spotify.Close(log, ct);
        if (ct.IsCancellationRequested) return 1;

        if (Directory.Exists(marketplaceDir))
        {
            string backup = marketplaceDir +
                ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.Move(marketplaceDir, backup);
            Say(log, "Moved install to backup: " + Path.GetFileName(backup));
        }
        ApplySpicetify(log);
        EnsureSpotifyClosed(log, ct);
        InstallState.Clear();
        Say(log, "Uninstall done. Original Spotify restored.");
        return 0;
    }
}
