# Рисует значок Orbit (скруглённый квадрат с градиентом, планета и орбита) и собирает Orbit.ico + Orbit.png.
# Запуск: powershell -File scripts/make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Orbit\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Плитка
    $margin = if ($size -le 24) { 4 } else { 12 }
    $tile = New-RoundedRect $margin $margin (256 - 2 * $margin) (256 - 2 * $margin) 52
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 256, 256), ([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), ([System.Drawing.Color]::FromArgb(255, 116, 77, 255))
    $g.FillPath($grad, $tile)
    # Мягкий блик сверху
    $shine = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, 256), ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($shine, $tile)

    $white = [System.Drawing.Color]::White
    $state = $g.Save()
    $g.TranslateTransform(128, 128)
    $g.RotateTransform(-28)
    # Задняя половина орбиты (за планетой)
    $ringWidth = if ($size -le 24) { 22 } elseif ($size -le 48) { 16 } else { 12 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)), $ringWidth
    $g.DrawArc($pen, -96, -38, 192, 76, 180, 180)
    $g.Restore($state)

    # Планета
    $planetR = if ($size -le 24) { 58 } else { 50 }
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), 128 - $planetR, 128 - $planetR, 2 * $planetR, 2 * $planetR)

    $state = $g.Save()
    $g.TranslateTransform(128, 128)
    $g.RotateTransform(-28)
    # Передняя половина орбиты
    $pen2 = New-Object System.Drawing.Pen $white, $ringWidth
    $g.DrawArc($pen2, -96, -38, 192, 76, 0, 180)
    # Спутник
    if ($size -gt 24) {
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), 78, -16, 30, 30)
    }
    $g.Restore($state)

    $g.Dispose()
    return $bmp
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($size in $sizes) {
    $bmp = Render $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir 'Orbit.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO: заголовок, каталог, затем PNG-данные каждого размера
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'Orbit.ico'), $out.ToArray())
Write-Output "Готово: $OutDir\Orbit.ico"
