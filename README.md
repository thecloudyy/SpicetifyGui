# SpicetifyGui

Spicetify Marketplace updater for Windows. Detects your Spicetify install,
downloads the latest Marketplace release, backs up the current install,
applies it with `spicetify apply`, and restarts Spotify if it was open.

## Download

Grab `SpicetifyGui-win-x64.zip` from
[GitHub Releases](https://github.com/thecloudyy/SpicetifyGui/releases/latest),
extract it anywhere, and run `SpicetifyGui.exe`.

Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Features

- Auto-detects Spicetify and the installed Marketplace version (no folder picking).
- Install / Update / Uninstall with backup + restore on failure.
- File status card: per-file presence plus release freshness (up to date / update available).
- Self-update: the App card reinstalls the latest `SpicetifyGui-win-x64.zip`
  release (SHA-256 verified) and restarts itself.
- Never leaves Spotify running unless it was open before the update.

## Build from source

```
dotnet build MarketplaceInstaller.slnx -c Release
```

## Release a new version

1. Bump `<Version>` in both `.csproj` files and `MarketplacePaths.Version`.
2. Rebuild Release, then package + publish:

```
$out = "MarketplaceInstaller.Gui/bin/Release/net10.0-windows"
Compress-Archive -Path "$out/*" -DestinationPath SpicetifyGui-win-x64.zip -Force
$hash = (Get-FileHash -Algorithm SHA256 SpicetifyGui-win-x64.zip).Hash.ToLower()
"$hash  SpicetifyGui-win-x64.zip" | Out-File SpicetifyGui-win-x64.zip.sha256 -Encoding ascii
gh release create vX.Y.Z --title "vX.Y.Z" --notes "..." SpicetifyGui-win-x64.zip SpicetifyGui-win-x64.zip.sha256
```
