<#
.SYNOPSIS
    Prüft das Setup von Lagerfeuer auf einem Windows-Rechner. In der CI läuft es als Job „Setup-Prüfung“.

.DESCRIPTION
    Baut aus installer/Lagerfeuer.nsi ein Probe-Setup mit einer winzigen Nutzlast: Statt der App liegt darin
    ein Programm, das nur schläft. Geprüft wird damit das Skript des Setups, nicht die App.

      1  Fremde Programme neben dem Setup, die wie Hilfsprogramme von Windows heißen (cmd.exe, tasklist.exe,
         find.exe und andere), werden nicht ausgeführt: bei der Installation, wenn Lagerfeuer gerade läuft,
         und bei der Deinstallation. Als Gegenprobe startet das veröffentlichte Setup sie noch.
      2  Läuft Lagerfeuer, brechen Setup und Deinstallation ab, bevor sie eine Datei anfassen.
      3  Im Quelltext des Setups nennt jeder Aufruf eines Programms dieses mit vollem Pfad.
      4  Die App startet kein Programm nur beim Namen: Gegenprobe mit dem früheren Aufruf von explorer.exe,
         dazu der Quelltext.

    Die Köder sind harmlose Programme. Sie schreiben nur auf, dass sie gestartet wurden.

.PARAMETER OldVersion
    Veröffentlichte Fassung für die Gegenprobe. Ihr Setup wird von GitHub geladen (gh, GH_TOKEN) und gegen
    die Prüfsumme des Release geprüft.

.PARAMETER SkipOld
    Ohne die Gegenprobe mit dem veröffentlichten Setup, zum Beispiel ohne Netz.

.EXAMPLE
    ./tests/installer/Test-Installer.ps1
#>
param(
    [string]$OldVersion = '0.2.1',
    [string]$Work = (Join-Path ([IO.Path]::GetTempPath()) "lagerfeuer-setup-pruefung-$PID"),
    [switch]$SkipOld
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$nsi = Join-Path $root 'installer\Lagerfeuer.nsi'
$icon = Join-Path $root 'src\Lagerfeuer\Assets\AppIcon.ico'
$repo = 'Celestial0579/lagerfeuer'
$regKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lagerfeuer'
$failed = [System.Collections.Generic.List[string]]::new()

function Step([string]$Text) { Write-Host "`n== $Text ==" -ForegroundColor Cyan }

function Check([bool]$Condition, [string]$Text) {
    if ($Condition) { Write-Host "   ok   $Text" -ForegroundColor Green }
    else { Write-Host "   FEHLT $Text" -ForegroundColor Red; $script:failed.Add($Text) }
}

# Startet ein Programm aus einem bestimmten Ordner heraus und wartet auf sein Ende. Beim Doppelklick ist der
# aktuelle Ordner der Ordner der Datei; genau das stellt -WorkingDirectory nach.
function Invoke-Program([string]$Exe, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSec = 300) {
    Write-Host "   >    $(Split-Path $Exe -Leaf) $($Arguments -join ' ')   (aus $WorkingDirectory)"
    $start = @{ FilePath = $Exe; PassThru = $true; WorkingDirectory = $WorkingDirectory }
    if ($Arguments) { $start.ArgumentList = $Arguments }
    $p = Start-Process @start
    $null = $p.Handle   # sonst ist ExitCode nach dem Ende leer
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        throw "$(Split-Path $Exe -Leaf) war nach $TimeoutSec Sekunden nicht fertig"
    }
    $p.ExitCode
}

function Wait-Gone([string]$Path) {
    $deadline = (Get-Date).AddSeconds(90)
    while ((Test-Path -LiteralPath $Path) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}

# ---------------------------------------------------------------- Vorbereitung

Step 'Vorbereitung'
if (-not $IsWindows) { throw 'Diese Prüfung läuft nur unter Windows.' }
New-Item -ItemType Directory -Force $Work | Out-Null
if ($Work -match '\s') { throw "Der Arbeitsordner darf keine Leerzeichen enthalten (wegen /D= des Setups): $Work" }

$makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (-not $makensis) {
    $makensis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $makensis) { throw 'NSIS (makensis.exe) nicht gefunden. Installation: winget install NSIS.NSIS' }
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw 'csc.exe von .NET Framework 4 fehlt, die Hilfsprogramme lassen sich nicht bauen' }
Write-Host "   NSIS: $makensis"

