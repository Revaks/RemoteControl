<#
.SYNOPSIS
    Сборка MSI-установщиков Remote Control (агент и консоль оператора).

.DESCRIPTION
    Требуется WiX Toolset v5 (бесплатная версия; v6+ требует OSMF-подписку):
        dotnet tool install --global wix --version 5.0.2
        wix extension add --global WixToolset.Firewall.wixext/5.0.2

    На этой VM .NET поставлен в C:\dotnet и не зарегистрирован в реестре,
    поэтому задаём DOTNET_ROOT, иначе apphost не находит runtime.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\Projects\RemoteControl\installer\build-msi.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$AgentExe,
    [string]$ViewerPublishDir,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $AgentExe) {
    $AgentExe = Join-Path $RepoRoot "build\agent\$Configuration\RemoteControlAgent.exe"
}
if (-not $ViewerPublishDir) {
    $ViewerPublishDir = Join-Path $RepoRoot 'dist\viewer-sc'
}
if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot 'dist\msi'
}

# apphost ищет runtime через DOTNET_ROOT (dotnet на этой VM не в реестре)
if (-not $env:DOTNET_ROOT -and (Test-Path 'C:\dotnet')) { $env:DOTNET_ROOT = 'C:\dotnet' }

$Wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $Wix)) { throw "WiX не найден: $Wix. Установите: dotnet tool install --global wix --version 5.0.2" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function Invoke-Wix {
    param([string[]]$WixArgs, [string]$Step, [string]$WorkingDirectory)
    Write-Host "==> $Step" -ForegroundColor Cyan
    $prev = Get-Location
    if ($WorkingDirectory) { Set-Location $WorkingDirectory }
    try {
        Write-Host "    wix $($WixArgs -join ' ')"
        & $Wix @WixArgs
        if ($LASTEXITCODE -ne 0) { throw "wix завершился с кодом $LASTEXITCODE ($Step)" }
    }
    finally {
        Set-Location $prev
    }
}

# ---------- 1. Агент ----------
$AgentDir = Join-Path $PSScriptRoot 'agent'
if (-not (Test-Path $AgentExe)) { throw "Не найден бинарник агента: $AgentExe. Соберите агент (stage3.ps1)." }

Copy-Item $AgentExe (Join-Path $AgentDir 'RemoteControlAgent.exe') -Force
$agentMsi = Join-Path $OutputDir 'RemoteControlAgent.msi'

Invoke-Wix -Step 'Сборка MSI агента' -WorkingDirectory $AgentDir -WixArgs @(
    'build', (Join-Path $AgentDir 'RemoteControlAgent.wxs'),
    '-arch', 'x64',
    '-ext', 'WixToolset.Firewall.wixext',
    '-o', $agentMsi
)

# ---------- 2. Консоль оператора ----------
$ViewerDir = Join-Path $PSScriptRoot 'viewer'
$PayloadDir = Join-Path $ViewerDir 'payload'
if (-not (Test-Path $ViewerPublishDir)) { throw "Не найден publish консоли: $ViewerPublishDir" }

if (Test-Path $PayloadDir) { Remove-Item $PayloadDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PayloadDir | Out-Null
Copy-Item (Join-Path $ViewerPublishDir '*') $PayloadDir -Recurse -Force

$viewerMsi = Join-Path $OutputDir 'RemoteControlViewer.msi'
Invoke-Wix -Step 'Сборка MSI консоли' -WorkingDirectory $ViewerDir -WixArgs @(
    'build', (Join-Path $ViewerDir 'RemoteControlViewer.wxs'),
    '-arch', 'x64',
    '-o', $viewerMsi
)

Write-Host ''
Write-Host 'Готово:' -ForegroundColor Green
Get-ChildItem $OutputDir -Filter *.msi | ForEach-Object {
    "{0,-32} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
