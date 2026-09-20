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
    [bool]$AllowLocalAdmins = $true,
    # Разрешать доступ ЛЮБОМУ включённому пользователю домена (учётка есть в AD).
    # По умолчанию выключено: включать осознанно.
    [bool]$AllowDomainUsers = $false
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
New-ItemProperty -Path $key -Name "AllowDomainUsers" -PropertyType DWord -Value ([int]$AllowDomainUsers) -Force | Out-Null
Write-Host "AllowLocalAdmins: $AllowLocalAdmins"
Write-Host "AllowDomainUsers: $AllowDomainUsers"

if ($AllowedGroups.Count -gt 0) {
    New-ItemProperty -Path $key -Name "AllowedGroups" -PropertyType MultiString -Value $AllowedGroups -Force | Out-Null
    Write-Host "AllowedGroups: $($AllowedGroups -join ', ')"
} else {
    Remove-ItemProperty -Path $key -Name "AllowedGroups" -ErrorAction SilentlyContinue
    if ($AllowDomainUsers) {
        Write-Host "AllowedGroups не задан, AllowDomainUsers=1 — доступ разрешён любому доменному пользователю."
    } elseif ($AllowLocalAdmins) {
        Write-Host "AllowedGroups не задан — доступ только локальным администраторам машины (AllowLocalAdmins=1)."
    } else {
        Write-Host "Все правила выключены — доступ разрешён любому сертификату домена (НЕ для продакшена)."
    }
}

Write-Host "Конфигурация записана в $key"
