using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MarketplaceInstaller.Gui.Services;

internal sealed record SelfUpdateInfo(
    string Version,
    string Name,
    string Notes,
    string FileName,
    string FileUrl,
    long FileSize,
    string FileDigest);

/// <summary>
/// Self-update service for SpicetifyGui — Element's reinstall method, ported from
/// Element's AppUpdateService. The app keeps its own version pinned, so there is no
/// version comparison: Reinstall takes releases/latest in thecloudyy/SpicetifyGui,
/// downloads the SpicetifyGui-Setup-*-win-x64.exe asset to %TEMP%, verifies it against
/// the release asset's advertised sha256 digest, then launches it /SILENT and exits —
/// the installer closes this instance (CloseApplications) and relaunches it when done.
/// </summary>
internal sealed class SelfUpdater
{
    public const string Owner = "thecloudyy";
    public const string Repo = "SpicetifyGui";
    public const string ExeName = "SpicetifyGui.exe";
    private const string AssetPrefix = "SpicetifyGui-Setup-";
    private const string AssetSuffix = "-win-x64.exe";

    private static readonly HttpClient _api = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient _dl = new() { Timeout = Timeout.InfiniteTimeSpan };

    static SelfUpdater()
    {
        _api.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpicetifyGui", "1.0"));
        _api.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _dl.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpicetifyGui", "1.0"));
    }

    internal static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(1, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string t = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(t, out var v))
        {
            version = new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
            return true;
        }
        return false;
    }

    private static SelfUpdateInfo? InfoFromRelease(JsonElement root)
    {
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
        if (!TryParseTag(tagEl.GetString() ?? "", out var version)) return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;

        string fileName = "", fileUrl = "", fileDigest = "";
        long fileSize = 0;
        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (url == "") continue;
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
        return new SelfUpdateInfo(version.ToString(), relName, notes,
            fileName, fileUrl, fileSize, fileDigest);
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

    /// <summary>Live latest release, if it carries the setup exe asset.</summary>
    public async Task<SelfUpdateInfo?> GetLatestWithAssetAsync(CancellationToken ct = default)
    {
        using var res = await _api.GetAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);
        if (!res.IsSuccessStatusCode)
        {
            if ((int)res.StatusCode == 404) return null; // no release published
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

    /// <summary>
    /// True when the file matches the asset's advertised sha256 digest, and also when
    /// the asset advertises none — an older release without one is not treated as
    /// corrupt (same semantics as Element's AssetHash.Matches).
    /// </summary>
    private static bool DigestMatches(string path, string assetDigest)
    {
        if (string.IsNullOrWhiteSpace(assetDigest)) return true; // nothing to check against
        return string.Equals(Sha256File(path), assetDigest.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Downloads the setup exe to %TEMP% (reusing the path Element uses) and verifies
    /// it against the release asset's advertised digest. A mismatch deletes the file
    /// and fails.
    /// </summary>
    public async Task<string> DownloadAsync(SelfUpdateInfo update, IProgress<double> progress, CancellationToken ct = default)
    {
        string dir = Path.GetTempPath();
        string exePath = Path.Combine(dir, update.FileName);
        try { if (File.Exists(exePath)) File.Delete(exePath); } catch { /* overwritten below */ }

        await DownloadFileAsync(update.FileUrl, exePath, update.FileSize, progress, ct);

        if (!DigestMatches(exePath, update.FileDigest))
        {
            try { File.Delete(exePath); } catch { /* best effort */ }
            throw new InvalidDataException("Download does not match the release checksum for " + update.FileName + ".");
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
