<#
.SYNOPSIS
    Erzeugt Lagerfeuer als self-contained Single-File-EXE und optional einen NSIS-Installer.

.EXAMPLE
    ./build/publish.ps1                       # artifacts/Lagerfeuer-<ver>-win-x64.exe
    ./build/publish.ps1 -Installer            # zusätzlich artifacts/Lagerfeuer-<ver>-win-x64-setup.exe
    ./build/publish.ps1 -Platform x64,ARM64 -Installer
#>
param(
    [ValidateSet('x64', 'ARM64')]
    [string[]] $Platform = @('x64'),
    [switch] $Installer,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Lagerfeuer\Lagerfeuer.csproj'
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts 'staging'
$icon = Join-Path $root 'src\Lagerfeuer\Assets\AppIcon.ico'
$nsi = Join-Path $root 'installer\Lagerfeuer.nsi'

function Invoke-Publish([string] $platform, [string] $outDir, [string[]] $extra) {
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    & dotnet publish $project -c $Configuration "-p:Platform=$platform" -p:DebugType=none -o $outDir @extra
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($platform) fehlgeschlagen." }
}

$version = (& dotnet msbuild $project -nologo -getProperty:Version).Trim()
Write-Host "Lagerfeuer $version" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $artifacts | Out-Null

$makensis = $null
if ($Installer) {
    $makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
    if (-not $makensis) {
        $makensis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $makensis) { throw 'NSIS (makensis.exe) nicht gefunden. Installation: winget install NSIS.NSIS' }
}

foreach ($p in $Platform) {
    $rid = "win-$($p.ToLowerInvariant())"

    # 1) Eine einzige, portable EXE (entpackt sich beim ersten Start nach %TEMP%\.net)
    Write-Host "`n== Single-File-EXE ($rid) ==" -ForegroundColor Cyan
    $singleDir = Join-Path $staging "$rid-single"
    Invoke-Publish $p $singleDir @(
        '-p:PublishSingleFile=true',
        '-p:IncludeAllContentForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true'
    )
    $exe = Join-Path $artifacts "Lagerfeuer-$version-$rid.exe"
    Copy-Item (Join-Path $singleDir 'Lagerfeuer.exe') $exe -Force
    Write-Host "-> $exe" -ForegroundColor Green

    # 2) Optional: Installer aus regulärem (nicht single-file) Publish – startet schneller, kein Entpacken
    if ($Installer) {
        Write-Host "`n== NSIS-Installer ($rid) ==" -ForegroundColor Cyan
        $folderDir = Join-Path $staging $rid
        Invoke-Publish $p $folderDir @()
        $setup = Join-Path $artifacts "Lagerfeuer-$version-$rid-setup.exe"
        & $makensis /V2 /INPUTCHARSET UTF8 "/DVERSION=$version" "/DARCH=$p" "/DSRCDIR=$folderDir" "/DOUTFILE=$setup" "/DICON=$icon" $nsi
        if ($LASTEXITCODE -ne 0) { throw "makensis ($p) fehlgeschlagen." }
        Write-Host "-> $setup" -ForegroundColor Green
    }
}

Write-Host "`nFertig. Ausgabe:" -ForegroundColor Cyan
Get-ChildItem $artifacts -File -Filter 'Lagerfeuer-*' |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
