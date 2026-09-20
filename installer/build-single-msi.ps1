<#
.SYNOPSIS
    Сборка ЕДИНОГО MSI Remote Control: агент + автономный мини-CA + автовыпуск
    сертификата + правила доступа + консоль оператора.

.DESCRIPTION
    Требуется WiX Toolset v5 (WixToolset.Firewall.wixext), .NET SDK 8 и уже собранный
    агент (build\agent\Release\RemoteControlAgent.exe).

    Что делает:
      1) publish мини-CA self-contained  -> installer\ca\payload
      2) publish консоли self-contained  -> installer\viewer\payload
      3) копирует агент и скрипты        -> installer\agent
      4) wix build                        -> dist\msi\RemoteControl.msi

.EXAMPLE
    $env:DOTNET_ROOT='C:\dotnet'
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\Projects\RemoteControl\installer\build-single-msi.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$AgentExe,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $AgentExe) { $AgentExe = Join-Path $RepoRoot "build\agent\$Configuration\RemoteControlAgent.exe" }
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'dist\msi' }
$Dotnet = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { 'dotnet' }
$Wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'

if (-not (Test-Path $AgentExe)) { throw "не найден бинарь агента: $AgentExe (соберите агент)" }
if (-not (Test-Path $Wix)) { throw "WiX не найден: $Wix. Установите: dotnet tool install --global wix --version 5.0.2" }
if (-not $env:DOTNET_ROOT -and (Test-Path 'C:\dotnet')) { $env:DOTNET_ROOT = 'C:\dotnet'; $Dotnet = 'C:\dotnet\dotnet.exe' }

function Reset-Dir([string]$Path) {
    if (Test-Path $Path) { Remove-Item $Path -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

Write-Host '==> 1/4 publish мини-CA (self-contained)' -ForegroundColor Cyan
$caPayload = Join-Path $PSScriptRoot 'ca\payload'
Reset-Dir $caPayload
& $Dotnet publish (Join-Path $RepoRoot 'tools\labca\labca.csproj') -c $Configuration -r win-x64 --self-contained true -o $caPayload
if ($LASTEXITCODE -ne 0) { throw "publish labca завершился с кодом $LASTEXITCODE" }

Write-Host '==> 2/4 publish консоли (self-contained)' -ForegroundColor Cyan
$viewerPayload = Join-Path $PSScriptRoot 'viewer\payload'
Reset-Dir $viewerPayload
& $Dotnet publish (Join-Path $RepoRoot 'src\Viewer\RemoteControl.Viewer\RemoteControl.Viewer.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $viewerPayload
if ($LASTEXITCODE -ne 0) { throw "publish viewer завершился с кодом $LASTEXITCODE" }

Write-Host '==> 3/4 файлы агента и скрипты' -ForegroundColor Cyan
$agentDir = Join-Path $PSScriptRoot 'agent'
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
Copy-Item $AgentExe (Join-Path $agentDir 'RemoteControlAgent.exe') -Force
Copy-Item (Join-Path $RepoRoot 'scripts\enroll-cert.ps1') (Join-Path $agentDir 'enroll-cert.ps1') -Force

Write-Host '==> 4/4 wix build (единый MSI)' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Push-Location $PSScriptRoot
try {
    & $Wix build RemoteControl.wxs -arch x64 -ext WixToolset.Firewall.wixext -o (Join-Path $OutputDir 'RemoteControl.msi')
    if ($LASTEXITCODE -ne 0) { throw "wix завершился с кодом $LASTEXITCODE" }
}
finally { Pop-Location }

Write-Host ''
Get-ChildItem $OutputDir -Filter RemoteControl.msi |
    ForEach-Object { "{0,-28} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB) }
