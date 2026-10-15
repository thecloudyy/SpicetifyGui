using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MarketplaceInstaller.Gui.Services;

internal sealed record SelfUpdateInfo(
    string Tag,
    string Name,
    string Notes,
    string FileName,
    string FileUrl,
    long FileSize,
    string FileDigest,
    string ChecksumUrl,
    string AppChecksumUrl);

/// <summary>
/// Self-update service for SpicetifyGui. Releases live under a single
/// always-live tag `latest` — never versioned tags — so Reinstall always pulls
/// releases/tags/latest, downloads the SpicetifyGui-Setup-*-win-x64.exe asset
/// to %TEMP%, and verifies it against the release's SHA-256: first the
/// published .sha256 sidecar asset, then the API digest field. No checksum
/// available is a failure, not a pass. It then launches the setup /SILENT and
/// exits — the installer closes this instance (CloseApplications) and relaunches
/// it when done.
/// </summary>
internal sealed class SelfUpdater
{
    public const string Owner = "thecloudyy";
    public const string Repo = "SpicetifyGui";
    public const string ExeName = "SpicetifyGui.exe";
    private const string AssetPrefix = "SpicetifyGui-Setup-";
    private const string AssetSuffix = "-win-x64.exe";

    /// <summary>Release asset carrying the sha256 of the installed app exe —
    /// how the UI knows whether the running build is the latest one.</summary>
    private const string AppChecksumAsset = ExeName + ".sha256";

    private static readonly HttpClient _api = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient _dl = new() { Timeout = Timeout.InfiniteTimeSpan };

    static SelfUpdater()
    {
        _api.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpicetifyGui", "1.0"));
        _api.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _dl.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpicetifyGui", "1.0"));
    }

