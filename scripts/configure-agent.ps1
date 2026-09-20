# Настройка параметров агента на локальной машине (выполнять на целевой машине или через GPO).
# Пример:
#   .\configure-agent.ps1 -AllowedGroups @("CN=Remote Control Operators,OU=Groups,DC=corp,DC=local")
param(
    [int]$ListenPort = 5900,
    [string[]]$AllowedGroups = @(),
    [switch]$RequireClientCert = $true
)

$ErrorActionPreference = "Stop"

$key = "HKLM:\SYSTEM\CurrentControlSet\Services\RemoteControlAgent\Parameters"
if (-not (Test-Path $key)) {
    New-Item -Path $key -Force | Out-Null
}

New-ItemProperty -Path $key -Name "ListenPort" -PropertyType DWord -Value $ListenPort -Force | Out-Null
New-ItemProperty -Path $key -Name "RequireClientCert" -PropertyType DWord -Value ([int]$RequireClientCert) -Force | Out-Null

if ($AllowedGroups.Count -gt 0) {
    New-ItemProperty -Path $key -Name "AllowedGroups" -PropertyType MultiString -Value $AllowedGroups -Force | Out-Null
    Write-Host "AllowedGroups: $($AllowedGroups -join ', ')"
} else {
    Remove-ItemProperty -Path $key -Name "AllowedGroups" -ErrorAction SilentlyContinue
    Write-Host "AllowedGroups не задан — доступ разрешён любому сертификату домена (НЕ для продакшена)."
}

Write-Host "Конфигурация записана в $key"
