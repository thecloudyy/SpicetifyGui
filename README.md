# SpicetifyGui

Spicetify Marketplace updater for Windows. Detects your Spicetify install,
downloads the latest Marketplace release, backs up the current install,
applies it with `spicetify apply`, and restarts Spotify if it was open.

## Download

Grab `SpicetifyGui-Setup-vX.Y.Z-win-x64.exe` from
[GitHub Releases](https://github.com/thecloudyy/SpicetifyGui/releases/latest)
and run it. It is a single file.

Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Features

- Auto-detects Spicetify and the installed Marketplace version (no folder picking).
- Install / Update / Uninstall with backup + restore on failure.
- File status card: per-file presence plus release freshness (up to date / update available).
- Self-update: the App card reinstalls the latest
  `SpicetifyGui-Setup-*-win-x64.exe` release (SHA-256 verified) and restarts itself.
- Never leaves Spotify running unless it was open before the update.

## Build from source

```
dotnet build MarketplaceInstaller.slnx -c Release
```

## Release a new version

1. Keep `<Version>` at `1.0.0` in both `.csproj` files and `MarketplacePaths.Version`.
2. Rebuild, publish the single file, then compile the Inno installer and publish it:

   The release asset must be the **installer**, not the bare exe — the App card's
   Reinstall runs it with `/SILENT`, which a plain `SpicetifyGui.exe` would ignore.

```
dotnet publish MarketplaceInstaller.Gui/MarketplaceInstaller.Gui.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-single
ISCC.exe /DAppVersion=1.0.0 installer.iss
$hash = (Get-FileHash -Algorithm SHA256 dist\SpicetifyGui-Setup-v1.0.0-win-x64.exe).Hash.ToLower()
"$hash  SpicetifyGui-Setup-v1.0.0-win-x64.exe" | Out-File dist\SpicetifyGui-Setup-v1.0.0-win-x64.exe.sha256 -Encoding ascii
gh release create v1.0.0 --title "v1.0.0" --notes "..." dist\SpicetifyGui-Setup-v1.0.0-win-x64.exe dist\SpicetifyGui-Setup-v1.0.0-win-x64.exe.sha256
```

   Republishing the same tag (the app never bumps its own version):

```
gh release upload v1.0.0 --clobber dist\SpicetifyGui-Setup-v1.0.0-win-x64.exe dist\SpicetifyGui-Setup-v1.0.0-win-x64.exe.sha256
```
