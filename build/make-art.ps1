<#
.SYNOPSIS
    Erzeugt die Grafiken von Lagerfeuer im Farbschema von firestrike.de:
      src/Lagerfeuer/Assets/AppIcon.ico + AppIcon.png   (App-Icon)
      installer/art/welcome.bmp                         (Seitengrafik Willkommen/Fertig, 2x für hohe DPI)
      installer/art/header.bmp                          (Kopfgrafik der übrigen Installer-Seiten, 2x)
.NOTES
    Nur unter Windows (System.Drawing). Ergebnis wird eingecheckt; nur bei Designänderungen neu ausführen.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent

function C([string] $hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

$Glut = C '#AB5019'; $GlutHell = C '#D9713C'; $Funke = C '#E8A33D'; $FunkeHell = C '#F2C46B'
$Stahl = C '#33414B'; $StahlTief = C '#212B33'; $StahlHell = C '#B4C6D1'; $Grund = C '#141715'

# Lagerfeuer (Glühen, Scheite, Flammen) im 256er-Koordinatensystem; Transform vorher setzen.
function Draw-Fire([System.Drawing.Graphics] $g) {
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath; $glow.AddEllipse(40, 70, 176, 176)
    $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
    $pg.CenterColor = [System.Drawing.Color]::FromArgb(110, $GlutHell); $pg.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $GlutHell))
    $g.FillPath($pg, $glow)
    foreach ($a in -16, 16) {
        $st = $g.Save(); $g.TranslateTransform(128, 206); $g.RotateTransform($a)
        $log = New-Object System.Drawing.Drawing2D.GraphicsPath; $lw = 150; $lh = 22
        $log.AddArc(-$lw / 2, -$lh / 2, $lh, $lh, 90, 180); $log.AddArc($lw / 2 - $lh, -$lh / 2, $lh, $lh, 270, 180); $log.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush $StahlHell), $log); $g.Restore($st)
    }
    $f = New-Object System.Drawing.Drawing2D.GraphicsPath
    $f.AddBezier(128, 30, 152, 70, 192, 96, 188, 144); $f.AddBezier(188, 144, 185, 182, 160, 200, 128, 200)
    $f.AddBezier(128, 200, 96, 200, 71, 182, 68, 144); $f.AddBezier(68, 144, 66, 114, 86, 98, 98, 76)
    $f.AddBezier(98, 76, 101, 96, 108, 106, 116, 108); $f.AddBezier(116, 108, 110, 84, 114, 56, 128, 30); $f.CloseFigure()
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 30)), ([System.Drawing.PointF]::new(0, 200)), $GlutHell, $Glut), $f)
    $i = New-Object System.Drawing.Drawing2D.GraphicsPath
    $i.AddBezier(128, 96, 142, 120, 162, 136, 160, 160); $i.AddBezier(160, 160, 158, 182, 144, 194, 128, 194)
    $i.AddBezier(128, 194, 112, 194, 98, 182, 97, 162); $i.AddBezier(97, 162, 96, 140, 116, 128, 128, 96); $i.CloseFigure()
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 96)), ([System.Drawing.PointF]::new(0, 194)), $FunkeHell, $Funke), $i)
}

function New-Canvas([int] $w, [int] $h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
    return $bmp, $g
}

function Save-Bmp24([System.Drawing.Bitmap] $bmp, [string] $path) {
    # NSIS erwartet ein klassisches 24-Bit-BMP
    $rgb = $bmp.Clone([System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height), [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $rgb.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
}

# ---------- App-Icon ----------
function New-Icon([int] $s) {
    $bmp, $g = New-Canvas $s $s
    $g.Clear([System.Drawing.Color]::Transparent); $g.ScaleTransform($s / 256.0, $s / 256.0)
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath; $r = 56; $w = 255
    $bg.AddArc(0, 0, $r, $r, 180, 90); $bg.AddArc($w - $r, 0, $r, $r, 270, 90); $bg.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $bg.AddArc(0, $w - $r, $r, $r, 90, 90); $bg.CloseFigure()
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, 256)), $Stahl, $StahlTief), $bg)
    Draw-Fire $g; $g.Dispose(); return $bmp
}

