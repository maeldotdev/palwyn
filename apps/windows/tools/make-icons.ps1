# Generates the app, taskbar and tray icons from the Palwyn "link" mark (docs/brand/logo-*.svg):
# a phone and a PC screen joined like two chain links. Every icon comes in a dark and a light version.
# Run with Windows PowerShell 5.1 (System.Drawing).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\Palwyn.App\Assets'
New-Item -ItemType Directory -Force (Join-Path $assets 'Tray') | Out-Null

function Rgb([int] $a, [string] $hex) { [Drawing.Color]::FromArgb($a, [Convert]::ToInt32($hex.Substring(0, 2), 16), [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16)) }
$themes = @{
    dark  = @{ Tile = (Rgb 255 '18181B'); Phone = (Rgb 255 'FAFAFA'); Link = (Rgb 255 '2ED3A1') }
    light = @{ Tile = (Rgb 255 'FAFAFA'); Phone = (Rgb 255 '18181B'); Link = (Rgb 255 '12A67C') }
}

# The mark on a 256 grid (same geometry as the SVGs). Each outline leaves a gap where the other passes over it.
function Phone-Path {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddLine(140, 179, 140, 180); $p.AddArc(92, 156, 48, 48, 0, 90)
    $p.AddLine(116, 204, 76, 204); $p.AddArc(52, 156, 48, 48, 90, 90)
    $p.AddLine(52, 180, 52, 76); $p.AddArc(52, 52, 48, 48, 180, 90)
    $p.AddLine(76, 52, 116, 52); $p.AddArc(92, 52, 48, 48, 270, 90)
    $p.AddLine(140, 76, 140, 149); $p
}
function Screen-Path {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddLine(155, 92, 184, 92); $p.AddArc(164, 92, 40, 40, 270, 90)
    $p.AddLine(204, 112, 204, 144); $p.AddArc(164, 124, 40, 40, 0, 90)
    $p.AddLine(184, 164, 124, 164); $p.AddArc(104, 124, 40, 40, 90, 90)
    $p.AddLine(104, 144, 104, 112); $p.AddArc(104, 92, 40, 40, 180, 90)
    $p.AddLine(124, 92, 125, 92); $p
}

# $tile: draw the rounded tile behind the mark; otherwise the mark alone fills the image (tray).
function New-Png([int] $size, [Drawing.Color] $phone, [Drawing.Color] $link, $tile) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    if ($tile) {
        $r = [Math]::Max(2, [int]($size * 0.22)); $d = 2 * $r; $e = $size - 1
        $shape = New-Object Drawing.Drawing2D.GraphicsPath
        $shape.AddArc(0, 0, $d, $d, 180, 90); $shape.AddArc($e - $d, 0, $d, $d, 270, 90)
        $shape.AddArc($e - $d, $e - $d, $d, $d, 0, 90); $shape.AddArc(0, $e - $d, $d, $d, 90, 90); $shape.CloseFigure()
        $g.FillPath((New-Object Drawing.SolidBrush $tile), $shape)
        $g.ScaleTransform($size / 256, $size / 256)
    } else {
        $g.ScaleTransform($size / 168, $size / 168); $g.TranslateTransform(-44, -44) # mark bounds incl. stroke: 44..212
    }
    foreach ($pair in @(@((Screen-Path), $link), @((Phone-Path), $phone))) {
        $pen = New-Object Drawing.Pen $pair[1], 16
        $pen.LineJoin = 'Round'
        $g.DrawPath($pen, $pair[0])
    }
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    , $ms.ToArray()
}
function Tile-Png([int] $size, [string] $theme) { $t = $themes[$theme]; New-Png $size $t.Phone $t.Link $t.Tile }

function Write-Ico([string] $file, [int[]] $sizes, [Drawing.Color] $phone, [Drawing.Color] $link, $tile) {
    $pngs = foreach ($s in $sizes) { , (New-Png $s $phone $link $tile) }
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write([byte[]]$p) }
    [IO.File]::WriteAllBytes($file, $ms.ToArray())
}
function Save([string] $name, [byte[]] $bytes) { [IO.File]::WriteAllBytes((Join-Path $assets $name), $bytes) }

# Package logos. Windows shows the "unplated" (dark) or "lightunplated" (light) set on the taskbar and Start to match its theme.
Save 'Square44x44Logo.png' (Tile-Png 44 'dark')
Save 'Square150x150Logo.png' (Tile-Png 150 'dark')
Save 'StoreLogo.png' (Tile-Png 50 'dark')
foreach ($s in 16, 24, 32, 48, 256) {
    Save "Square44x44Logo.targetsize-$($s)_altform-unplated.png" (Tile-Png $s 'dark')
    Save "Square44x44Logo.targetsize-$($s)_altform-lightunplated.png" (Tile-Png $s 'light')
}

# In-app logo and window icon, picked by the app's theme at run time.
foreach ($theme in 'dark', 'light') {
    $suffix = if ($theme -eq 'light') { '-light' } else { '' }
    Save "Logo$suffix.png" (Tile-Png 64 $theme)
    $t = $themes[$theme]
    Write-Ico (Join-Path $assets "AppIcon$suffix.ico") @(16, 20, 24, 32, 48, 64, 256) $t.Phone $t.Link $t.Tile
}

# Tray: "dark" = for a dark taskbar. "off" = not connected: the whole mark dimmed, without the green.
$tray = @{
    'dark-on'   = @((Rgb 255 'FFFFFF'), (Rgb 255 '2ED3A1'))
    'dark-off'  = @((Rgb 140 'FFFFFF'), (Rgb 140 'FFFFFF'))
    'light-on'  = @((Rgb 255 '1A1A1A'), (Rgb 255 '12A67C'))
    'light-off' = @((Rgb 140 '1A1A1A'), (Rgb 140 '1A1A1A'))
}
foreach ($k in $tray.Keys) {
    Write-Ico (Join-Path $assets "Tray\tray-$k.ico") @(16, 20, 24, 32, 40, 48) $tray[$k][0] $tray[$k][1] $null
}
"icons written to $assets"
