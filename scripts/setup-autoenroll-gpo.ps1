<#
.SYNOPSIS
    Создаёт GPO автоенроллмента: startup-скрипт (машины) и logon-скрипт (пользователи),
    которые вызывают scripts\enroll-cert.ps1 у мини-CA (tools/labca).

.DESCRIPTION
    Скрипты кладутся в SYSVOL GPO и выполняются:
      * Machine\Scripts\Startup\enroll-machine.cmd -> enroll-cert.ps1 -Kind Machine (SYSTEM, при загрузке)
      * User\Scripts\Logon\enroll-user.cmd         -> enroll-cert.ps1 -Kind User   (при входе)
    enroll-cert.ps1 идемпотентен, поэтому повторные запуски ничего не делают.

    ВАЖНО (иначе GPO не применяется):
      * в gpt.ini И в атрибутах AD-объекта GPO (gPCMachineExtensionNames/gPCUserExtensionNames)
        должна стоять пара GUID Scripts CSE + tool-extension:
            {42B5FAAE-6536-11D2-AE5A-0000F87571E3}{42B5FAAE-6536-11D1-AE54-0000F80367C1}
        Клиент читает extension names из AD, а не из gpt.ini;
      * нужно увеличивать versionNumber у AD-объекта GPO (New-GPO оставляет 0/1).

.EXAMPLE
    .\setup-autoenroll-gpo.ps1
#>
param(
    [string]$GpoName = 'RemoteControl Autoenrollment',
    [string]$EnrollScript = (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\enroll-cert.ps1'),
    [string]$CaUrl = 'http://dc1.corp.local/',
    [string]$EnrollUrl = 'http://dc1.corp.local:8555/'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $EnrollScript)) { throw "не найден enroll-cert.ps1: $EnrollScript" }
Import-Module GroupPolicy -ErrorAction Stop

$domainDn = (Get-ADDomain).DistinguishedName
$dnsDomain = (Get-ADDomain).DNSRoot

$gpo = Get-GPO -Name $GpoName -ErrorAction SilentlyContinue
if (-not $gpo) { $gpo = New-GPO -Name $GpoName -Comment 'Автовыпуск сертификатов RemoteControl у мини-CA' }
Write-Host "GPO: $($gpo.DisplayName) {$($gpo.Id)}"

# Scripts CSE (gpscript.dll) + tool-extension GUID (пара для gPC*ExtensionNames)
$cse = '{42B5FAAE-6536-11D2-AE5A-0000F87571E3}'
$tool = '{42B5FAAE-6536-11D1-AE54-0000F80367C1}'
$extPair = "[$cse$tool]"

$gpoPath = "\\$dnsDomain\SYSVOL\$dnsDomain\Policies\{$($gpo.Id)}"
$machineStartup = Join-Path $gpoPath 'Machine\Scripts\Startup'
$userLogon = Join-Path $gpoPath 'User\Scripts\Logon'
New-Item -ItemType Directory -Force -Path $machineStartup, $userLogon | Out-Null

$machineScript = Join-Path $machineStartup 'enroll-cert.ps1'
$userScript = Join-Path $userLogon 'enroll-cert.ps1'
Copy-Item $EnrollScript $machineScript -Force
Copy-Item $EnrollScript $userScript -Force

$machineCmd = @"
@echo off
if not exist C:\ProgramData\RemoteControl mkdir C:\ProgramData\RemoteControl
echo %DATE% %TIME% machine cmd started >> C:\ProgramData\RemoteControl\gpo-machine-cmd.log
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$machineScript" -Kind Machine -CaUrl $CaUrl -EnrollUrl $EnrollUrl >> C:\ProgramData\RemoteControl\gpo-machine-cmd.log 2>&1
echo %DATE% %TIME% exit=%ERRORLEVEL% >> C:\ProgramData\RemoteControl\gpo-machine-cmd.log
"@
Set-Content -Path (Join-Path $machineStartup 'enroll-machine.cmd') -Value $machineCmd -Encoding ASCII

$userCmd = @"
@echo off
if not exist C:\ProgramData\RemoteControl mkdir C:\ProgramData\RemoteControl
echo %DATE% %TIME% user cmd started >> C:\ProgramData\RemoteControl\gpo-user-cmd.log
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$userScript" -Kind User -CaUrl $CaUrl -EnrollUrl $EnrollUrl >> C:\ProgramData\RemoteControl\gpo-user-cmd.log 2>&1
echo %DATE% %TIME% exit=%ERRORLEVEL% >> C:\ProgramData\RemoteControl\gpo-user-cmd.log
"@
Set-Content -Path (Join-Path $userLogon 'enroll-user.cmd') -Value $userCmd -Encoding ASCII

Set-Content -Path (Join-Path $gpoPath 'Machine\Scripts\scripts.ini') `
    -Value "[Startup]`r`n0CmdLine=enroll-machine.cmd`r`n0Parameters=`r`n" -Encoding ASCII
Set-Content -Path (Join-Path $gpoPath 'User\Scripts\scripts.ini') `
    -Value "[Logon]`r`n0CmdLine=enroll-user.cmd`r`n0Parameters=`r`n" -Encoding ASCII

# Новая версия GPO: и в gpt.ini, и в AD-объекте.
$gpoDn = "CN={$($gpo.Id)},CN=Policies,CN=System,$domainDn"
$adObject = [ADSI]"LDAP://$gpoDn"
$version = [int]$adObject.versionNumber
if ($version -lt 1) { $version = 1 } else { $version++ }

Set-Content -Path (Join-Path $gpoPath 'gpt.ini') -Encoding ASCII -Value (
    "[General]`r`ngPCFunctionalityVersion=2`r`nVersion=$version`r`n" +
    "gPCMachineExtensionNames=$extPair`r`n" +
    "gPCUserExtensionNames=$extPair`r`n")

$adObject.Properties['gPCMachineExtensionNames'].Clear()
$adObject.Properties['gPCMachineExtensionNames'].Add($extPair)
$adObject.Properties['gPCUserExtensionNames'].Clear()
$adObject.Properties['gPCUserExtensionNames'].Add($extPair)
$adObject.versionNumber = $version
$adObject.SetInfo()

$linked = (Get-GPInheritance -Target $domainDn).GpoLinks | Where-Object { $_.GpoId -eq $gpo.Id }
if (-not $linked) {
    New-GPLink -Name $GpoName -Target $domainDn -LinkEnabled Yes | Out-Null
    Write-Host 'GPO привязана к домену'
} else {
    Write-Host 'GPO уже привязана к домену'
}

Write-Host "versionNumber = $version"
Write-Host 'Готово. На клиенте: перезагрузка (startup-скрипт выполняется при загрузке).'