    private static SelfUpdateInfo? InfoFromRelease(JsonElement root)
    {
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
        // The live tag is "latest" — deliberately not parsed as a version.
        string tag = tagEl.GetString() ?? "";
        if (tag == "") return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;

        string fileName = "", fileUrl = "", fileDigest = "", checksumUrl = "", appChecksumUrl = "";
        long fileSize = 0;
        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (url == "") continue;
            if (name.Equals(AppChecksumAsset, StringComparison.OrdinalIgnoreCase))
            {
                appChecksumUrl = url;
                continue;
            }
            if (name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(AssetSuffix + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
                continue;
            }
            if (!name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
            string digest = "";
            if (a.TryGetProperty("digest", out var d))
            {
                string raw = d.GetString() ?? "";
                if (raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    digest = raw.Substring(7).ToLowerInvariant();
            }
            fileName = name;
            fileUrl = url;
            fileSize = size;
            fileDigest = digest;
        }
        if (fileUrl == "") return null;

        string relName = root.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";
        string notes = root.TryGetProperty("body", out var rb) ? rb.GetString() ?? "" : "";
        return new SelfUpdateInfo(tag, relName, notes,
            fileName, fileUrl, fileSize, fileDigest, checksumUrl, appChecksumUrl);
    }

    private static string RateLimitSuffix(HttpResponseMessage res)
    {
        try
        {
            if (res.Headers.TryGetValues("x-ratelimit-reset", out var vals) &&
                long.TryParse(System.Linq.Enumerable.FirstOrDefault(vals), out long unix))
            {
                var t = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime();
                return $" Limit resets at {t:HH:mm}.";
            }
        }
        catch { }
        return "";
    }

    /// <summary>The always-live release: releases/tags/latest, with the classic
    /// releases/latest endpoint as a fallback for a repo not yet migrated.</summary>
    public async Task<SelfUpdateInfo?> GetLatestWithAssetAsync(CancellationToken ct = default)
    {
        var info = await GetReleaseAsync($"releases/tags/latest", ct);
        if (info != null) return info;
        return await GetReleaseAsync("releases/latest", ct);
    }

    private async Task<SelfUpdateInfo?> GetReleaseAsync(string endpoint, CancellationToken ct)
    {
        using var res = await _api.GetAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/{endpoint}", ct);
        if (!res.IsSuccessStatusCode)
        {
            if ((int)res.StatusCode == 404) return null; // release not published
            int code = (int)res.StatusCode;
            if (code is 403 or 429)
                throw new HttpRequestException(
                    "GitHub API rate limit exceeded." + RateLimitSuffix(res));
            throw new HttpRequestException(
                $"GitHub API returned {code} {res.ReasonPhrase}.");
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return InfoFromRelease(doc.RootElement);
    }

    /// <summary>Downloads a .sha256 asset and returns the hex digest it
    /// advertises (first 64-hex token of the file).</summary>
    internal static async Task<string> FetchChecksumAsync(string url, CancellationToken ct)
    {
        using var res = await _dl.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        string text = await res.Content.ReadAsStringAsync(ct);
        var m = System.Text.RegularExpressions.Regex.Match(text, "[0-9a-fA-F]{64}");
        return m.Success ? m.Value.ToLowerInvariant() : "";
    }

    /// <summary>Sha256 of the executable this process was started from — the
    /// file Inno installed, byte-identical to the published single-file build.</summary>
    internal static string LocalExeSha256()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            return Sha256File(path);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Is the running build the one the `latest` release ships? True/false,
    /// or null when it cannot be determined (no sidecar published, offline, …).</summary>
    internal async Task<bool?> IsRunningBuildLatestAsync(CancellationToken ct = default)
    {
        var info = await GetLatestWithAssetAsync(ct);
        if (info == null || info.AppChecksumUrl == "") return null;
        string expected = await FetchChecksumAsync(info.AppChecksumUrl, ct);
        string local = LocalExeSha256();
        if (expected == "" || local == "") return null;
        return string.Equals(local, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadFileAsync(string url, string dest, long size,
        IProgress<double> progress, CancellationToken ct)
    {
        using var res = await _dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? size;
        await using var net = await res.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        byte[] buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await net.ReadAsync(buf, ct)) > 0)
        {
            await file.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total * 0.95);
        }
    }

    /// <summary>True when the file matches the expected sha256 digest.</summary>
    private static bool DigestMatches(string path, string expected)
    {
        return string.Equals(Sha256File(path), expected.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Downloads the setup exe to %TEMP% and verifies it against the release's
    /// SHA-256: the published .sha256 sidecar asset first, then the API digest
    /// field. A missing checksum is a failure and a mismatch deletes the file.
    /// </summary>
    public async Task<string> DownloadAsync(SelfUpdateInfo update, IProgress<double> progress, CancellationToken ct = default)
    {
        string dir = Path.GetTempPath();
        string exePath = Path.Combine(dir, update.FileName);
        try { if (File.Exists(exePath)) File.Delete(exePath); } catch { /* overwritten below */ }

        await DownloadFileAsync(update.FileUrl, exePath, update.FileSize, progress, ct);

        string expected = "";
        if (update.ChecksumUrl != "")
        {
            try { expected = await FetchChecksumAsync(update.ChecksumUrl, ct); }
            catch { expected = ""; }
        }
        if (expected == "") expected = update.FileDigest.Trim();
        if (expected == "")
        {
            try { File.Delete(exePath); } catch { /* best effort */ }
            throw new InvalidDataException(
                update.FileName + " has no SHA-256 published — refusing to install it.");
        }

        if (!DigestMatches(exePath, expected))
        {
            try { File.Delete(exePath); } catch { /* best effort */ }
            throw new InvalidDataException("Download does not match the release SHA-256 for " + update.FileName + ".");
        }
        progress?.Report(1.0);
        return dir;
    }

    /// <summary>
    /// Launches the setup silently and returns. The installer closes this
    /// instance (CloseApplications) and relaunches the app when done.
    /// </summary>
    public static void RunSetupAndExit(string setupPath)
    {
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT") { UseShellExecute = true });
    }
}
