# Собирает Orbit в папку publish/ (автономно: .NET внутри, ставить ничего не нужно)
# и, если установлен Inno Setup 6, — установщик artifacts/Orbit-<версия>-Setup-x64.exe.
param(
    [string]$Version = '1.0.0',
    [switch]$NoInstaller
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) { $dotnet = $local }
    if (-not $dotnet) { throw 'Нужен .NET SDK 10: https://dotnet.microsoft.com/download' }

    if (Test-Path publish) { Remove-Item publish -Recurse -Force }
    & $dotnet publish src/Orbit/Orbit.csproj -c Release -r win-x64 --self-contained `
        -p:Version=$Version -p:DebugType=none -o publish
    if ($LASTEXITCODE -ne 0) { throw 'Сборка Orbit завершилась с ошибкой.' }
    Copy-Item LICENSE, THIRD-PARTY-NOTICES.md publish/
    Write-Output "Готово: publish\Orbit.exe"

    if ($NoInstaller) { return }
    $iscc = @(
        (Get-Command iscc -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        Write-Warning 'Inno Setup 6 не найден — установщик не собран. Скачать: https://jrsoftware.org/isdl.php'
        return
    }
    & $iscc /Qp "/DAppVersion=$Version" installer/Orbit.iss
    if ($LASTEXITCODE -ne 0) { throw 'Сборка установщика завершилась с ошибкой.' }
    Get-ChildItem artifacts/*.exe | ForEach-Object { Write-Output "Установщик: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) МБ)" }
}
finally { Pop-Location }
