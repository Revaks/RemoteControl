# Установка RemoteControl

Пошаговая инструкция: как собрать единый установщик и развернуть удалённый доступ на сервере.
Краткое описание продукта — в [README.md](../README.md), лабораторный стенд — в [ENVIRONMENT.md](../ENVIRONMENT.md).

---

## 0. Что и куда ставится

Единый пакет `RemoteControl.msi` содержит **и агент (серверную часть), и консоль оператора**,
и полностью самодостаточен: на сервере он поднимает ещё и собственный мини-CA, который сам
выпускает машинный сертификат агенту. Ручных шагов после `msiexec` не требуется.

```
                    ┌──────────────────────── сервер, куда ставим MSI ────────────────────────┐
                    │  RemoteControlAgent.exe (служба SYSTEM, :5900, mTLS)                      │
                    │  labca.exe (мини-CA: :80 корень/CRL, :8555 выпуск)                       │
                    │  машинный сертификат CN=<FQDN> в LocalMachine\My                         │
                    │  консоль оператора + ярлыки                                              │
                    └──────────────────────────────────────────────────────────────────────────┘
                                            ▲
                                            │ mTLS + RFB
                                            ▼
                    ┌──────────────────── рабочая станция оператора ─────────────────────┐
                    │  консоль (RemoteControl.Viewer.exe)                                │
                    │  клиентский сертификат <user>@<domain>, выпущенный ЦС сервера      │
                    └────────────────────────────────────────────────────────────────────┘
```

Возможны два режима (подробнее — §7):

| Режим | Как получаются сертификаты | Когда удобен |
|---|---|---|
| **Автономный** (по умолчанию в этом MSI) | каждый сервер — свой мини-CA, сам себе выпускает сертификат | поставить MSI на сервер и забыть; нет зависимости от другого сервера |
| **Центральный ЦС** | один мини-CA (или настоящий AD CS) + GPO-автоенроллмент на все машины | много машин, сертификаты операторов должны быть взаимозаменяемы |

---

## 1. Требования

### 1.1. Целевой сервер

| | |
|---|---|
| ОС | Windows Server 2019+ / Windows 10+ (x64) |
| Права | локальный администратор (MSI ставит службу и задачи от SYSTEM) |
| Свободно | ~200 МБ на диске |
| Порты | **80** и **8555** — мини-CA; **5900** — агент. Порт 80 должен быть **свободен** (IIS/WSUS его занимают — см. §8) |
| Домен | машина в домене AD (нужна для авторизации по UPN/группам) |

### 1.2. Сборочная машина (если собираете MSI сами)

| Компонент | Версия / как поставить |
|---|---|
| Visual Studio 2022 (C++ workload) или Build Tools | для агента |
| CMake | ≥ 3.20 |
| .NET SDK | 8.x |
| WiX Toolset | **v5** (v6 требует платной подписки) |

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.Firewall.wixext/5.0.2
```

> На стенде .NET лежит в `C:\dotnet` без записи в реестр — тогда обязательно
> `$env:DOTNET_ROOT='C:\dotnet'`, иначе apphost WiX не запускается.
> `build-single-msi.ps1` выставляет это сам, если каталог `C:\dotnet` существует.

Сборке агента нужен интернет (CMake `FetchContent` тянет LibVNCServer 0.9.14) либо локальный
исходник: `-LibVncDir <путь>`.

---

## 2. Сборка единого MSI

Готовый установщик собирать не обязательно — он приложен к релизам:
<https://github.com/Revaks/RemoteControl/releases/latest> (`RemoteControl.msi`).
Релиз собирается автоматически по тегу `v*` (workflow `.github/workflows/release.yml`).
Ниже — как собрать самому.

### 2.1. Агент (C++, CMake)

```powershell
powershell -File scripts\build-agent.ps1
# build\agent\Release\RemoteControlAgent.exe
```

Либо вручную:

```powershell
cmake -S src\Agent -B build\agent -A x64
cmake --build build\agent --config Release
```

### 2.2. Единый установщик

```powershell
$env:DOTNET_ROOT='C:\dotnet'          # если .NET не зарегистрирован в реестре
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-single-msi.ps1
```

Результат: **`dist\msi\RemoteControl.msi`** (~69 МБ).

Скрипт по шагам:

1. `dotnet publish tools\labca` → self-contained для `win-x64` в `installer\ca\payload`;
2. `dotnet publish src\Viewer\RemoteControl.Viewer` → self-contained в `installer\viewer\payload`;
3. копирует `RemoteControlAgent.exe` и `scripts\enroll-cert.ps1` в `installer\agent`;
4. `wix build installer\RemoteControl.wxs -arch x64 -ext WixToolset.Firewall.wixext`.

Параметры: `-AgentExe <путь>` (если агент собран в нестандартное место), `-Configuration`,
`-OutputDir`.

### 2.3. Отдельные MSI (необязательно)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-msi.ps1
# dist\msi\RemoteControlAgent.msi, dist\msi\RemoteControlViewer.msi
```

