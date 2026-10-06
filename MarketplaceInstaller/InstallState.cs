using System.Text.Json;

namespace MarketplaceInstaller;

/// <summary>
/// Remembers which release tag was last installed, because the Marketplace
/// manifest.json carries no version field. Stored outside the Marketplace
/// folder so updates (which delete that folder) don't wipe it.
/// </summary>
public static class InstallState
{
    public static string? GetRecordedTag()
    {
        try
        {
            if (!File.Exists(MarketplacePaths.StateFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(MarketplacePaths.StateFile));
            if (doc.RootElement.TryGetProperty("installedTag", out var t))
            {
                string? s = t.GetString()?.Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static void RecordTag(string tag)
    {
        try
        {
            Directory.CreateDirectory(MarketplacePaths.StateDir);
            File.WriteAllText(MarketplacePaths.StateFile,
                JsonSerializer.Serialize(new { installedTag = tag }));
        }
        catch { }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(MarketplacePaths.StateFile))
                File.Delete(MarketplacePaths.StateFile);
        }
        catch { }
    }
}
