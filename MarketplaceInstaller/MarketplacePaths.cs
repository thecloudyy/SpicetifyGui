namespace MarketplaceInstaller;

public static class MarketplacePaths
{
    public const string Version = "1.0.0";
    public const string UserAgent = "spicetify-marketplace-updater/1.0.0";

    public const string Repo = "spicetify/spicetify-marketplace";
    public const string ReleasesRepo = "spicetify/marketplace";
    public const string SpicetifyCliRepo = "spicetify/cli";
    public const string Branch = "dist";
    public const string AppName = "marketplace";

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "spicetify");

    public static string CustomApps => Path.Combine(ConfigPath, "CustomApps");
    public static string Marketplace => Path.Combine(CustomApps, AppName);
    public static string ConfigIni => Path.Combine(ConfigPath, "config-xpui.ini");

    public static string DistZipUrl =>
        $"https://github.com/{Repo}/archive/refs/heads/{Branch}.zip";

    /// <summary>Official release asset for a tag, e.g. v1.0.11 — what the official
    /// Marketplace install script downloads.</summary>
    public static string ReleaseZipUrl(string tag) =>
        $"https://github.com/{ReleasesRepo}/releases/download/v{tag.TrimStart('v', 'V')}/marketplace.zip";

    /// <summary>Same asset via the stable latest-download alias (no tag needed).</summary>
    public static string LatestReleaseZipUrl =>
        $"https://github.com/{ReleasesRepo}/releases/latest/download/marketplace.zip";

    public static string LatestReleaseApi =>
        $"https://api.github.com/repos/{ReleasesRepo}/releases/latest";

    public static string LatestSpicetifyCliApi =>
        $"https://api.github.com/repos/{SpicetifyCliRepo}/releases/latest";

    public static string StateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarketplaceInstaller");

    public static string StateFile => Path.Combine(StateDir, "config.json");
}
