<#
.SYNOPSIS
  Schreibt fuer makensis auf, welche Dateien das Setup installiert - als NSIS-Befehle, mit denen die
  Deinstallation genau diese Dateien wieder entfernt.

.DESCRIPTION
  Lagerfeuer.nsi ruft dieses Skript beim Uebersetzen selbst auf (!system) und bindet das Ergebnis im
  Abschnitt der Deinstallation ein. Von Hand ist kein Aufruf noetig.

  Die Liste entsteht aus dem Ordner, den auch "File /r" einpackt. Kommt im Programm eine Datei dazu,
  kennt die Deinstallation sie damit von selbst.

  Bewusst ohne Umlaute geschrieben: makensis startet Windows PowerShell 5.1, und die liest Skripte
  ohne BOM nicht als UTF-8.

.PARAMETER Source
  Der Ordner mit den Programmdateien (SRCDIR von Lagerfeuer.nsi, Ausgabe von dotnet publish).

.PARAMETER Out
  Die zu schreibende Datei mit den NSIS-Befehlen.

.PARAMETER Manifest
  Wahlweise: dieselbe Liste als schlichter Text, ein Pfad je Zeile, Ordner mit "\" am Ende und die tiefsten
  zuerst. Die erste Zeile ist die Kennzeile aus -ManifestHeader: Nur eine Liste, die damit beginnt, befolgt das
  Setup spaeter. Das Setup legt die Liste in den Installationsordner. Ein spaeteres Setup liest sie dort und entfernt, was
  die alte Fassung mitgebracht hat - so bleibt nichts liegen, wenn eine Datei wegfaellt.

.PARAMETER Also
  Weitere Dateien fuer die Liste in -Manifest, die das Setup ausserhalb von "File /r" installiert.
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Manifest,
    [string]$ManifestHeader = '',
    [string[]]$Also = @()
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
$lines = New-Object System.Collections.Generic.List[string]

# In NSIS leitet $ eine Variable ein, ein $ im Dateinamen muss deshalb verdoppelt werden.
function ConvertTo-NsisPath([string]$FullName) {
    $FullName.Substring($root.Length + 1).Replace('$', '$$')
}

Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
    $lines.Add('Delete "$INSTDIR\' + (ConvertTo-NsisPath $_.FullName) + '"')
}

# Unterordner danach, die tiefsten zuerst. RMDir ohne /r entfernt einen Ordner nur, wenn er leer ist -
# liegt dort etwas Fremdes, bleibt er stehen.
Get-ChildItem -LiteralPath $root -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
    $lines.Add('RMDir "$INSTDIR\' + (ConvertTo-NsisPath $_.FullName) + '"')
}

if ($lines.Count -eq 0) { throw "Keine Dateien in $root - SRCDIR stimmt nicht." }

[System.IO.File]::WriteAllLines($Out, $lines, (New-Object System.Text.UTF8Encoding $false))

if ($Manifest) {
    $entries = New-Object System.Collections.Generic.List[string]
    Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
        $entries.Add($_.FullName.Substring($root.Length + 1))
    }
    foreach ($a in $Also) { $entries.Add($a) }
    Get-ChildItem -LiteralPath $root -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        $entries.Add($_.FullName.Substring($root.Length + 1) + '\')
    }
    # Das Setup liest die Liste spaeter als schlichten ASCII-Text und befolgt nur schlichte Pfade unterhalb des
    # Installationsordners. Was nicht dazu passt, soll hier auffallen und nicht erst als liegen gebliebene Datei.
    foreach ($e in $entries) {
        if ($e -match '[^\x20-\x7E]' -or $e -match '\.\.|[:*?/]') {
            throw "Der Pfad '$e' passt nicht in die Dateiliste: nur ASCII, kein '..' und keines der Zeichen : * ? /"
        }
    }
    if (-not $ManifestHeader) { throw '-Manifest braucht die Kennzeile aus -ManifestHeader.' }
    $entries.Insert(0, $ManifestHeader)
    [System.IO.File]::WriteAllLines($Manifest, $entries, [System.Text.Encoding]::ASCII)
}
