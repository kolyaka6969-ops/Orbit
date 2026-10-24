# Собирает две версии Orbit (автономно: .NET внутри, ставить ничего не нужно):
#   publish/        — обычная: Windows 11 и Windows 10 с обновлениями;
#   publish-win10/  — для Windows 10 22H2 без обновлений, где обычная падает с «Unknown Hard Error» (см. Orbit.csproj).
# Если установлен Inno Setup 6 — ещё установщики artifacts/Orbit-<версия>-Setup-x64.exe
# и artifacts/Orbit-<версия>-Win10-22H2-Setup-x64.exe и их контрольные суммы в artifacts/SHA256SUMS.txt.
param(
    [string]$Version = '1.1.0',
    [switch]$NoInstaller
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) { $dotnet = $local }
    if (-not $dotnet) { throw 'Нужен .NET SDK 10: https://dotnet.microsoft.com/download' }

    $editions = @(
        @{ Name = ''; Output = 'publish' },
        @{ Name = 'Win10'; Output = 'publish-win10' }
    )
    foreach ($edition in $editions) {
        if (Test-Path $edition.Output) { Remove-Item $edition.Output -Recurse -Force }
        & $dotnet publish src/Orbit/Orbit.csproj -c Release -r win-x64 --self-contained `
            -p:Version=$Version -p:OrbitEdition=$($edition.Name) -p:DebugType=none -o $edition.Output
        if ($LASTEXITCODE -ne 0) { throw 'Сборка Orbit завершилась с ошибкой.' }
        Copy-Item LICENSE, THIRD-PARTY-NOTICES.md "$($edition.Output)/"
        Write-Output "Готово: $($edition.Output)\Orbit.exe"
    }

    if ($NoInstaller) { return }
    $iscc = @(
        (Get-Command iscc -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        Write-Warning 'Inno Setup 6 не найден — установщики не собраны. Скачать: https://jrsoftware.org/isdl.php'
        return
    }
    foreach ($edition in $editions) {
        & $iscc /Qp "/DAppVersion=$Version" "/DEdition=$($edition.Name)" installer/Orbit.iss
        if ($LASTEXITCODE -ne 0) { throw 'Сборка установщика завершилась с ошибкой.' }
    }
    $installers = Get-ChildItem "artifacts/Orbit-$Version-Setup-x64.exe", "artifacts/Orbit-$Version-Win10-22H2-Setup-x64.exe"
    $installers | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
        Set-Content artifacts/SHA256SUMS.txt -Encoding ascii
    $installers | ForEach-Object { Write-Output "Установщик: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) МБ)" }
}
finally { Pop-Location }