# Köder: schreibt auf, unter welchem Namen und mit welchen Argumenten er gestartet wurde.
# Schläfer: steht im Probe-Setup an der Stelle der App und läuft einfach weiter.
# Aufruf: startet den Explorer so, wie es die App bis 0.2.1 tat (nur beim Namen, über die Shell).
$baitLog = Join-Path $Work 'koeder-gelaufen.txt'
$baitExe = Join-Path $Work 'koeder.exe'
$sleeperExe = Join-Path $Work 'schlaefer.exe'
$callExe = Join-Path $Work 'aufruf.exe'
Set-Content -LiteralPath (Join-Path $Work 'koeder.cs') -Encoding ascii -Value @'
using System;
using System.Diagnostics;
using System.IO;
public static class Koeder
{
    public static int Main(string[] args)
    {
        try
        {
            File.AppendAllText(Environment.GetEnvironmentVariable("LAGERFEUER_KOEDER"),
                Process.GetCurrentProcess().MainModule.FileName + " " + string.Join(" ", args) + Environment.NewLine);
        }
        catch { }
        return 1;
    }
}
'@
Set-Content -LiteralPath (Join-Path $Work 'schlaefer.cs') -Encoding ascii -Value @'
public static class Schlaefer
{
    public static void Main() { System.Threading.Thread.Sleep(600000); }
}
'@
Set-Content -LiteralPath (Join-Path $Work 'aufruf.cs') -Encoding ascii -Value @'
using System;
using System.Diagnostics;
public static class Aufruf
{
    public static int Main(string[] args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("explorer.exe", args[0]) { UseShellExecute = true });
            if (p != null) p.WaitForExit(15000);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }
    }
}
'@
foreach ($name in 'koeder', 'schlaefer', 'aufruf') {
    & $csc /nologo "/out:$(Join-Path $Work "$name.exe")" (Join-Path $Work "$name.cs") | Out-Null
    if ($LASTEXITCODE) { throw "$name.cs ließ sich nicht übersetzen" }
}

# Probe-Setup aus dem echten Skript. Die zweite Fassung bringt eine Datei mehr mit, die im Ordner vor
# Lagerfeuer.exe einsortiert wird: Daran zeigt sich, ob das Setup abbricht, bevor es etwas kopiert.
function New-ProbeSetup([string]$Version, [string[]]$ExtraFiles) {
    $payload = Join-Path $Work "nutzlast-$Version"
    New-Item -ItemType Directory -Force $payload | Out-Null
    Copy-Item -LiteralPath $sleeperExe -Destination (Join-Path $payload 'Lagerfeuer.exe')
    Set-Content -LiteralPath (Join-Path $payload 'beigabe.txt') -Value "Probe $Version"
    foreach ($f in $ExtraFiles) { Set-Content -LiteralPath (Join-Path $payload $f) -Value "Probe $Version" }
    $out = Join-Path $Work "Lagerfeuer-$Version-win-x64-setup.exe"
    & $makensis /V2 /INPUTCHARSET UTF8 "/DVERSION=$Version" /DARCH=x64 "/DSRCDIR=$payload" "/DOUTFILE=$out" "/DICON=$icon" $nsi | Out-Null
    if ($LASTEXITCODE) { throw "makensis ist für das Probe-Setup $Version gescheitert" }
    $out
}
$setup = New-ProbeSetup '0.0.1' @()
$setup2 = New-ProbeSetup '0.0.2' @('Aaa-neu.txt')
Check (Test-Path $setup) 'Das Probe-Setup ist gebaut'

# Ordner mit einer Kopie des Setups und den Ködern daneben; liefert den Pfad der Kopie.
# .com-Köder gehören dazu, weil cmd ein Programm ohne Endung zuerst als .com sucht.
$baitNames = 'cmd.exe', 'tasklist.exe', 'tasklist.com', 'find.exe', 'find.com', 'taskkill.exe', 'explorer.exe'
function New-BaitFolder([string]$Name, [string[]]$Files) {
    $folder = Join-Path $Work $Name
    New-Item -ItemType Directory -Force $folder | Out-Null
    foreach ($f in $Files) { Copy-Item -LiteralPath $f -Destination $folder }
    foreach ($n in $baitNames) { Copy-Item -LiteralPath $baitExe -Destination (Join-Path $folder $n) }
    $folder
}

function Show-BaitLog {
    if (Test-Path -LiteralPath $baitLog) { Get-Content -LiteralPath $baitLog | ForEach-Object { Write-Host "        Köder: $_" } }
}
function Test-BaitRan { Test-Path -LiteralPath $baitLog }
function Reset-BaitLog { Remove-Item -LiteralPath $baitLog -ErrorAction SilentlyContinue }

