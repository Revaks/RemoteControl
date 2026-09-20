# Сборка консоли оператора (WPF, .NET 8) под Windows.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\Viewer\RemoteControl.Viewer\RemoteControl.Viewer.csproj"

Write-Host "dotnet build $proj -c $Configuration"
& dotnet build $proj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

Write-Host ""
Write-Host "Готово: src\Viewer\RemoteControl.Viewer\bin\$Configuration\net8.0-windows\RemoteControl.Viewer.exe"
