using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace MarketplaceInstaller.Gui.Services;

internal sealed record SelfUpdateInfo(
    string Version,
    string Name,
    string Notes,
    string ZipName,
    string ZipUrl,
    long ZipSize,
    string ZipDigest);

/// <summary>
/// Self-update service for SpicetifyGui. Looks for releases in
/// thecloudyy/SpicetifyGui carrying a SpicetifyGui-win-x64.zip asset plus a
/// matching .sha256 file, verifies the download, then swaps it in over the
/// app folder and restarts.
/// </summary>
internal sealed class SelfUpdater
{
    public const string Owner = "thecloudyy";
    public const string Repo = "SpicetifyGui";
    public const string ExeName = "SpicetifyGui.exe";
    private const string AssetPrefix = "SpicetifyGui-";
    private const string AssetSuffix = "-win-x64.zip";

    private static readonly HttpClient _api = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient _dl = new() { Timeout = Timeout.InfiniteTimeSpan };

    static SelfUpdater()
    {
        _api.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpicetifyGui", "1.0"));
        _api.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _dl.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpicetifyGui", "1.0"));
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return Normalize(v ?? new Version(1, 0, 0));
        }
    }

    internal static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    internal static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(1, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string t = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(t, out var v))
        {
            version = Normalize(v);
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

        string zipName = "", zipUrl = "", zipDigest = "";
        long zipSize = 0;
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
            zipName = name;
            zipUrl = url;
            zipSize = size;
            zipDigest = digest;
        }
        if (zipUrl == "") return null;

        string relName = root.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";
        string notes = root.TryGetProperty("body", out var rb) ? rb.GetString() ?? "" : "";
        return new SelfUpdateInfo(version.ToString(), relName, notes,
            zipName, zipUrl, zipSize, zipDigest);
    }

    private static async Task<SelfUpdateInfo?> ScanReleasesAsync(bool newerOnly, CancellationToken ct)
    {
        for (int page = 1; page <= 3; page++)
        {
            using var res = await _api.GetAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=20&page={page}", ct);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            foreach (var root in doc.RootElement.EnumerateArray())
            {
                var info = InfoFromRelease(root);
                if (info == null) continue;
                if (newerOnly)
                {
                    if (Version.Parse(info.Version) <= CurrentVersion) return null;
                    return info;
                }
                return info;
            }
        }
        return null;
    }

    /// <summary>Newest release carrying the zip asset that is newer than this app.</summary>
    public Task<SelfUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default) =>
        ScanReleasesAsync(newerOnly: true, ct);

    /// <summary>Newest release carrying the zip asset, even if already installed (reinstall).</summary>
    public Task<SelfUpdateInfo?> GetLatestWithAssetAsync(CancellationToken ct = default) =>
        ScanReleasesAsync(newerOnly: false, ct);

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

    private static string ParseChecksum(string text)
    {
        foreach (string rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string token = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0].Trim('*', ' ', '\t');
            if (token.Length == 64)
            {
                bool hex = true;
                foreach (char c in token)
                {
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) { hex = false; break; }
                }
                if (hex) return token.ToLowerInvariant();
            }
        }
        throw new InvalidDataException("No SHA-256 hash found in checksum file.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public async Task<string> DownloadAsync(SelfUpdateInfo update, IProgress<double> progress, CancellationToken ct = default)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpicetifyGui", "updates");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(dir)) File.Delete(f);

        string zipPath = Path.Combine(dir, update.ZipName);
        await DownloadFileAsync(update.ZipUrl, zipPath, update.ZipSize, progress, ct);

        using var res = await _dl.GetAsync(update.ZipUrl + ".sha256", ct);
        res.EnsureSuccessStatusCode();
        string setupSum = ParseChecksum(await res.Content.ReadAsStringAsync(ct));
        if (!string.Equals(Sha256File(zipPath), setupSum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download check failed for " + update.ZipName + ". Deleted nothing.");
        if (update.ZipDigest != "" && !string.Equals(Sha256File(zipPath), update.ZipDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download does not match the release checksum for " + update.ZipName + ".");
        progress?.Report(1.0);
        return dir;
    }

    public static void InstallAndRestart(string dir, string zipName)
    {
        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        int pid = Environment.ProcessId;
        string script = Path.Combine(dir, "update.cmd");

        string bat =
            "@echo off\r\n" +
            $"set \"UPD={dir}\"\r\n" +
            $"set \"APPDIR={appDir}\"\r\n" +
            $"set \"ZIP={zipName}\"\r\n" +
            ":wait\r\ntasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL\r\n" +
            "if %errorlevel%==0 ( timeout /t 1 /nobreak >NUL & goto wait )\r\n" +
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -Path '%UPD%\\%ZIP%' -DestinationPath '%APPDIR%' -Force\"\r\n" +
            $"start \"\" \"%APPDIR%\\{ExeName}\"\r\n" +
            "rd /s /q \"%UPD%\"\r\n" +
            "(goto) 2>nul & del \"%~f0\"\r\n";
        File.WriteAllText(script, bat);

        Process.Start(new ProcessStartInfo
        {
            FileName = script,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = appDir,
        });
    }
}
