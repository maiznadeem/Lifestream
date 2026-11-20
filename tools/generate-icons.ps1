<#
.SYNOPSIS
  Renders the Lifestream app mark of direction A (a blood drop with a white stream wave) to every icon file the app and the Store package use.
.DESCRIPTION
  Usage: pwsh tools/generate-icons.ps1      (Windows only; run from anywhere; safe to re-run, the output is byte-for-byte the same)

  The mark is drawn from vector geometry in a 256x256 design space on a transparent background (no tile); it is the 32 x 32 mark of
  Theme/Images.xaml (Geometry.LogoDrop, Geometry.LogoWave) scaled by 8:
    drop   M128,20 C128,20 49.6,105.6 49.6,156.8 A78.4,78.4 0 0 0 206.4,156.8 C206.4,105.6 128,20 128,20 Z, filled with the blood token #B4162E
    wave   M84.8,163.2 C99.2,150.4 113.6,150.4 128,163.2 C142.4,176 156.8,176 171.2,163.2, a white 16.8 round-capped stroke
  Frames of 24 px and smaller drop the wave (it would be thinner than a pixel) and scale the drop to the frame height with 1 px of
  padding so it stays crisp; at 16 px the drop is also 10 px wide so its widest points land on whole pixels.

  Rendering uses WPF (DrawingVisual + RenderTargetBitmap, 96 DPI, Pbgra32, default antialiasing); no window is ever created.

  Writes:
    src/Lifestream.Desktop/Assets/Lifestream.ico            16, 20, 24, 32, 40, 48, 64, 256 px (32-bit, PNG-compressed frames)
    packaging/icon/Lifestream-<size>.png             16, 24, 32, 48, 64, 256, 512
    packaging/store/Assets/StoreLogo.png (50), Square44x44Logo.png (44), Square150x150Logo.png (150, mark at 60%)
  packaging/store/build-msix.ps1 prefers the files in packaging/store/Assets over its generated placeholders.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$assets = Join-Path $root 'src/Lifestream.Desktop/Assets'
$store = Join-Path $root 'packaging/store/Assets'
$pngs = Join-Path $root 'packaging/icon'   # PNG renders for the README and other artwork; not shipped in the app
New-Item -ItemType Directory -Path $pngs -Force | Out-Null
New-Item -ItemType Directory -Force $assets, $store | Out-Null

$dropPath = 'M128,20 C128,20 49.6,105.6 49.6,156.8 A78.4,78.4 0 0 0 206.4,156.8 C206.4,105.6 128,20 128,20 Z'
$wavePath = 'M84.8,163.2 C99.2,150.4 113.6,150.4 128,163.2 C142.4,176 156.8,176 171.2,163.2'
$drop = [Windows.Media.Geometry]::Parse($dropPath); $drop.Freeze()
$wave = [Windows.Media.Geometry]::Parse($wavePath); $wave.Freeze()

$blood = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(0xB4, 0x16, 0x2E))
$blood.Freeze()
$white = [Windows.Media.Brushes]::White
$wavePen = New-Object Windows.Media.Pen ($white, 16.8)
$wavePen.StartLineCap = [Windows.Media.PenLineCap]::Round
$wavePen.EndLineCap = [Windows.Media.PenLineCap]::Round
$wavePen.Freeze()

# Renders the mark into a size x size PNG. $Scale is the share of the frame the 256 design space occupies (1 = all of it).
function New-MarkPng([int]$Size, [double]$Scale = 1.0) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    if ($Size -le 24) {
        # Drop only, filling the frame height minus 1 px of padding; drop bounds are x 49.6..206.4, y 20..235.2 in the design space.
        $sy = ($Size - 2) / 215.2
        $sx = if ($Size -le 16) { 10.0 / 156.8 } else { $sy }
        $left = ($Size - 156.8 * $sx) / 2
        $dc.PushTransform((New-Object Windows.Media.MatrixTransform ($sx, 0, 0, $sy, ($left - 49.6 * $sx), (1 - 20 * $sy))))
        $dc.DrawGeometry($blood, $null, $drop)
        $dc.Pop()
    } else {
        $s = $Size * $Scale / 256.0
        $offset = $Size * (1 - $Scale) / 2
        $dc.PushTransform((New-Object Windows.Media.MatrixTransform ($s, 0, 0, $s, $offset, $offset)))
        $dc.DrawGeometry($blood, $null, $drop)
        $dc.DrawGeometry($null, $wavePen, $wave)
        $dc.Pop()
    }
    $dc.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap ($Size, $Size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    , $stream.ToArray()
}

foreach ($size in 16, 24, 32, 48, 64, 256, 512) {
    [IO.File]::WriteAllBytes((Join-Path $pngs "Lifestream-$size.png"), (New-MarkPng $size))
}

[IO.File]::WriteAllBytes((Join-Path $store 'StoreLogo.png'), (New-MarkPng 50))
[IO.File]::WriteAllBytes((Join-Path $store 'Square44x44Logo.png'), (New-MarkPng 44))
[IO.File]::WriteAllBytes((Join-Path $store 'Square150x150Logo.png'), (New-MarkPng 150 0.6))

# ICO: ICONDIR (6 bytes) + one ICONDIRENTRY (16 bytes) per frame + the PNG frames. Width/height byte 0 means 256.
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($size in $sizes) { , (New-MarkPng $size) }
$ico = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'Lifestream.ico'), $ico.ToArray())

Write-Host "Icons written to $assets and $store"
