# Картинки мастера установки (Inno Setup): боковая панель с градиентом и орбитами + маленький значок.
# Запуск: powershell -File scripts/make-wizard-images.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\installer'
$logo = [System.Drawing.Image]::FromFile((Resolve-Path (Join-Path $PSScriptRoot '..\src\Orbit\Assets\Orbit.png')))

function New-Side([int]$scale) {
    $w = 164 * $scale; $h = 314 * $scale
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $w, $h), ([System.Drawing.Color]::FromArgb(255, 0, 103, 192)), ([System.Drawing.Color]::FromArgb(255, 104, 72, 230))
    $g.FillRectangle($grad, 0, 0, $w, $h)
    # Концентрические орбиты
    $cx = $w / 2; $cy = $h * 0.42
    foreach ($r in 46, 74, 104, 136, 170) {
        $rr = $r * $scale
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(38, 255, 255, 255)), (1.2 * $scale)
        $g.DrawEllipse($pen, $cx - $rr, $cy - $rr, 2 * $rr, 2 * $rr)
    }
    # Точки-«спутники» на орбитах
    $dots = @(@(74, -40), @(104, 200), @(136, 25), @(170, 135))
    foreach ($d in $dots) {
        $rr = $d[0] * $scale; $a = $d[1] * [math]::PI / 180; $s = 5 * $scale
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 255, 255))), $cx + $rr * [math]::Cos($a) - $s / 2, $cy + $rr * [math]::Sin($a) - $s / 2, $s, $s)
    }
    $size = 72 * $scale
    $g.DrawImage($logo, [int]($cx - $size / 2), [int]($cy - $size / 2), [int]$size, [int]$size)
    # Подпись
    $font = New-Object System.Drawing.Font 'Segoe UI Semibold', (15 * $scale), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $small = New-Object System.Drawing.Font 'Segoe UI', (10.5 * $scale), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.DrawString('Orbit', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($h * 0.74), $w, (24 * $scale)), $fmt)
    $g.DrawString("Конвертер файлов`nс круговым меню", $small, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 255, 255, 255))), (New-Object System.Drawing.RectangleF 0, ($h * 0.74 + 26 * $scale), $w, (40 * $scale)), $fmt)
    $g.Dispose()
    return $bmp
}

function New-Small([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($logo, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

(New-Side 1).Save((Join-Path $out 'wizard-large.png'), [System.Drawing.Imaging.ImageFormat]::Png)
(New-Side 2).Save((Join-Path $out 'wizard-large-2x.png'), [System.Drawing.Imaging.ImageFormat]::Png)
(New-Small 55).Save((Join-Path $out 'wizard-small.png'), [System.Drawing.Imaging.ImageFormat]::Png)
(New-Small 110).Save((Join-Path $out 'wizard-small-2x.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
Write-Output 'Готово: installer\wizard-*.png'