Дают то же самое, но **без** ЦС и автоенроллмента — сертификаты придётся выпускать самому
(§7, §10).

---

## 3. Установка на сервер

### 3.1. Команда

```powershell
msiexec /i dist\msi\RemoteControl.msi /qn /l*v C:\Setup\rc-msi.log
```

Интерактивно — просто двойной клик по `RemoteControl.msi` (тогда виден прогресс и ход установки).

Установка занимает ~40 секунд: часть времени MSI ждёт, пока поднимется ЦС и выпустится
сертификат.

### 3.2. Что MSI делает по шагам

| Шаг | Действие |
|---|---|
| 1 | копирует файлы в `C:\Program Files\RemoteControl` (агент, `ca\`, скрипты) и `C:\Program Files\Remote Control Viewer` |
| 2 | регистрирует службу `RemoteControlAgent` (LocalSystem, автостарт) и брандмауэр TCP 5900 |
| 3 | брандмауэр TCP 80 и 8555 |
| 4 | пишет параметры агента в реестр (§3.4) |
| 5 | `netsh http add urlacl` для `http://+:80/` и `http://+:8555/` |
| 6 | создаёт задачи `RemoteControlLabCa` и `RemoteControlEnrollMachine` (SYSTEM, at startup) |
| 7 | запускает мини-CA и **сразу выпускает машинный сертификат** |
| 8 | стартует службу агента |

Всё это делает `agent-setup.cmd`, который MSI запускает от SYSTEM сразу после копирования
файлов и **до** старта службы. Журнал: `C:\ProgramData\RemoteControl\msi-setup.log`.

### 3.3. Что где лежит после установки

| Что | Путь |
|---|---|
| Агент | `C:\Program Files\RemoteControl\RemoteControlAgent.exe` |
| Мини-CA | `C:\Program Files\RemoteControl\ca\labca.exe` |
| Скрипты | `...\RemoteControl\enroll-cert.ps1`, `enroll-user.cmd`, `agent-setup.cmd`, `agent-cleanup.cmd` |
| Консоль | `C:\Program Files\Remote Control Viewer\RemoteControl.Viewer.exe` |
| Ярлыки | Start Menu → `Remote Control\`: «Remote Control Viewer», «Получить сертификат оператора» |
| Журнал установки | `C:\ProgramData\RemoteControl\msi-setup.log` |
| Журнал выпуска сертификата | `C:\ProgramData\RemoteControl\enroll-machine.log` |
| Журнал ЦС | `C:\ProgramData\RemoteControl\labca.log` |
| События агента | журнал Application, источник `RemoteControlAgent` |

Задачи планировщика:

- `RemoteControlLabCa` — запускает `labca.exe` при загрузке (SYSTEM, highest);
- `RemoteControlEnrollMachine` — при загрузке проверяет/выпускает машинный сертификат (идемпотентно;
  если сертификат уже есть, пишет в лог «сертификат уже есть в хранилище»).

Сам сертификат ЦС (`CN=RemoteControl Lab CA`) лежит в `LocalMachine\My` вместе с ключом и
**переживает перезапуски** (ключ в machine key set); корень — в `LocalMachine\Root`.

### 3.4. Параметры агента (реестр)

`HKLM\SYSTEM\CurrentControlSet\Services\RemoteControlAgent\Parameters`:

| Значение | Тип | По умолчанию в MSI | Смысл |
|---|---|---|---|
| `ListenPort` | DWORD | 5900 | порт агента |
| `RequireClientCert` | DWORD | 1 | требовать клиентский сертификат |
| `AllowLocalAdmins` | DWORD | 1 | разрешать локальным администраторам машины |
| `AllowDomainUsers` | DWORD | 1 | разрешать **любому** доменному пользователю |
| `AllowedGroups` | MULTI_SZ | — | DN групп AD, которым разрешён доступ |

Правила складываются по «ИЛИ»; отключённые в AD учётки не допускаются (событие `1216`).

Чтобы сузить доступ (рекомендуется для продакшена) — после установки:

```powershell
# только группа операторов, без «любого доменного пользователя»
.\scripts\configure-agent.ps1 `
  -AllowedGroups @("CN=Remote Control Operators,OU=Groups,DC=corp,DC=local") `
  -AllowDomainUsers $false
Restart-Service RemoteControlAgent
```

### 3.5. Чего MSI НЕ делает

- не выпускает **пользовательские** сертификаты автоматически (оператор берёт их сам, §5.2);
- не создаёт группу AD и не настраивает её состав;
- не меняет порты ЦС (если 80/8555 заняты — §8);
- не удаляет свой CA-сертификат при удалении пакета (§6).

---

## 4. Проверка после установки

```powershell
# 1) служба агента
sc.exe query RemoteControlAgent | Select-String STATE      # ожидаем STATE : 4  RUNNING

# 2) мини-CA отвечает
(Invoke-WebRequest http://localhost/health -UseBasicParsing).Content   # ok

# 3) задачи
schtasks /query /tn RemoteControlLabCa        /fo list | Select-String 'Status|Task To Run'
schtasks /query /tn RemoteControlEnrollMachine /fo list | Select-String 'Last Result'

# 4) машинный сертификат выпущен локальным ЦС
Get-ChildItem Cert:\LocalMachine\My |
  Where-Object { $_.Subject -like "CN=$env:COMPUTERNAME*" } |
  Select-Object Subject, Issuer, Thumbprint | Format-List

# 5) цепочка и отзыв проверяются
$c = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "CN=$env:COMPUTERNAME*" } | Select-Object -First 1
$ch = New-Object System.Security.Cryptography.X509Certificates.X509Chain
$ch.ChainPolicy.RevocationMode = 'Online'; $ch.Build($c)     # ожидаем True
```

Ожидаемый результат (проверено на стенде, MSI 1.2.2.0):

```
Subject    : CN=WIN11HOST.corp.local
Issuer     : CN=RemoteControl Lab CA
```

Пример строки из `enroll-machine.log`:

```
2026-09-20 18:59:04 готово: thumbprint=2E58... notAfter=09/20/2028 18:59:03 store=LocalMachine\My
```

---

## 5. Рабочая станция оператора

### 5.1. Консоль

Агент на рабочей станции не нужен, поэтому ставим только функцию консоли:

```powershell
msiexec /i RemoteControl.msi ADDLOCAL=Viewer /qn
```

(`ADDLOCAL=Agent` — наоборот, только агентская часть. Без параметра ставится всё.)

### 5.2. Клиентский сертификат у ЦС того сервера

Сертификат нужно взять у **того** сервера, к которому будет подключение: именно его ЦС
проверит подпись. Скрипт заодно положит корень этого ЦС в доверенные.

```powershell
& 'C:\Program Files\RemoteControl\enroll-user.cmd' `
    -CaUrl    http://srv01.corp.local/ `
    -EnrollUrl http://srv01.corp.local:8555/
```

Либо ярлык **«Получить сертификат оператора»** — но тогда он выпустит сертификат у **локального**
ЦС (годится, если MSI поставлен на самом сервере).

Сертификат кладётся в `CurrentUser\My` (EKU Client Auth, UPN в SAN). Проверка:

```powershell
Get-ChildItem Cert:\CurrentUser\My |
  Where-Object { $_.Issuer -like '*RemoteControl Lab CA*' } |
  Select-Object Subject, Issuer, Thumbprint | Format-List
```

### 5.3. Настройка консоли

`RemoteControl.Viewer.dll.config` рядом с `RemoteControl.Viewer.exe`:

| Ключ | Значение |
|---|---|
| `AgentPort` | порт агента (5900) |
| `InternalRootCaThumbprint` | SHA-256 отпечаток корня **этого** сервера (pinning; пусто = доверять любому доверенному корню) |
| `AgentExePath`, `PushAgentByDefault` | для push-режима (см. README) |

Отпечаток корня сервера:

```powershell
(Invoke-WebRequest http://srv01.corp.local/ca.cer -OutFile ca.cer)
(New-Object System.Security.Cryptography.X509Certificates.X509Certificate2('ca.cer')).Thumbprint
```

### 5.4. Подключение

1. Запустить `RemoteControl.Viewer.exe`.
2. Выбрать машину в списке AD или ввести имя/IP.
3. «Подключить».

Агенту нужен клиентский сертификат, подписанный доверенным ему ЦС, с UPN включённой учётки AD —
события `1402` (подключение) и `1204`/`1206`/`1215` (по какому правилу пущен).

---

## 6. Обновление и удаление

```powershell
# обновление: просто поставить MSI более новой версии
msiexec /i RemoteControl.msi /qn /l*v C:\Setup\rc-msi-upgrade.log

# удаление (путь к MSI или через «Программы и компоненты», запись Remote Control)
msiexec /x RemoteControl.msi /qn /l*v C:\Setup\rc-msi-uninstall.log
```

При удалении `agent-cleanup.cmd` снимает задачи `RemoteControlLabCa` и
`RemoteControlEnrollMachine` и останавливает ЦС (иначе занятый `labca.exe` мешал бы смене версии).

Сертификаты MSI не трогает — если нужно вычистить полностью:

```powershell
# корень ЦС и сам CA-сертификат (с приватным ключом)
$ca = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq 'CN=RemoteControl Lab CA' }
foreach ($c in $ca) {
    Get-ChildItem Cert:\LocalMachine\Root |
        Where-Object { $_.Thumbprint -eq $c.Thumbprint } | Remove-Item -Force
    Remove-Item $c.PSPath -Force
}
# плюс при необходимости: выпущенные машинные сертификаты
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "CN=$env:COMPUTERNAME*" } | Remove-Item -Force
```

---

## 7. Вариант с центральным ЦС (вместо автономного)

Если сертификаты должны быть взаимозаменяемы между машинами (или уже есть AD CS), вместо
автономного ЦС на каждом сервере разворачивают один:

1. поднять мини-CA на выделенной машине (или использовать AD CS с шаблонами Computer/User);
2. на всех машинах раздать машинные и пользовательские сертификаты — например GPO из
   `scripts\setup-autoenroll-gpo.ps1` (startup-скрипт для машин, logon — для пользователей);
3. на целевых серверах ставить только агентскую часть и указывать в `enroll-cert.ps1` адрес
   общего ЦС (`-CaUrl http://ca.corp.local/ -EnrollUrl http://ca.corp.local:8555/`).

