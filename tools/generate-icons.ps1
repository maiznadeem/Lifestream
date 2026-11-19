<#
.SYNOPSIS
  Renders the Lifestream app mark (a blood drop with a white heart) to every icon file the app and the Store package use.
.DESCRIPTION
  Usage: pwsh tools/generate-icons.ps1      (Windows only; run from anywhere; safe to re-run, the output is byte-for-byte the same)

  The mark is drawn from vector geometry in a 256x256 design space on a transparent background (no tile):
    drop   M128,14 C128,14 40,108 40,166 A88,88 0 0 0 216,166 C216,108 128,14 128,14 Z
           filled with a linear gradient #F0475C (0.2,0 = top left) to #B3122E (0.8,1 = bottom right)
    heart  white, about 76 wide, centered in the round belly of the drop (cubic curves, symmetric)
  Frames of 24 px and smaller drop the heart and scale the drop to the frame height with 1 px of padding so it stays crisp;
  at 16 px the drop is also 10 px wide so its widest points land on whole pixels.

  Rendering uses WPF (DrawingVisual + RenderTargetBitmap, 96 DPI, Pbgra32, default antialiasing); no window is ever created.

  Writes:
    src/Lifestream.Desktop/Assets/Lifestream.ico            16, 20, 24, 32, 40, 48, 64, 256 px (32-bit, PNG-compressed frames)
    src/Lifestream.Desktop/Assets/Lifestream-<size>.png     16, 24, 32, 48, 64, 256, 512
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
New-Item -ItemType Directory -Force $assets, $store | Out-Null

$dropPath = 'M128,14 C128,14 40,108 40,166 A88,88 0 0 0 216,166 C216,108 128,14 128,14 Z'
$heartPath = 'M128,200 C100,180 90,166 90,152 C90,140 99,132 109,132 C117,132 124,136 128,144 C132,136 139,132 147,132 C157,132 166,140 166,152 C166,166 156,180 128,200 Z'
$drop = [Windows.Media.Geometry]::Parse($dropPath); $drop.Freeze()
$heart = [Windows.Media.Geometry]::Parse($heartPath); $heart.Freeze()

$gradient = New-Object Windows.Media.LinearGradientBrush
$gradient.StartPoint = New-Object Windows.Point 0.2, 0
$gradient.EndPoint = New-Object Windows.Point 0.8, 1
$gradient.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.Color]::FromRgb(0xF0, 0x47, 0x5C)), 0))
$gradient.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.Color]::FromRgb(0xB3, 0x12, 0x2E)), 1))
$gradient.Freeze()
$white = [Windows.Media.Brushes]::White

# Renders the mark into a size x size PNG. $Scale is the share of the frame the 256 design space occupies (1 = all of it).
function New-MarkPng([int]$Size, [double]$Scale = 1.0) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    if ($Size -le 24) {
        # Drop only, filling the frame height minus 1 px of padding; drop bounds are x 40..216, y 14..254 in the design space.
        $sy = ($Size - 2) / 240.0
        $sx = if ($Size -le 16) { 10.0 / 176 } else { $sy }
        $left = ($Size - 176 * $sx) / 2
        $dc.PushTransform((New-Object Windows.Media.MatrixTransform ($sx, 0, 0, $sy, ($left - 40 * $sx), (1 - 14 * $sy))))
        $dc.DrawGeometry($gradient, $null, $drop)
        $dc.Pop()
    } else {
        $s = $Size * $Scale / 256.0
        $offset = $Size * (1 - $Scale) / 2
        $dc.PushTransform((New-Object Windows.Media.MatrixTransform ($s, 0, 0, $s, $offset, $offset)))
        $dc.DrawGeometry($gradient, $null, $drop)
        $dc.DrawGeometry($white, $null, $heart)
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
    [IO.File]::WriteAllBytes((Join-Path $assets "Lifestream-$size.png"), (New-MarkPng $size))
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
