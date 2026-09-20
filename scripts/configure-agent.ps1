# Настройка параметров агента на локальной машине (выполнять на целевой машине или через GPO).
# Пример:
#   .\configure-agent.ps1 -AllowedGroups @("CN=Remote Control Operators,OU=Groups,DC=corp,DC=local")
#   .\configure-agent.ps1 -AllowedGroups @("CN=Remote Control Operators,OU=Groups,DC=corp,DC=local") -AllowLocalAdmins $true
param(
    [int]$ListenPort = 5900,
    [string[]]$AllowedGroups = @(),
    [switch]$RequireClientCert = $true,
    # Разрешать доступ учётке, входящей в локальную группу Administrators этой машины,
    # даже если она не состоит в AllowedGroups. По умолчанию включено.
    [bool]$AllowLocalAdmins = $true
)

$ErrorActionPreference = "Stop"

$key = "HKLM:\SYSTEM\CurrentControlSet\Services\RemoteControlAgent\Parameters"
if (-not (Test-Path $key)) {
    New-Item -Path $key -Force | Out-Null
}

New-ItemProperty -Path $key -Name "ListenPort" -PropertyType DWord -Value $ListenPort -Force | Out-Null
# [switch] нельзя привести к [int] напрямую — берём IsPresent.
$requireCertValue = if ($RequireClientCert.IsPresent) { 1 } else { 0 }
New-ItemProperty -Path $key -Name "RequireClientCert" -PropertyType DWord -Value $requireCertValue -Force | Out-Null
New-ItemProperty -Path $key -Name "AllowLocalAdmins" -PropertyType DWord -Value ([int]$AllowLocalAdmins) -Force | Out-Null
Write-Host "AllowLocalAdmins: $AllowLocalAdmins"

if ($AllowedGroups.Count -gt 0) {
    New-ItemProperty -Path $key -Name "AllowedGroups" -PropertyType MultiString -Value $AllowedGroups -Force | Out-Null
    Write-Host "AllowedGroups: $($AllowedGroups -join ', ')"
} else {
    Remove-ItemProperty -Path $key -Name "AllowedGroups" -ErrorAction SilentlyContinue
    if ($AllowLocalAdmins) {
        Write-Host "AllowedGroups не задан — доступ только локальным администраторам машины (AllowLocalAdmins=1)."
    } else {
        Write-Host "AllowedGroups не задан, AllowLocalAdmins=0 — доступ разрешён любому сертификату домена (НЕ для продакшена)."
    }
}

Write-Host "Конфигурация записана в $key"
