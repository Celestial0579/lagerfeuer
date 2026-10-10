<#
.SYNOPSIS
    Erzeugt Lagerfeuer als self-contained Single-File-EXE und optional einen NSIS-Installer.
    Optional werden alle ausführbaren Dateien mit einem Code-Signing-Zertifikat signiert.

.EXAMPLE
    ./build/publish.ps1                       # artifacts/Lagerfeuer-<ver>-win-x64.exe
    ./build/publish.ps1 -Installer            # zusätzlich artifacts/Lagerfeuer-<ver>-win-x64-setup.exe
    ./build/publish.ps1 -Platform x64,ARM64 -Installer

.EXAMPLE
    # Signieren mit Zertifikat aus dem Windows-Zertifikatsspeicher (z.B. Hardware-Token):
    ./build/publish.ps1 -Installer -CertificateThumbprint 0123ABCD...

    # Signieren mit PFX-Datei (Kennwort aus Umgebungsvariable LAGERFEUER_SIGN_PASSWORD):
    ./build/publish.ps1 -Installer -CertificatePath C:\certs\lagerfeuer.pfx

.NOTES
    In GitHub Actions: Secrets SIGNING_CERT_PFX_BASE64 und SIGNING_CERT_PASSWORD hinterlegen –
    der Release-Workflow signiert dann automatisch (siehe .github/workflows/release.yml).
#>
param(
    [ValidateSet('x64', 'ARM64')]
    [string[]] $Platform = @('x64'),
    [switch] $Installer,
    [string] $Configuration = 'Release',
    [string] $CertificateThumbprint = $env:LAGERFEUER_SIGN_THUMBPRINT,
    [string] $CertificatePath = $env:LAGERFEUER_SIGN_PFX,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
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

# ---------- Signieren (optional) ----------

$signArgs = $null
if ($CertificateThumbprint -or $CertificatePath) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe nicht gefunden (Windows SDK installieren).' }

    $signArgs = @('sign', '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256', '/d', 'Lagerfeuer')
    if ($CertificateThumbprint) {
        $signArgs += @('/sha1', $CertificateThumbprint)
    }
    else {
        $signArgs += @('/f', $CertificatePath)
        if ($env:LAGERFEUER_SIGN_PASSWORD) { $signArgs += @('/p', $env:LAGERFEUER_SIGN_PASSWORD) }
    }
    Write-Host 'Code-Signing aktiv.' -ForegroundColor Cyan
}

function Invoke-Sign([string[]] $files) {
    if (-not $signArgs) { return }
    # Virenscanner prüfen frisch gebaute EXE-Dateien teils in einer Sandbox und sperren sie solange –
    # dann scheitert schon der Start von signtool. In dem Fall mehrfach mit Pause erneut versuchen.
    for ($attempt = 1; ; $attempt++) {
        try {
            & $signtool @signArgs @files | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Signieren fehlgeschlagen: $($files -join ', ')" }
            return
        }
        catch [System.Management.Automation.ApplicationFailedException] {
            if ($attempt -ge 10) { throw }
            Write-Host "Datei gesperrt (Virenscanner?) – neuer Versuch in 15 s ($attempt/10) …" -ForegroundColor Yellow
            Start-Sleep -Seconds 15
        }
    }
}

# Befehl für NSIS (!finalize / !uninstfinalize); %1 ist die zu signierende Datei.
function Get-NsisSignCommand {
    if (-not $signArgs) { return $null }
    $quoted = $signArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }
    return "`"$signtool`" $($quoted -join ' ') `"%1`""
}

# ---------- Build ----------

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
    Invoke-Sign @($exe)
    Write-Host "-> $exe" -ForegroundColor Green

    # 2) Optional: Installer aus regulärem (nicht single-file) Publish – startet schneller, kein Entpacken
    if ($Installer) {
        Write-Host "`n== NSIS-Installer ($rid) ==" -ForegroundColor Cyan
        $folderDir = Join-Path $staging $rid
        Invoke-Publish $p $folderDir @()
        Invoke-Sign @((Join-Path $folderDir 'Lagerfeuer.exe'))
        $setup = Join-Path $artifacts "Lagerfeuer-$version-$rid-setup.exe"
        $nsisArgs = @('/V2', '/INPUTCHARSET', 'UTF8', "/DVERSION=$version", "/DARCH=$p", "/DSRCDIR=$folderDir", "/DOUTFILE=$setup", "/DICON=$icon")
        if ($cmd = Get-NsisSignCommand) { $nsisArgs += "/DSIGNCMD=$cmd" }
        & $makensis @nsisArgs $nsi
        if ($LASTEXITCODE -ne 0) { throw "makensis ($p) fehlgeschlagen." }
        Write-Host "-> $setup" -ForegroundColor Green
    }
}

Write-Host "`nFertig. Ausgabe:" -ForegroundColor Cyan
Get-ChildItem $artifacts -File -Filter 'Lagerfeuer-*' |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
