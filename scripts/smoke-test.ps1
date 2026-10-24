# Прогоняет основные операции Orbit на тестовых файлах через режим «--run» и проверяет, что результаты появились.
# Запуск: powershell -File scripts/smoke-test.ps1 -Exe publish\Orbit.exe -WorkDir C:\Temp\orbit-test
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$WorkDir
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $WorkDir | Out-Null

# --- исходные файлы: картинки из Windows, голос из «Параметров», ролик из OOBE
function Copy-Sample($from, $name) {
    $target = Join-Path $WorkDir $name
    if (-not (Test-Path $target) -and (Test-Path $from)) { Copy-Item $from $target }
    if (Test-Path $target) { return $target }
    return $null
}
$jpg = Copy-Sample "$env:WINDIR\Web\Wallpaper\Windows\img0.jpg" 'wallpaper.jpg'
$png = Copy-Sample (Join-Path $PSScriptRoot '..\src\Orbit\Assets\Orbit.png') 'logo.png'
$mp3 = Copy-Sample "$env:WINDIR\ImmersiveControlPanel\SystemSettings\Assets\Aria.mp3" 'voice.mp3'
$mp4 = Copy-Sample (Get-ChildItem "$env:WINDIR\SystemApps" -Recurse -Filter 'oobe-intro.mp4' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName 'intro.mp4'
$folder = Join-Path $WorkDir 'folder'
New-Item -ItemType Directory -Force $folder | Out-Null
Set-Content (Join-Path $folder 'readme.txt') 'Orbit test' -Encoding utf8

$script:failed = 0
$script:passed = 0

function Invoke-Orbit([string[]]$arguments) {
    $out = [IO.Path]::GetTempFileName()
    $quoted = $arguments | ForEach-Object { '"' + $_ + '"' }
    $p = Start-Process -FilePath $Exe -ArgumentList $quoted -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
    $text = Get-Content $out -Raw -Encoding utf8
    Remove-Item $out
    return @{ Code = $p.ExitCode; Output = ($text | Out-String).Trim() }
}

function Test-Op([string]$id, [string[]]$files) {
    if ($files -contains $null) { Write-Host "SKIP  $id (нет исходного файла)"; return @() }
    $r = Invoke-Orbit (@('--run', $id) + $files)
    $outputs = @($r.Output -split "`r?`n" | Where-Object { $_ -and -not $_.StartsWith('#') -and -not $_.StartsWith('ERROR') })
    $ok = $r.Code -eq 0 -and ($outputs | Where-Object { -not (Test-Path $_) }).Count -eq 0
    if ($ok) {
        $script:passed++
        $sizes = ($outputs | ForEach-Object { $i = Get-Item $_; if ($i.PSIsContainer) { "$($i.Name)\ ($((Get-ChildItem $_ -Recurse -File).Count) файлов)" } else { "$($i.Name) ($([math]::Round($i.Length / 1KB)) КБ)" } }) -join ', '
        $msg = ($r.Output -split "`r?`n" | Where-Object { $_.StartsWith('#') }) -join ' '
        Write-Host "OK    $id -> $sizes $msg"
    } else {
        $script:failed++
        Write-Host "FAIL  $id -> $($r.Output)"
    }
    return $outputs
}

foreach ($t in 'png', 'webp', 'avif', 'tiff', 'gif', 'ico', 'pdf') { Test-Op "image.to.$t" @($jpg) | Out-Null }
Test-Op 'image.to.jpg' @($png) | Out-Null
foreach ($t in 'compress', 'half', 'web', 'rotate.right', 'mirror', 'gray', 'square', 'strip') { Test-Op "image.$t" @($jpg) | Out-Null }
Test-Op 'image.compress' @($png) | Out-Null
# Картинка с текстом для распознавания
Add-Type -AssemblyName System.Drawing
$textPng = Join-Path $WorkDir 'text.png'
$bmp = New-Object System.Drawing.Bitmap 900, 220
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::White); $g.TextRenderingHint = 'AntiAlias'
$g.DrawString('Orbit converts files 2026', (New-Object System.Drawing.Font 'Segoe UI', 48), [System.Drawing.Brushes]::Black, 20, 60)
$g.Dispose(); $bmp.Save($textPng, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Test-Op 'image.ocr' @($textPng) | Out-Null
$merged = @(Test-Op 'image.pdf' @($jpg, $png))

foreach ($t in 'wav', 'm4a', 'flac', 'wma') { Test-Op "audio.to.$t" @($mp3) | Out-Null }
Test-Op 'audio.compress' @($mp3) | Out-Null

Write-Host (Invoke-Orbit @('--probe', $mp4)).Output
Test-Op 'video.to.wmv' @($mp4) | Out-Null
Test-Op 'video.audio.mp3' @($mp4) | Out-Null
Test-Op 'video.compress' @($mp4) | Out-Null
Test-Op 'video.720' @($mp4) | Out-Null
Test-Op 'video.mute' @($mp4) | Out-Null

if ($merged) {
    Test-Op 'pdf.to.png' @($merged[0]) | Out-Null
    Test-Op 'pdf.split' @($merged[0]) | Out-Null
    Test-Op 'pdf.merge' @($merged[0], $merged[0]) | Out-Null
}

$zip = @(Test-Op 'tools.zip' @($folder, $png))
if ($zip) { Test-Op 'archive.extract' @($zip[0]) | Out-Null }
Push-Location $WorkDir
tar -czf sample.tar.gz folder
Pop-Location
Test-Op 'archive.extract' @((Join-Path $WorkDir 'sample.tar.gz')) | Out-Null
Test-Op 'tools.sha256' @($png) | Out-Null
Test-Op 'action.duplicate' @($png) | Out-Null

$list = Invoke-Orbit @('--list', $jpg)
Write-Output "`n--list wallpaper.jpg:`n$($list.Output)"
Write-Output "`nИтого: $script:passed успешно, $script:failed с ошибкой"
if ($script:failed -gt 0) { exit 1 }