Детали и грабли GPO-скриптов — ENVIRONMENT.md §5.1.

---

## 8. Диагностика

| Симптом | Причина | Решение |
|---|---|---|
| Нет `C:\ProgramData\RemoteControl\msi-setup.log` | кастомное действие не выполнилось (например, пакет уже стоял) | удалить пакет и установить заново; смотреть `rc-msi.log` (искать `RunAgentSetup`) |
| Служба агента есть, но подключения отклоняются, в событиях `1400`/`1102` | нет машинного сертификата | `schtasks /run /tn RemoteControlEnrollMachine`, затем `enroll-machine.log` |
| В `enroll-machine.log` `CA отклонил (403)` | ЦС не признал вызывающего: машинный запрос должен идти от учётки компьютера или локального SYSTEM, пользовательский — от самого пользователя | запускать задачей от SYSTEM; для пользователя — от его имени |
| `/health` не отвечает | порт 80/8555 занят (IIS, WSUS, другой сервис) или нет urlacl | `netstat -ano \| findstr ':80 '`, `netsh http show urlacl`; освободить порт либо поменять `LABCA_PREFIX`/`LABCA_ENROLL_PREFIX` в задаче и правила брандмауэра |
| В `labca.log` ошибка запуска HTTPS-прослушивателя | нет прав/urlacl у SYSTEM | `netsh http add urlacl url=http://+:80/ user=SYSTEM` (и для 8555) |
| Консоль: цепочка сертификата сервера не проходит | корень ЦС сервера не в доверенных | выпустить сертификат оператора с `-CaUrl http://<сервер>/` (скрипт добавит корень) |
| Консоль: ошибка проверки отзыва | недоступен `http://<сервер>/ca.crl` или устаревший кэш | открыть CRL в браузере, проверить брандмауэр, `certutil -urlcache * delete` |
| Агент предъявляет не тот сертификат | в `LocalMachine\My` несколько сертификатов с EKU Server Auth (агент берёт **первый**) | убрать лишние или перевыпустить сертификат агенту |
| Доступ запрещён (событие `1205`) | пользователь не попал ни под одно правило | проверить `AllowedGroups`/`AllowDomainUsers`/`AllowLocalAdmins`, включённость учётки в AD |