$env:LAGERFEUER_KOEDER = $baitLog
$sleeper = $null
try {
    $null = Invoke-Program $baitExe @('Selbsttest') $Work
    Check ((Test-BaitRan) -and (Get-Content -LiteralPath $baitLog -Raw) -like '*Selbsttest*') 'Der Köder meldet sich, wenn er gestartet wird'
    Reset-BaitLog

    # ------------------------------------------------------------ 1a Gegenprobe: veröffentlichtes Setup

    if ($SkipOld) {
        Step "1a Gegenprobe mit dem veröffentlichten Setup ${OldVersion}: übersprungen (-SkipOld)"
    }
    else {
        Step "1a Gegenprobe: Das veröffentlichte Setup $OldVersion startet die Köder noch"
        # Schlägt der Köder hier nicht an, taugt die ganze Stufe 1 nicht als Prüfung.
        $oldName = "Lagerfeuer-$OldVersion-win-x64-setup.exe"
        $oldDir = Join-Path $Work 'veroeffentlicht'
        New-Item -ItemType Directory -Force $oldDir | Out-Null
        & gh release download "v$OldVersion" --repo $repo --pattern $oldName --dir $oldDir --clobber
        if ($LASTEXITCODE) { throw "Das Setup $OldVersion ließ sich nicht laden" }
        $oldSetup = Join-Path $oldDir $oldName
        $release = & gh api "repos/$repo/releases/tags/v$OldVersion" | ConvertFrom-Json
        $digest = ($release.assets | Where-Object name -eq $oldName).digest
        $hash = 'sha256:' + (Get-FileHash -LiteralPath $oldSetup -Algorithm SHA256).Hash.ToLowerInvariant()
        Check ($digest -eq $hash) "Das geladene Setup $OldVersion passt zur Prüfsumme des Release"
        if ($digest -ne $hash) { throw 'Das geladene Setup wird nicht gestartet' }

        $oldBait = New-BaitFolder 'koeder-alt' @($oldSetup)
        $oldTarget = Join-Path $Work 'ziel-alt'
        $code = Invoke-Program (Join-Path $oldBait $oldName) @('/S', "/D=$oldTarget") $oldBait
        Check ($code -eq 0) "Installation: Exitcode 0 (war $code)"
        Show-BaitLog
        Check (Test-BaitRan) "Gegenprobe: Das Setup $OldVersion startet bei der Installation einen Köder"
        Reset-BaitLog

        # Die Deinstallation erbt den aktuellen Ordner dessen, der sie aufruft. Ob der Köder dabei anschlägt,
        # wird nur vermerkt: Die Gegenprobe, auf die es ankommt, ist die Installation oben.
        $null = Invoke-Program (Join-Path $oldTarget 'uninstall.exe') @('/S') $oldBait
        Wait-Gone $oldTarget
        Start-Sleep -Seconds 2   # die Registry-Einträge löscht sie nach dem Ordner
        Show-BaitLog
        Write-Host "        Deinstallation von ${OldVersion}: Köder gestartet = $(Test-BaitRan)"
        Check (-not (Test-Path $oldTarget)) "Das Setup $OldVersion ist wieder deinstalliert"
        Reset-BaitLog
    }

    # ------------------------------------------------------------ 1b Dieses Setup, frische Installation

    Step '1b Dieses Setup startet keinen Köder: frische Installation'
    $bait = New-BaitFolder 'koeder-neu' @($setup, $setup2)
    $target = Join-Path $Work 'ziel-neu'
    $code = Invoke-Program (Join-Path $bait (Split-Path $setup -Leaf)) @('/S', "/D=$target") $bait
    Check ($code -eq 0) "Exitcode 0 (war $code)"
    Check (Test-Path (Join-Path $target 'Lagerfeuer.exe')) 'Die Installation ist vollständig'
    Check (Test-Path $regKey) 'Der Eintrag unter „Apps & Features“ ist angelegt'
    Show-BaitLog
    Check (-not (Test-BaitRan)) 'Das Setup hat keinen Köder gestartet'
    Reset-BaitLog

    # ------------------------------------------------------------ 1c und 2 Lagerfeuer läuft

    Step '1c/2 Lagerfeuer läuft: Setup und Deinstallation brechen ab, ohne etwas anzufassen und ohne Köder'
    $sleeper = Start-Process -FilePath (Join-Path $target 'Lagerfeuer.exe') -PassThru -WindowStyle Hidden
    $null = $sleeper.Handle
    Start-Sleep -Seconds 1
    Check (-not $sleeper.HasExited) 'Im Zielordner läuft ein Programm namens Lagerfeuer.exe'

    $code = Invoke-Program (Join-Path $bait (Split-Path $setup2 -Leaf)) @('/S', "/D=$target") $bait
    Check ($code -ne 0) "Das Setup bricht ab (Exitcode $code)"
    Check (-not (Test-Path (Join-Path $target 'Aaa-neu.txt'))) 'Es hat vorher keine Datei kopiert'
    Check (-not $sleeper.HasExited) 'Das laufende Programm läuft weiter'
    Show-BaitLog
    Check (-not (Test-BaitRan)) 'Dabei hat das Setup keinen Köder gestartet'
    Reset-BaitLog

    # Mit _?= läuft die Deinstallation an Ort und Stelle und meldet ihren Exitcode.
    $code = Invoke-Program (Join-Path $target 'uninstall.exe') @('/S', "_?=$target") $bait
    Check ($code -ne 0) "Die Deinstallation bricht ab (Exitcode $code)"
    Check (Test-Path (Join-Path $target 'beigabe.txt')) 'Sie hat vorher keine Datei gelöscht'
    Check (Test-Path $regKey) 'Der Eintrag unter „Apps & Features“ steht noch'
    Show-BaitLog
    Check (-not (Test-BaitRan)) 'Dabei hat die Deinstallation keinen Köder gestartet'
    Reset-BaitLog

    Stop-Process -Id $sleeper.Id -Force
    $null = $sleeper.WaitForExit(5000)

    # ------------------------------------------------------------ 1d Update und Deinstallation

    Step '1d Lagerfeuer läuft nicht mehr: Update und Deinstallation gelingen, ohne Köder'
    $code = Invoke-Program (Join-Path $bait (Split-Path $setup2 -Leaf)) @('/S', "/D=$target") $bait
    Check ($code -eq 0) "Update: Exitcode 0 (war $code)"
    Check (Test-Path (Join-Path $target 'Aaa-neu.txt')) 'Das Update hat die neue Datei gebracht'

    $null = Invoke-Program (Join-Path $target 'uninstall.exe') @('/S') $bait
    Wait-Gone $target
    Check (-not (Test-Path $target)) 'Die Deinstallation entfernt den Ordner'
    Check (-not (Test-Path $regKey)) 'Der Eintrag unter „Apps & Features“ ist weg'
    Show-BaitLog
    Check (-not (Test-BaitRan)) 'Weder Update noch Deinstallation haben einen Köder gestartet'
    Reset-BaitLog

    # ------------------------------------------------------------ 4 Die App: Explorer nur beim Namen

    Step '4 App: Gegenprobe mit dem früheren Aufruf von explorer.exe'
    # Bis 0.2.1 öffnete „Ordner öffnen“ in den Einstellungen den Explorer nur beim Namen über die Shell. Die
    # Shell sucht ein solches Programm zuerst im aktuellen Ordner; bei der portablen EXE ist das der Ordner,
    # aus dem sie gestartet wurde.
    $appBait = New-BaitFolder 'koeder-app' @()
    $code = Invoke-Program $callExe @($Work) $appBait 60
    Show-BaitLog
    Check ((Test-BaitRan) -and (Get-Content -LiteralPath $baitLog -Raw) -like '*explorer.exe*') 'Gegenprobe: Der frühere Aufruf startet den Köder namens explorer.exe'
    Reset-BaitLog
}
finally {
    Remove-Item Env:LAGERFEUER_KOEDER -ErrorAction SilentlyContinue
    if ($sleeper -and -not $sleeper.HasExited) { Stop-Process -Id $sleeper.Id -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- 3 Quelltext des Setups

Step '3 Quelltext des Setups: Jeder Aufruf eines Programms nennt es mit vollem Pfad'
$calls = @(Get-ChildItem -LiteralPath (Join-Path $root 'installer') -Recurse -File -Include '*.nsi', '*.nsh' |
    Select-String -Pattern '^\s*(nsExec::\w+|Exec|ExecWait|ExecShell|ExecShellWait)\s+(.+)$')
Check ($calls.Count -ge 2) "Aufrufe von Programmen im Quelltext des Setups gefunden: $($calls.Count)"
$bare = @($calls | Where-Object { $_.Matches[0].Groups[2].Value -notmatch '^[''"`]"?\$(SYSDIR|WINDIR)\\' })
foreach ($b in $bare) { Write-Host "        $($b.Filename):$($b.LineNumber): $($b.Line.Trim())" }
Check ($bare.Count -eq 0) 'Jeder davon beginnt mit $SYSDIR oder $WINDIR'

# ---------------------------------------------------------------- 4 Quelltext der App

Step '4 Quelltext der App: Kein Programm wird nur beim Namen gestartet'
# Ein Zeichenketten-Literal als Programm in ProcessStartInfo oder Process.Start wäre ein bloßer Name
# (oder ein fest verdrahteter Pfad, den es hier auch nicht geben soll).
$literal = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.cs' |
    Select-String -Pattern '(new\s+ProcessStartInfo|Process\.Start)\s*\(\s*\$?@?"')
foreach ($l in $literal) { Write-Host "        $($l.Path | Split-Path -Leaf):$($l.LineNumber): $($l.Line.Trim())" }
Check ($literal.Count -eq 0) 'Kein Aufruf nennt das Programm als festen Text'

# ---------------------------------------------------------------- Ergebnis

Write-Host ''
if ($failed.Count) {
    Write-Host "$($failed.Count) Prüfung(en) nicht bestanden:" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'Alle Prüfungen bestanden.' -ForegroundColor Green
