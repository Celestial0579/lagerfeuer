<#
.SYNOPSIS
    Stellt NSIS in genau einer Version bereit, geprüft über die Prüfsumme (#59).

.DESCRIPTION
    Für die Windows-Runner von GitHub (CI und Release). Bis 0.2.7 kam NSIS über "choco install nsis": jeweils die
    neueste Fassung aus dem Community-Repo, ohne Version und ohne Prüfsumme. Fiel das Repo aus, scheiterte die
    Setup-Prüfung erst später mit "makensis nicht gefunden".

    Jetzt: das Setup von NSIS von SourceForge, die Prüfsumme fest aus dem winget-Manifest (NSIS.NSIS 3.13).
    Stimmt sie nicht, bricht das Skript ab, bevor etwas startet. Danach muss "makensis /VERSION" genau die
    erwartete Version melden.

    Eine neue NSIS-Version zu übernehmen ist ein bewusster Schritt: Version, Adresse und Prüfsumme hier ändern.
#>
[CmdletBinding()]
param(
    [string]$Work = $env:RUNNER_TEMP
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Version = '3.13'
$Url = "https://sourceforge.net/projects/nsis/files/NSIS%203/$Version/nsis-$Version-setup.exe/download"
$Sha256 = 'DB9A1038BBC0E53FEF24F423FD609979BD578E942F1BE1198DB186D1C42652A3' # winget-pkgs manifests/n/NSIS/NSIS/3.13

$dir = Join-Path ${env:ProgramFiles(x86)} 'NSIS'
$makensis = Join-Path $dir 'makensis.exe'

function Get-NsisVersion {
    if (-not (Test-Path -LiteralPath $makensis)) { return $null }
    (& $makensis /VERSION | Out-String).Trim().TrimStart('v')
}

$found = Get-NsisVersion
if ($found -eq $Version) {
    Write-Host "NSIS $found liegt schon unter $dir."
}
else {
    if ($found) { Write-Host "NSIS $found gefunden, verlangt ist ${Version}: wird ersetzt." }
    if (-not $Work) { $Work = [System.IO.Path]::GetTempPath() }
    $setup = Join-Path $Work "nsis-$Version-setup.exe"
    # SourceForge leitet zu einem Spiegel weiter; mit dem Kennzeichen eines Download-Programms kommt die Datei
    # statt einer Warteseite. Was ankommt, prüft ohnehin die Prüfsumme.
    Invoke-WebRequest -Uri $Url -OutFile $setup -UserAgent 'Wget/1.21' -MaximumRetryCount 4 -RetryIntervalSec 15
    $actual = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) {
        Remove-Item -LiteralPath $setup -Force
        throw "Prüfsumme von nsis-$Version-setup.exe stimmt nicht: $actual statt $Sha256."
    }
    Write-Host "nsis-$Version-setup.exe geprüft (SHA256 $actual)."
    $p = Start-Process -FilePath $setup -ArgumentList '/S' -Wait -PassThru
    Remove-Item -LiteralPath $setup -Force
    if ($p.ExitCode -ne 0) { throw "Setup von NSIS endete mit $($p.ExitCode)." }
    $found = Get-NsisVersion
    if ($found -ne $Version) { throw "Nach der Installation meldet makensis '$found' statt $Version." }
}

if ($env:GITHUB_PATH) { Add-Content -LiteralPath $env:GITHUB_PATH -Value $dir }
Write-Host "NSIS $found"
exit 0
