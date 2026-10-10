<#
.SYNOPSIS
    Erzeugt winget-Manifeste (Schema 1.10) für ein veröffentlichtes GitHub-Release von Lagerfeuer.
    URLs und SHA-256-Prüfsummen der Setups werden direkt aus dem Release gelesen.

.EXAMPLE
    ./build/New-WingetManifest.ps1 -Version 0.1.1
    winget validate artifacts/winget/0.1.1
    # Lokal testen (installiert wirklich):  winget install --manifest artifacts/winget/0.1.1

.NOTES
    Einreichen bei winget: Pull Request an github.com/microsoft/winget-pkgs mit dem Ordner
    manifests/c/Celestial0579/Lagerfeuer/<Version>/ – z.B. mit "wingetcreate submit".
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Repository = 'Celestial0579/lagerfeuer',
    [string] $OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\winget')
)

$ErrorActionPreference = 'Stop'
$id = 'Celestial0579.Lagerfeuer'
$schema = '1.10.0'
$release = gh api "repos/$Repository/releases/tags/v$Version" | ConvertFrom-Json

function Get-Installer([string] $arch) {
    $asset = $release.assets | Where-Object { $_.name -eq "Lagerfeuer-$Version-win-$arch-setup.exe" }
    if (-not $asset) { throw "Setup für $arch fehlt im Release v$Version." }
    if ($asset.digest -notmatch '^sha256:(?<hash>[0-9a-f]{64})$') { throw "Keine SHA-256-Prüfsumme für $($asset.name)." }
    [pscustomobject]@{ Arch = $arch; Url = $asset.browser_download_url; Sha256 = $Matches.hash.ToUpperInvariant() }
}

$installers = @(Get-Installer 'x64'; Get-Installer 'arm64')
$dir = Join-Path $OutputRoot $Version
New-Item -ItemType Directory -Force $dir | Out-Null
$releaseDate = ([datetime]$release.published_at).ToString('yyyy-MM-dd')

$installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: nullsoft
Scope: user
InstallModes:
  - interactive
  - silent
  - silentWithProgress
InstallerSwitches:
  Silent: /S
  SilentWithProgress: /S
UpgradeBehavior: install
Protocols:
  - audiobookshelf
ReleaseDate: $releaseDate
Installers:
$(($installers | ForEach-Object { "  - Architecture: $($_.Arch)`n    InstallerUrl: $($_.Url)`n    InstallerSha256: $($_.Sha256)" }) -join "`n")
ManifestType: installer
ManifestVersion: $schema
"@

$localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: de-DE
Publisher: Celestial0579
PublisherUrl: https://github.com/Celestial0579
PublisherSupportUrl: https://github.com/$Repository/issues
PackageName: Lagerfeuer
PackageUrl: https://github.com/$Repository
License: GPL-3.0
LicenseUrl: https://github.com/$Repository/blob/main/LICENSE
ShortDescription: Native Windows-App für Audiobookshelf – Hörbücher und Podcasts streamen, offline hören, Fortschritt synchronisieren.
Description: |-
  Lagerfeuer ist ein Windows-Client für selbst gehostete Audiobookshelf-Server.
  Streaming mit Cache, Downloads für die Offline-Wiedergabe, Fortschritts-Sync und Live-Sync mit anderen Geräten,
  Lesezeichen, Serien- und Autorenansichten, Podcasts, SSO (OpenID Connect), Taskleisten-Miniplayer
  und automatisches Pausieren bei Anrufen.
Moniker: lagerfeuer
Tags:
  - audiobookshelf
  - audiobooks
  - hoerbuch
  - podcast
  - player
ReleaseNotesUrl: $($release.html_url)
ManifestType: defaultLocale
ManifestVersion: $schema
"@

$versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: de-DE
ManifestType: version
ManifestVersion: $schema
"@

$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $dir "$id.installer.yaml"), $installerYaml, $utf8)
[IO.File]::WriteAllText((Join-Path $dir "$id.locale.de-DE.yaml"), $localeYaml, $utf8)
[IO.File]::WriteAllText((Join-Path $dir "$id.yaml"), $versionYaml, $utf8)
Write-Host "Manifeste erzeugt: $dir" -ForegroundColor Green
