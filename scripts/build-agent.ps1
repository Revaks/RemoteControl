# Сборка агента под Windows (Visual Studio + CMake).
# Требуется: cmake, Visual Studio 2022 (C++ workload) или Build Tools.
# LibVNCServer либо подтянется с GitHub, либо укажите локальный исходник.
param(
    [string]$LibVncDir = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "src\Agent"
$build = Join-Path $root "build\agent"

$cmakeArgs = @("-S", $src, "-B", $build, "-A", "x64")
if ($LibVncDir) {
    $cmakeArgs += "-DLIBVNCSERVER_DIR=$LibVncDir"
}

Write-Host "cmake $($cmakeArgs -join ' ')"
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

& cmake --build $build --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

Write-Host ""
Write-Host "Готово: $build\$Configuration\RemoteControlAgent.exe"