Полезные журналы: `C:\ProgramData\RemoteControl\{msi-setup,enroll-machine,labca}.log`,
журнал Application (источник `RemoteControlAgent`), MSI-лог `rc-msi.log`.

---

## 9. Безопасность

- MSI по умолчанию ставит `AllowDomainUsers=1`: **любой** пользователь домена сможет подключиться
  к серверу (включая защищённый стол). Для продакшена сузить до `AllowedGroups` (§3.4).
- Автономный режим — «сервер сам себе ЦС»: его корень доверенный только на нём; каждый сервер
  выпускает сертификаты независимо, сертификаты разных серверов не взаимозаменяемы.
- ЦС выпускает сертификат вызывающему только на его собственное имя (машина — `HOST$`/локальный
  SYSTEM, пользователь — свой UPN), но **любой доменный пользователь** может получить клиентский
  сертификат — это осознанное упрощение лабораторного мини-CA, в продакшене нужен AD CS с
  approval/шаблонами.
- Приватные ключи и сертификаты не коммитятся (`.gitignore`).

---

## 10. Приложение: установка без MSI

Если MSI использовать нельзя, те же шаги вручную (все команды — на целевой машине от администратора):

```powershell
# 1) агент: скопировать exe, создать службу
Copy-Item RemoteControlAgent.exe 'C:\Program Files\RemoteControl\' -Force
sc.exe create RemoteControlAgent binPath= '"C:\Program Files\RemoteControl\RemoteControlAgent.exe"' `
    start= auto obj= LocalSystem DisplayName= "Remote Control Agent"
sc.exe start RemoteControlAgent

# 2) правила доступа
.\scripts\configure-agent.ps1 -AllowedGroups @("CN=Remote Control Operators,OU=Groups,DC=corp,DC=local")

# 3) машинный сертификат (у нужного ЦС)
.\scripts\enroll-cert.ps1 -Kind Machine -CaUrl http://srv01.corp.local/ -EnrollUrl http://srv01.corp.local:8555/

# 4) брандмауэр
New-NetFirewallRule -DisplayName 'RemoteControl Agent (TCP 5900)' -Direction Inbound -Protocol TCP -LocalPort 5900 -Action Allow
```

Консоль — `dotnet publish` (README, §«Сборка»), запускается без установки.
