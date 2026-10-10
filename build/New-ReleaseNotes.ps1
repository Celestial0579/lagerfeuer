<#
.SYNOPSIS
    Erzeugt Release-Notizen (Markdown) aus den Commits seit dem vorherigen Versions-Tag.
    Betreff → Überschrift, Aufzählungspunkte aus dem Commit-Text → Liste.

.EXAMPLE
    ./build/New-ReleaseNotes.ps1 -Tag v0.2.0 > notes.md
#>
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Repository = 'Celestial0579/lagerfeuer'
)

$ErrorActionPreference = 'Stop'
$previous = git describe --tags --abbrev=0 --match 'v*' "$Tag^" 2>$null
$range = if ($previous) { "$previous..$Tag" } else { $Tag }

# Datensätze durch ASCII 30 getrennt, Betreff und Text durch ASCII 31
$raw = git log $range --no-merges --reverse --format='%s%x1f%b%x1e'
$commits = ($raw -join "`n") -split [char]30 | Where-Object { $_.Trim() } | ForEach-Object {
    $subject, $body = $_.Trim() -split [char]31, 2
    [pscustomobject]@{ Subject = $subject.Trim(); Body = "$body" }
} | Where-Object { $_.Subject -notmatch '^Version \d' }

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('## Änderungen')
$lines.Add('')
foreach ($c in $commits) {
    $lines.Add("### $($c.Subject)")
    # Aufzählungen (inkl. eingerückter Folgezeilen) übernehmen, Trailer wie Co-Authored-By weglassen
    $bullets = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($c.Body -split "`r?`n")) {
        if ($line -match '^\s*[-*] (.+)$') { $bullets.Add("- $($Matches[1].Trim())") }
        elseif ($line -match '^\s{2,}(\S.*)$' -and $bullets.Count -gt 0) { $bullets[$bullets.Count - 1] += " $($Matches[1].Trim())" }
    }
    foreach ($b in $bullets) { $lines.Add($b) }
    $lines.Add('')
}

$version = $Tag.TrimStart('v')
$lines.Add('## Download')
$lines.Add('')
$lines.Add("- **Installer (empfohlen):** ``Lagerfeuer-$version-win-x64-setup.exe`` (ARM64: ``…-win-arm64-setup.exe``) – Installation pro Benutzer, ohne Adminrechte")
$lines.Add("- **Portable:** ``Lagerfeuer-$version-win-x64.exe`` bzw. ``…-win-arm64.exe``")
$lines.Add('- Bereits installierte Versionen bieten das Update selbst an.')
$lines.Add('')
$lines.Add('> Die Dateien sind (noch) nicht signiert – Windows SmartScreen warnt beim ersten Start („Weitere Informationen“ → „Trotzdem ausführen“).')
if ($previous) {
    $lines.Add('')
    $lines.Add("**Alle Änderungen:** https://github.com/$Repository/compare/$previous...$Tag")
}

$lines -join "`n"