$assets = Join-Path $root 'src\Lagerfeuer\Assets'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-Icon $s; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $b.Save((Join-Path $assets 'AppIcon.png')) }; , $ms.ToArray()
}
$fs = [IO.File]::Create((Join-Path $assets 'AppIcon.ico')); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count); $off = 6 + 16 * $sizes.Count
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $s = $sizes[$k]; $d = $(if ($s -ge 256) { 0 } else { $s })
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$k].Length); $bw.Write([uint32]$off); $off += $pngs[$k].Length
}
foreach ($p in $pngs) { $bw.Write($p) }; $bw.Close()

# ---------- Installer: Seitengrafik (164x314, hier doppelt) ----------
$W = 328; $H = 628
$bmp, $g = New-Canvas $W $H
$g.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $H)), $StahlTief, $Grund), 0, 0, $W, $H)
# Funken
$rnd = New-Object System.Random 7
foreach ($n in 1..28) {
    $x = 40 + $rnd.NextDouble() * ($W - 80); $y = 30 + $rnd.NextDouble() * 220; $r = 1.5 + $rnd.NextDouble() * 3
    $alpha = [int](60 + $rnd.NextDouble() * 150)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, $Funke))), [float]$x, [float]$y, [float]$r, [float]$r)
}
$st = $g.Save(); $g.TranslateTransform(($W - 240) / 2, 150); $g.ScaleTransform(240 / 256.0, 240 / 256.0); Draw-Fire $g; $g.Restore($st)
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'
$g.DrawString('Lagerfeuer', (New-Object System.Drawing.Font 'Palatino Linotype', 40, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)),
    (New-Object System.Drawing.SolidBrush $StahlHell), [System.Drawing.RectangleF]::new(0, 420, $W, 60), $sf)
$g.DrawString('Hörbücher aus Audiobookshelf', (New-Object System.Drawing.Font 'Segoe UI', 17, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)),
    (New-Object System.Drawing.SolidBrush $Funke), [System.Drawing.RectangleF]::new(0, 478, $W, 30), $sf)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $Glut), 0, $H - 8, $W, 8)
$g.Dispose(); Save-Bmp24 $bmp (Join-Path $root 'installer\art\welcome.bmp')

# ---------- Installer: Kopfgrafik (150x57, hier doppelt) ----------
$W = 300; $H = 114
$bmp, $g = New-Canvas $W $H
$g.Clear([System.Drawing.Color]::White)
$st = $g.Save(); $g.TranslateTransform($W - 106, 9); $g.ScaleTransform(96 / 256.0, 96 / 256.0)
$bg = New-Object System.Drawing.Drawing2D.GraphicsPath; $r = 56; $w = 255
$bg.AddArc(0, 0, $r, $r, 180, 90); $bg.AddArc($w - $r, 0, $r, $r, 270, 90); $bg.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $bg.AddArc(0, $w - $r, $r, $r, 90, 90); $bg.CloseFigure()
$g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, 256)), $Stahl, $StahlTief), $bg)
Draw-Fire $g; $g.Restore($st)
$g.Dispose(); Save-Bmp24 $bmp (Join-Path $root 'installer\art\header.bmp')

# ---------- Taskleisten-Knöpfe (Symbole aus Segoe Fluent Icons, weiß) ----------
$taskbar = Join-Path $assets 'Taskbar'
New-Item -ItemType Directory -Force $taskbar | Out-Null
$glyphs = [ordered]@{ previous = 0xE892; back10 = 0xED3C; play = 0xE768; pause = 0xE769; forward30 = 0xED3D; next = 0xE893 }
foreach ($name in $glyphs.Keys) {
    $iconSizes = 16, 20, 24, 32, 40, 48
    $images = foreach ($s in $iconSizes) {
        $bmp, $g = New-Canvas $s $s
        $g.Clear([System.Drawing.Color]::Transparent)
        $font = New-Object System.Drawing.Font 'Segoe Fluent Icons', ($s * 0.8), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
        $g.DrawString([string][char]$glyphs[$name], $font, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(0, 0, $s, $s), $sf)
        $g.Dispose(); $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray()
    }
    $fs = [IO.File]::Create((Join-Path $taskbar "$name.ico")); $bw = New-Object IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$iconSizes.Count); $off = 6 + 16 * $iconSizes.Count
    for ($k = 0; $k -lt $iconSizes.Count; $k++) {
        $s = $iconSizes[$k]
        $bw.Write([byte]$s); $bw.Write([byte]$s); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$images[$k].Length); $bw.Write([uint32]$off); $off += $images[$k].Length
    }
    foreach ($p in $images) { $bw.Write($p) }; $bw.Close()
}

Write-Host 'Grafiken erzeugt.' -ForegroundColor Green
