<#
.SYNOPSIS
    Автоматический выпуск сертификата у лабораторного мини-CA (tools/labca).

.DESCRIPTION
    Ставит корневой CA в доверенные, генерирует ключ + PKCS#10 и получает подписанный
    сертификат у мини-CA по HTTP (аутентификация Windows: machine — учёткой компьютера,
    user — учёткой пользователя). Рассчитан на запуск из GPO:
    startup-скрипт (Kind=Machine, от SYSTEM) и logon-скрипт (Kind=User).

    Идемпотентен: если действующий сертификат уже есть, ничего не делает.

.EXAMPLE
    .\enroll-cert.ps1 -Kind Machine
    .\enroll-cert.ps1 -Kind User -Force
#>
param(
    [ValidateSet('Machine', 'User')][string]$Kind = 'Machine',
    # Пусто = автономный режим: ЦС на этой же машине (по её FQDN).
    [string]$CaUrl = '',
    [string]$EnrollUrl = '',
    [string]$CaSubject = 'CN=RemoteControl Lab CA',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$localFqdn = [System.Net.Dns]::GetHostEntry([System.Net.Dns]::GetHostName()).HostName
if (-not $CaUrl) { $CaUrl = "http://$localFqdn/" }
if (-not $EnrollUrl) { $EnrollUrl = "http://${localFqdn}:8555/" }
$CaUrl = $CaUrl.TrimEnd('/')
$EnrollUrl = $EnrollUrl.TrimEnd('/')

# ---------- журнал ----------
if ($Kind -eq 'Machine') {
    $logDir = 'C:\ProgramData\RemoteControl'
} else {
    $logDir = Join-Path $env:LOCALAPPDATA 'RemoteControl'
}
try { New-Item -ItemType Directory -Force -Path $logDir | Out-Null } catch { $logDir = $env:TEMP }
$logFile = Join-Path $logDir "enroll-$($Kind.ToLower()).log"

function Write-Log([string]$Message) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
}

