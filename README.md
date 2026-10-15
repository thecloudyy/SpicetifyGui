# SpicetifyGui

Spicetify Marketplace updater for Windows. Detects your Spicetify install
(installing the Spicetify CLI for you if it is missing), downloads the latest
Marketplace release, backs up the current install, applies it with
`spicetify apply`, and restarts Spotify if it was open.

## Download

Grab `SpicetifyGui-Setup-latest-win-x64.exe` from
[GitHub Releases](https://github.com/thecloudyy/SpicetifyGui/releases/latest)
and run it. It is a single file. The repo ships one always-live release
(tagged `latest`) that is overwritten on every build — there are no
versioned tags.

Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Features

- Auto-detects Spicetify and the installed Marketplace version (no folder picking).
- Installs the Spicetify CLI automatically when it is missing: official install
  script first (per-user, no admin), winget as fallback — both documented on
  [spicetify.app](https://spicetify.app/docs/getting-started).
- Installs the official `marketplace.zip` release asset (same source as the
  official Marketplace installer), falling back to the dist branch build.
- Install / Update / Uninstall with backup + restore on failure.
- File status card: per-file presence plus release freshness (up to date / update available).
- Self-update: the App card reinstalls the `latest` release
  (SHA-256 verified against the published `.sha256` asset) and restarts itself.
- The App card shows whether the build you are running is the latest one:
  a green ✓ latest, or an amber update-available badge — checked by hashing
  the running exe against the published `SpicetifyGui.exe.sha256` asset.
- Never leaves Spotify running unless it was open before the update.

## Build from source

```
dotnet build MarketplaceInstaller.slnx -c Release
```

## Release a new build

Everything ships to a single always-live release tagged `latest` — never
versioned tags. The app's Reinstall pulls that tag and verifies the download
against the published `.sha256` asset before running it.

1. Keep `<Version>` at `1.0.0` in both `.csproj` files (assembly versions must
   stay numeric; the visible `MarketplacePaths.Version` is `Latest`).
2. Create the release once:

```
gh release create latest --title "Latest" --notes "Latest SpicetifyGui build." --latest
```

3. Rebuild, publish the single file, compile the Inno installer, hash it, and
   overwrite the release assets:

   The release asset must be the **installer**, not the bare exe — the App card's
   Reinstall runs it with `/SILENT`, which a plain `SpicetifyGui.exe` would ignore.

```
dotnet publish MarketplaceInstaller.Gui/MarketplaceInstaller.Gui.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-single
ISCC.exe /DAppVersion=latest installer.iss
$hash = (Get-FileHash -Algorithm SHA256 dist\SpicetifyGui-Setup-latest-win-x64.exe).Hash.ToLower()
"$hash  SpicetifyGui-Setup-latest-win-x64.exe" | Out-File dist\SpicetifyGui-Setup-latest-win-x64.exe.sha256 -Encoding ascii
$appHash = (Get-FileHash -Algorithm SHA256 publish-single\SpicetifyGui.exe).Hash.ToLower()
"$appHash  SpicetifyGui.exe" | Out-File dist\SpicetifyGui.exe.sha256 -Encoding ascii
gh release upload latest --clobber dist\SpicetifyGui-Setup-latest-win-x64.exe dist\SpicetifyGui-Setup-latest-win-x64.exe.sha256 dist\SpicetifyGui.exe.sha256
```

   The app hashes its own running exe (byte-identical to `publish-single\SpicetifyGui.exe`
   after Inno extracts it) against `SpicetifyGui.exe.sha256` — keep it in sync
   with the installer on every upload.