function Open-CertStore([string]$Name, [string]$Location, [bool]$Write) {
    $flags = if ($Write) {
        [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
    } else {
        [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly
    }
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($Name, $Location)
    $store.Open($flags)
    return $store
}

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Идентификатор ключа из расширения: SKI (2.5.29.14, OCTET STRING) или AKI (2.5.29.35, [0] keyIdentifier).
function Get-KeyIdBytes($Extension) {
    if (-not $Extension) { return $null }
    $d = $Extension.RawData
    for ($i = 0; $i -lt $d.Length - 1; $i++) {
        if ($d[$i] -eq 0x04 -or $d[$i] -eq 0x80) {
            $len = [int]$d[$i + 1]
            if ($len -gt 0 -and ($i + 2 + $len) -le $d.Length) { return $d[($i + 2)..($i + 1 + $len)] }
        }
    }
    return $null
}

function Test-KeyIdEqual($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return $false }
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}

# Действующий сертификат, выпущенный ИМЕННО ЭТИМ CA, с нужным CN и EKU уже установлен?
# ЦС опознаём по ключу: у автономных мини-CA одинаковый subject (CN=RemoteControl Lab CA),
# поэтому сравнение строки Issuer давало ложное «уже есть».
function Test-ExistingCert([string]$Location, [string]$CnMatch, [string]$EkuOid, $Root) {
    $rootKeyId = Get-KeyIdBytes (($Root.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.14' } | Select-Object -First 1))
    $store = Open-CertStore 'My' $Location $false
    try {
        foreach ($c in $store.Certificates) {
            if (-not $c.HasPrivateKey) { continue }
            if ($c.NotAfter -lt (Get-Date).AddDays(30)) { continue }

            $certKeyId = Get-KeyIdBytes (($c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.35' } | Select-Object -First 1))
            if ($null -ne $rootKeyId) {
                if (-not (Test-KeyIdEqual $certKeyId $rootKeyId)) { continue }
            } elseif ($c.Issuer -ne $Root.Subject) {
                continue
            }

            $cn = $c.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
            if ($cn -ne $CnMatch) { continue }
            foreach ($ext in $c.Extensions) {
                if ($ext.Oid.Value -ne '2.5.29.37') { continue }
                $eku = New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($ext.RawData, $false)
                foreach ($oid in $eku.EnhancedKeyUsages) {
                    if ($oid.Value -eq $EkuOid) { return $true }
                }
            }
        }
    } finally { $store.Close() }
    return $false
}

function Test-RootPresent([string]$Location, [string]$Thumbprint) {
    $store = Open-CertStore 'Root' $Location $false
    try {
        foreach ($c in $store.Certificates) { if ($c.Thumbprint -eq $Thumbprint) { return $true } }
    } finally { $store.Close() }
    return $false
}

function Add-TrustedRoot([string]$Location, $Cert) {
    if (Test-RootPresent $Location $Cert.Thumbprint) { return }
    $store = Open-CertStore 'Root' $Location $true
    try {
        # На части систем Add в CurrentUser\Root отдаёт «The request is not supported»,
        # хотя сертификат фактически добавляется — поэтому проверяем результат, а не исключение.
        try {
            $store.Add($Cert)
        } catch {
            Write-Log "Add в $Location\Root: $($_.Exception.Message)"
        }
    } finally { $store.Close() }

    if (Test-RootPresent $Location $Cert.Thumbprint) { return }
    throw "не удалось добавить корневой сертификат в $Location\Root"
}

try {
    # ---------- 1. Кто мы ----------
    if ($Kind -eq 'Machine') {
        $fqdn = [System.Net.Dns]::GetHostEntry([System.Net.Dns]::GetHostName()).HostName
        $cn = $fqdn
        $eku = '1.3.6.1.5.5.7.3.1'          # Server Auth
        $storeLocation = 'LocalMachine'
        $upn = $null
        $names = New-Object System.Collections.Generic.List[string]
        $names.Add($fqdn)
        if ($fqdn -ne $env:COMPUTERNAME) { $names.Add($env:COMPUTERNAME) }
        try {
            Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
                Where-Object { $_.IPAddress -ne '127.0.0.1' } |
                ForEach-Object { $names.Add($_.IPAddress) }
        } catch { }
    } else {
        $cn = $env:USERNAME
        $eku = '1.3.6.1.5.5.7.3.2'          # Client Auth
        $storeLocation = 'CurrentUser'
        $names = $null
        $upn = $null
        try { $upn = (whoami /upn 2>$null | Select-Object -First 1) } catch { }
        if (-not $upn) {
            $domain = (Get-CimInstance Win32_ComputerSystem).Domain
            $upn = "$env:USERNAME@$domain"
        }
        $upn = "$upn".Trim()
    }

    Write-Log "start kind=$Kind cn=$cn ca=$CaUrl"

    # ---------- 2. Корневой CA в доверенные ----------
    $rootPath = Join-Path $env:TEMP 'rc-ca.cer'
    $root = $null
    for ($i = 1; $i -le 3 -and -not $root; $i++) {
        try {
            Invoke-WebRequest -Uri "$CaUrl/ca.cer" -OutFile $rootPath -UseBasicParsing -UseDefaultCredentials -TimeoutSec 30
            $root = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($rootPath)
        } catch {
            Write-Log "ca.cer попытка ${i}: $($_.Exception.Message)"
            Start-Sleep -Seconds 5
        }
    }
    if (-not $root) { throw 'не удалось получить корневой сертификат CA' }

    if ($Kind -eq 'Machine') {
        Add-TrustedRoot 'LocalMachine' $root
    } else {
        Add-TrustedRoot 'CurrentUser' $root
        if (Test-Admin) { Add-TrustedRoot 'LocalMachine' $root }
    }

    # ---------- 3. Уже выпущен этим CA? ----------
    if (-not $Force -and (Test-ExistingCert $storeLocation $cn $eku $root)) {
        Write-Log 'действующий сертификат уже есть — выход'
        exit 0
    }

    # ---------- 4. Ключ + PKCS#10 ----------
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
        "CN=$cn", $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    $san = New-Object System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
    if ($Kind -eq 'Machine') {
        foreach ($n in $names) {
            if ($n -match '^\d{1,3}(\.\d{1,3}){3}$') { $san.AddIpAddress([System.Net.IPAddress]::Parse($n)) }
            else { $san.AddDnsName($n) }
        }
        $keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor `
                    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
    } else {
        $san.AddUserPrincipalName($upn)
        $keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature
    }
    $req.CertificateExtensions.Add($san.Build())
    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension($keyUsage, $true)))
    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
        (New-Object System.Security.Cryptography.OidCollection), $false)))

    $csr = $req.CreateSigningRequest()

    # ---------- 4. Запрос в CA ----------
    $payload = @{
        kind  = $Kind.ToLower()
        csr   = [Convert]::ToBase64String($csr)
        names = @($names)
        upn   = $upn
    } | ConvertTo-Json -Compress -Depth 3

    # На старте машины сеть может быть ещё не готова — повторяем (кроме явного отказа 400/403).
    $response = $null
    for ($attempt = 1; $attempt -le 5 -and -not $response; $attempt++) {
        try {
            $response = Invoke-RestMethod -Uri "$($EnrollUrl.TrimEnd('/'))/enroll" -Method Post -ContentType 'application/json' `
                -Body $payload -UseDefaultCredentials -TimeoutSec 60
        } catch {
            $status = $null
            try { $status = [int]$_.Exception.Response.StatusCode } catch { }
            if ($status -eq 400 -or $status -eq 403) { throw "CA отказал ($status): $($_.Exception.Message)" }
            Write-Log "enroll попытка ${attempt}: $($_.Exception.Message)"
            Start-Sleep -Seconds 10
        }
    }
    if (-not $response) { throw 'CA недоступен (все попытки исчерпаны)' }
    if (-not $response.cert) { throw "CA отказал: $($response.error)" }
    $der = [Convert]::FromBase64String($response.cert)

    # ---------- 5. Установка ----------
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(, $der)
    # CopyWithPrivateKey/GetRSAPrivateKey — extension-методы; PowerShell 5.1 зовёт их только статически.
    $withKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($cert, $rsa)

    $pfxPath = Join-Path $env:TEMP ("rc-enroll-" + [Guid]::NewGuid().ToString('N') + ".pfx")
    $pfxPassword = [Guid]::NewGuid().ToString('N')
    [System.IO.File]::WriteAllBytes($pfxPath, $withKey.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pfxPassword))

    $keyFlags = if ($Kind -eq 'Machine') {
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
    } else {
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
    }
    $keyFlags = $keyFlags -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
    $imported = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $pfxPassword, $keyFlags)
    Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue

    $store = Open-CertStore 'My' $storeLocation $true
    try { $store.Add($imported) } finally { $store.Close() }

    # Машинный ключ должен быть доступен SYSTEM (агент работает от SYSTEM).
    if ($Kind -eq 'Machine') {
        try {
            $rsaKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($imported)
            if ($rsaKey -is [System.Security.Cryptography.RSACng] -and $rsaKey.Key.UniqueName) {
                $keyFile = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsaKey.Key.UniqueName)"
                if (Test-Path $keyFile) { & icacls.exe $keyFile /grant '*S-1-5-18:(F)' | Out-Null }
            }
        } catch { Write-Log "ACL: $($_.Exception.Message)" }
    }

    Write-Log "готово: thumbprint=$($imported.Thumbprint) notAfter=$($imported.NotAfter) store=$storeLocation\My"
    exit 0
} catch {
    Write-Log "ОШИБКА: $($_.Exception.Message)"
    exit 1
}
