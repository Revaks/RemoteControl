# RemoteControl — лабораторный стенд (Windows Server 2022 + DC)

Дата актуальности: 20.09.2026. Описывает, что поднято, как устроено и как перезапустить после перерыва.

---

## 1. Состав стенда

| Компонент | Значение |
|---|---|
| Гипервизор | KVM/libvirt (qemu:///system) |
| VM | `win-dc`, Windows Server 2022 Standard Evaluation (Desktop Experience) |
| Домен | `corp.local` (DC = `dc1.corp.local`) |
| IP VM | `192.168.122.10` |
| Вторая VM | `win11-host`, Windows 11 Pro 25H2 (русская), в домене `corp.local` |
| IP второй VM | `192.168.122.20` |
| Хост | `192.168.122.1` (libvirt NAT) |
| Агент | служба `RemoteControlAgent`, порт `5900`, mTLS + авторизация по AD-группе |
| Viewer | WPF `.NET 8`, self-contained сборка на VM |

Учётные данные:

- Локальный/доменный администратор: `CORP\Administrator` / `LabAdmin!2026`
- Тестовый пользователь (член AD-группы `Remote Control Operators`): `rctest@corp.local` / пароль `LabAdmin!2026`
- Локальный администратор без AD-группы операторов: `CORP\locadmin` / пароль `RcLab#2026!Secure`
  (входит в `BUILTIN\Administrators`, но не в `Remote Control Operators` — на нём проверяется правило `AllowLocalAdmins`)
- Клиентские сертификаты `rctest` и `locadmin` лежат в `CurrentUser\My` у пользователя Administrator на VM
  (и в `C:\Setup\client-rctest.pfx`, `C:\Setup\client-locadmin.pfx`; пароль контейнера — `LabAdmin!2026`)

Вторая VM (`win11-host`):

- Windows 11 Pro 25H2 RU, компьютер `WIN11HOST`, введена в домен `corp.local`
- IP `192.168.122.20` (статический), DNS `192.168.122.10`
- Локальный администратор: встроенная учётная запись `Администратор` / `LabAdmin!2026` (SAM-имя локализовано — это `WIN11HOST\Администратор`, не `Administrator`)
- После ввода в домен основной вход: `CORP\Administrator` / `LabAdmin!2026` (автологин настроен на него)
- Открыты/работают: SSH (22), WinRM (5985), RDP (3389), файрвол-правило под агента `RemoteControlAgent-5900`
- Для push-режима с сервера включены входящие правила файрвола: `FPS-SMB-In-TCP*` (SMB 445, `admin$`) и `RemoteSvcAdmin-*` (SCM/RPC, удалённое управление службами)
- Автологин под `Administrator` оставлен включённым (для лаборатории: консоль должна быть разблокирована, иначе BitBlt в агенте вернёт ACCESS_DENIED)
- Первичная настройка: `C:\Setup\stage1.ps1` (запускается из автозагрузки, идемпотентный, флаг `C:\Setup\win11-joined.flag`)

---

## 2. Как зайти / посмотреть на VM

```bash
# SSH (работает)
sshpass -p 'LabAdmin!2026' ssh -o StrictHostKeyChecking=no Administrator@192.168.122.10

# Вторая VM (Windows 11, домен)
sshpass -p 'LabAdmin!2026' ssh -o StrictHostKeyChecking=no Administrator@192.168.122.20

# GUI: virt-viewer (установлен)
virt-viewer -c qemu:///system win-dc
virt-viewer -c qemu:///system win11-host

# или напрямую VNC
remote-viewer vnc://127.0.0.1:5900

# Скриншот без GUI
virsh -c qemu:///system screenshot win-dc /tmp/screen.ppm
```

Освободить курсор из окна virt-viewer: `Ctrl+Alt`. Ctrl+Alt+Del в VM: меню virt-viewer `Send Key` → `Ctrl+Alt+Del`.

---

## 3. Сеть и CRL

- Хост раздаёт CRL для проверки отзыва сертификатов: `http://192.168.122.1:8080/lab-root3.crl` (текущий URL; старые `lab-root.crl`, `lab-root2.crl` не использовать — закэшированы CryptnetUrlCache)
- Сервер: `python3 -m http.server 8080` в каталоге `/tmp/crl-server` (файл `lab-root3.crl`).
- Если CRL-сервер не запущен, .NET viewer будет падать с `RevocationStatusUnknown`.

Запустить CRL-сервер заново:

```bash
mkdir -p /tmp/crl-server
# положить свежий lab-root.crl из VM в /tmp/crl-server/lab-root3.crl
cd /tmp/crl-server && python3 -m http.server 8080 --bind 0.0.0.0
```

---

## 4. Active Directory

- Домен: `DC=corp,DC=local`
- OU: `OU=Groups`
- Группа: `CN=Remote Control Operators,OU=Groups,DC=corp,DC=local`
- Пользователь: `rctest` (член группы Remote Control Operators)
- Агент при mTLS берёт UPN из SAN клиентского сертификата (`CertGetNameStringW`) и проверяет членство в группе через LDAP (`defaultNamingContext` из RootDSE).

---

## 5. PKI (лабораторные сертификаты)

Инструмент генерации: `C:\Setup\labcerts\` на VM (копия на хосте: `/tmp/labcerts/`).

Что генерирует и куда кладёт:

| Сертификат | Куда устанавливается |
|---|---|
| Root CA `CN=Lab Root CA` | `LocalMachine\Root` (на win-dc и на win11-host) |
| Машинный `CN=dc1.corp.local` (EKU Server Auth, SAN DNS+IP, CDP) | `LocalMachine\My` + SYSTEM ACL на ключевой файл CNG (win-dc) |
| Машинный `CN=WIN11HOST.corp.local` (EKU Server Auth, SAN DNS+IP, CDP) | экспортируется в `C:\Setup\machine-win11host.pfx`; на win11-host импортирован в `LocalMachine\My` + SYSTEM ACL |
| Клиентский `CN=rctest` (EKU Client Auth, UPN SAN `rctest@corp.local`, CDP) | `CurrentUser\My` (win-dc) |

Файлы на VM:

- `C:\Setup\lab-root.crl` — CRL (текущий URL: `lab-root3.crl`)
- `C:\Setup\lab-root.cer` — корневой (DER)
- `C:\Setup\client-rctest.pfx` — клиентский PFX (пароль `LabAdmin!2026`)
- `C:\Setup\machine.pfx` — машинный PFX (dc1)
- `C:\Setup\machine-win11host.pfx` — машинный PFX (WIN11HOST)

Перевыпустить всё (после этого обновить CRL на хосте и перезапустить службу):

```powershell
dotnet run --project C:\Setup\labcerts\labcerts.csproj -c Release
# затем: scp C:\Setup\lab-root.crl на хост в /tmp/crl-server/lab-root3.crl
# и переимпортировать machine-win11host.pfx + lab-root.cer на win11-host
sc.exe stop RemoteControlAgent; sc.exe start RemoteControlAgent
```

Важные детали реализации (уже исправлено в коде):

- CDP кодируется вручную как `30 { 30 { A0 { A0 { 86 url } } } }`
- UPN SAN: `A0 { OID 1.3.6.1.4.1.311.20.2.3, A0 { UTF8String } }` — **без** внутреннего SEQUENCE
- CRL: `CertificateRevocationListBuilder.Build(issuer, generator, crlNumber, nextUpdate, hashAlgorithm, aki, thisUpdate)` — обрати внимание на порядок аргументов
- CRL URL вынесен в отдельный файл `lab-root3.crl`, чтобы не попадать в кэш CryptnetUrlCache от старых CRL

---

## 5.1. Мини-CA и автоенроллмент (`tools/labca`)

Дополнительно к `labcerts` (ручной стенд) в репозитории есть мини-CA для автоматического выпуска
сертификатов: `tools/labca` (ЦС), `scripts/enroll-cert.ps1` (клиент), `scripts/setup-autoenroll-gpo.ps1` (GPO).

Развёртывание ЦС на DC1:

```powershell
& C:\dotnet\dotnet.exe build C:\Projects\RemoteControl\tools\labca\labca.csproj -c Release -o C:\Projects\RemoteControl\tools\labca\out
netsh http add urlacl url=http://+:80/ user=SYSTEM
netsh http add urlacl url=http://+:8555/ user=SYSTEM
schtasks /create /tn RemoteControlLabCa /tr "C:\dotnet\dotnet.exe C:\Projects\RemoteControl\tools\labca\out\labca.dll" /sc onstart /ru SYSTEM /rl HIGHEST /f
schtasks /run /tn RemoteControlLabCa
New-NetFirewallRule -DisplayName 'RemoteControl Lab CA (TCP 80)'   -Direction Inbound -Protocol TCP -LocalPort 80   -Action Allow
New-NetFirewallRule -DisplayName 'RemoteControl Lab CA (TCP 8555)' -Direction Inbound -Protocol TCP -LocalPort 8555 -Action Allow
```

Корень: `CN=RemoteControl Lab CA`; журнал — `C:\Setup\labca\labca.log`. Эндпоинты:
`http://dc1.corp.local/ca.cer`, `/ca.crl` — анонимно (CRL обязан быть без аутентификации, иначе
проверка отзыва не работает), `http://dc1.corp.local:8555/enroll` — Negotiate/NTLM + подпись PKCS#10.
Автоенроллмент: `scripts/setup-autoenroll-gpo.ps1` (startup-скрипт для машин, logon — для пользователей).

**Грабли GPO-скриптов (на которые наступили):**
- клиент берёт extension names **из атрибутов AD** `gPCMachineExtensionNames`/`gPCUserExtensionNames`
  объекта GPO, а не из gpt.ini; без них Scripts CSE вообще не запускается;
- правильная пара GUID для Scripts: CSE `{42B5FAAE-6536-11D2-AE5A-0000F87571E3}` (`gpscript.dll`) +
  tool `{42B5FAAE-6536-11D1-AE54-0000F80367C1}`; старый `...11D1-AE54...` в роли CSE не работает;
- `New-GPO` оставляет `versionNumber=0` — при ручной правке SYSVOL версию у AD-объекта GPO надо
  поднимать (иначе клиент не перечитывает), а gpt.ini держать в соответствии;
- `scripts.ini` лежит в `Machine\Scripts\scripts.ini` (секция `[Startup]`) и `User\Scripts\scripts.ini`
  (секция `[Logon]`); сами файлы — в `Machine\Scripts\Startup\` и `User\Scripts\Logon\`;
- startup-скрипт выполняется только при загрузке, logon — при входе (не при `gpupdate`).

Проверка (20.09.2026): у win11-host удалили машинный сертификат и перезагрузили — GPO startup-скрипт
сам выпустил `CN=WIN11HOST.corp.local` от мини-CA (`C:\ProgramData\RemoteControl\enroll-machine.log`).

---

## 5.2. Единый MSI (агент + автономный ЦС + консоль)

Сборка: `installer\build-single-msi.ps1` → `dist\msi\RemoteControl.msi` (~69 МБ).
Скрипт делает self-contained publish ЦС (`tools/labca`) и консоли, копирует агент и скрипты
в `installer\{ca\payload,viewer\payload,agent}` и вызывает `wix build`.

Установка (`msiexec /i RemoteControl.msi`) одной командой: служба агента + брандмауэр
(5900/80/8555) + правила доступа в реестре + автономный мини-CA + задачи
`RemoteControlLabCa` и `RemoteControlEnrollMachine` + выпуск машинного сертификата + консоль.

**Грабли, на которые наступили:**

- **WiX v5**: элементы `<CustomAction>` и `<InstallExecuteSequence>` должны лежать **внутри
  `<Package>`** — в `<Fragment>` они молча игнорируются (MSI собирается без ошибок и предупреждений,
  но таблица `CustomAction` остаётся пустой). Проверять так:
  `SELECT Action,Type FROM CustomAction` через COM `WindowsInstaller.Installer`.
- **WiX v5**: условие у `<Custom>` задаётся атрибутом `Condition="..."`, а не вложенным текстом
  (иначе `error WIX0400: The Custom element contains illegal inner text`).
- **Локализованный SYSTEM**: имя локального SYSTEM локализовано (`NT AUTHORITY\СИСТЕМА`),
  поэтому сравнивать строку `"SYSTEM"` нельзя — в `labca` проверка идёт по SID `S-1-5-18`
  (`identity.User?.Value == "S-1-5-18"`). Иначе enrollment с самого сервера падал с 403.
- **`.cmd` только ASCII**: cmd.exe читает .cmd в OEM-кодировке, кириллические `rem`-комментарии
  превращаются в мусорные команды. Все обёртки в `installer\agent\*.cmd` — ASCII.
- **CIDR/URL ЦС**: `LABCA_PUBLIC_URL` по умолчанию больше не `dc1.corp.local`, а FQDN локальной
  машины (`ResolvePublicUrl()`), иначе в CDP попадал чужой адрес; журнал ЦС переехал в
  `C:\ProgramData\RemoteControl\labca.log`.
- **Санитария при апгрейде**: `agent-cleanup.cmd` (deferred CA, `Before="RemoveFiles"`,
  `REMOVE~="ALL"`) снимает задачи и убивает `labca.exe`, иначе занятый файл мешает удалению
  старых файлов при major upgrade.

**Проверка (20.09.2026, win11-host, версия MSI 1.2.2.0):** установка → служба `RemoteControlAgent`
RUNNING, `/health` = ok, машинный сертификат `CN=WIN11HOST.corp.local` от **локального** ЦС
(`Issuer=CN=RemoteControl Lab CA`, chain ok с online-проверкой отзыва), ярлыки консоли на месте;
`enroll-user.cmd` выпустил клиентский `CN=administrator`, mTLS-подключение к 127.0.0.1:5900
дало баннер `RFB 003.008`.

### 5.2.1. Релиз через GitHub Actions (`.github/workflows/release.yml`)

Раньше MSI собирались руками и заливались в релиз вручную (в v1.0.0/v1.0.1 — только два
отдельных пакета, единого MSI там нет). Теперь релиз собирается по тегу: `git push origin v1.2.2`
→ runner `windows-latest` собирает агент, консоль и единый MSI и публикует релиз с ассетами
(`GITHUB_TOKEN`, `permissions: contents: write`).

**Грабли CI (все ловились по очереди):**

- на `windows-latest` теперь **Visual Studio Enterprise 2026** (VS 18), а пакетный CMake —
  4.x, который не собирает LibVNCServer 0.9.14 (`cmake_minimum_required < 3.5`) → берём
  **CMake 3.29.6 через pip** (`python -m pip install cmake==3.29.6`);
- генератор `-G "Visual Studio 17 2022"` на таком раннере падает (`could not find any instance
  of Visual Studio`), а pip-овый CMake по умолчанию берёт `NMake Makefiles`, который не понимает
  `-A x64` → собираем через **MSVC-окружение (`ilammy/msvc-dev-cmd`) + Ninja**;
- `-DCMAKE_MAKE_PROGRAM=$env:NINJA_EXE` в PowerShell **не подставляется** (CMake получает
  литерал `$env:NINJA_EXE`) → кладём каталог pip-скриптов в `GITHUB_PATH` и не задаём его вообще;
- Ninja — single-config, поэтому exe агента лежит в `build/agent/RemoteControlAgent.exe`
  (без `Release\`), и в `build-single-msi.ps1`/`build-msi.ps1` передаётся `-AgentExe` явно;
- логи упавших прогонов без авторизации не скачать, поэтому хвост ошибки печатается
  **аннотацией** (`::error title=...::...`, до этого — `::notice::` с версией VS/CMake) —
  иначе причина не видна.

Проверка: прогон по тегу `v1.2.2` — success, в релизе
`RemoteControl.msi` (68.7 МБ) + legacy `RemoteControlAgent.msi`/`RemoteControlViewer.msi`;
скачанный MSI — валидный (WiX 5.0.2, x64, `ProductName=Remote Control`).

### 5.2.2. Грабли установки на DC, push-режима и обновлений (1.2.3–1.2.5)

**Агент не должен тянуть чужие DLL.** CI-раннер нашёл OpenSSL, LibVNCServer слинковался с ним,
и агент получил зависимость от `libcrypto-3-x64.dll`. На целевой машине её нет → процесс не
стартует, SCM ждёт 30 с и MSI падает с **Error 1920** (и откатывается). Проверять:
`dumpbin /dependents RemoteControlAgent.exe`. Лечение: в `src/Agent/CMakeLists.txt`
все внешние опции LibVNCServer (`WITH_OPENSSL/WITH_GNUTLS/WITH_GCRYPT/WITH_SASL/WITH_LIBSSH2/
WITH_WEBSOCKETS/WITH_ZLIB/WITH_LZO/WITH_JPEG/WITH_PNG/...`) принудительно `OFF` — агенту они
не нужны (наружный TLS делает Schannel, консоль использует Raw/CopyRect/Hextile). В CI есть шаг
проверки, который падает, если в зависимостях появилась не-системная DLL (`api-ms-win-*` — системные).

**У автономных ЦС одинаковый subject.** `CN=RemoteControl Lab CA` у каждого сервера, поэтому
`enroll-cert.ps1` не может отличить «свой» ЦС от чужого сравнением строки Issuer: сертификат,
выпущенный ЦС другой машины, считался своим, и выпуск пропускался («действующий сертификат уже
есть»). Теперь ЦС опознаётся по ключу: AKI сертификата (`2.5.29.35`) сравнивается со SKI корня
(`2.5.29.14`), а корень берётся с `-CaUrl` (делается до проверки «уже есть»; работает и в PS 5.1,
где нет `X509ChainTrustMode`).

**`Add` в `CurrentUser\Root` может бросить исключение.** На DC1 `X509Store.Add` вернул
«The request is not supported», хотя сертификат фактически добавился. Теперь результат
проверяется наличием сертификата в хранилище, а не отсутствием исключения.

**Major upgrade: старый продукт удалять ДО установки нового.** При обновлении WIN11HOST
1.2.2 → 1.2.4 каталог `ca\` похудел с 187 файлов до 6 (пропал self-contained рантайм .NET) и ЦС
не запускался («Failed to resolve hostfxr.dll»). Причина — расписание по умолчанию
(`afterInstallFinalize`): новый продукт ставится, а затем удаление старого сносит только что
записанные файлы с теми же путями. В `installer/RemoteControl.wxs`:
`<MajorUpgrade Schedule="afterInstallValidate" .../>`.

**Push-режим временный.** `PushSession.CleanupAsync` (= `DisposeAsync`) делает
`RemoteServiceManager.StopAndDelete()` и удаляет `admin$\Temp\RemoteControlAgent.exe`. Поэтому
после сеанса push-агента служба исчезает — именно так «пропали» службы на DC1 и WIN11HOST
(удаление службы уносит и ветку `...\Services\RemoteControlAgent\Parameters`). У MSI-установки
это оставляет рассинхрон: продукт в «Установка приложений» есть, а службы нет — лечится
переустановкой. Push ставит агент в `C:\Windows\Temp`, MSI — в `Program Files`.

**Проверено 20.09.2026 (DC1, MSI 1.2.4/1.2.5):** DC1 (контроллер домена) с установленным MSI
развернул агент на WIN11HOST тем же кодом, что использует консоль
(`PushSession.DeployAsync` → `TlsTransport.ConnectAsync` → `RfbClient`): служба создалась и
запустилась, TLS 1.2 (`TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384`), кадр 1280x800, после сеанса
push убрал службу и файл.



---

## 6. Агент (C, Schannel + LibVNCServer)

Исходники: `/home/UCBerkeley/Projects/RemoteControl/src/Agent` (хост) = `C:\Projects\RemoteControl\src\Agent` (VM).

Сборка на VM:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Setup\stage3.ps1
```

Развёртывание (стоп, копирование exe в `C:\Program Files\RemoteControl`, создание службы, старт):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Setup\redeploy2.ps1
```

Где лежит:

- Установленный: `C:\Program Files\RemoteControl\RemoteControlAgent.exe`
- Свежая сборка: `C:\Projects\RemoteControl\build\agent\Release\RemoteControlAgent.exe`

Архитектура работы:

1. Служба (SYSTEM) слушает `:5900`, принимает TLS (Schannel, mTLS — клиентский сертификат обязателен).
2. Из клиентского сертификата извлекается UPN, проверяется членство в `Remote Control Operators`.
3. В активной консольной сессии (session 1) как **SYSTEM** запускается helper (`--session --listen 127.0.0.1 --port N`) на рабочем столе `Default` (SYSTEM-токен берётся у `winlogon.exe` консольной сессии).
4. Helper = LibVNCServer + отдельный поток захвата/ввода, который **следит за активным рабочим столом** (`OpenInputDesktop` + `SetThreadDesktop`):
   - на `Default` — захват через **DXGI Desktop Duplication** (быстро), фолбэк — GDI `BitBlt`;
   - на `Winlogon` (UAC, Ctrl+Alt+Del, экран блокировки) — GDI `BitBlt` с DC физического дисплея (`CreateDC("DISPLAY")`).
   Ввод (`SendInput`) выполняется из этого же потока, поэтому доходит и до защищённых окон (можно видеть UAC/экран входа и вводить пароль администратора).
5. Служба ретранслирует: TLS-клиент ↔ loopback-порт helper. Helper завершается сам после отключения клиента (служба добивает его через 3 с, чтобы не копились процессы и не исчерпывался лимит DXGI-дупликаторов).

Исправленные в ходе работ баги (важно не откатывать):

- `tls.c`:
  - `fail:` всегда возвращает `-1` (раньше при SEC_E_OK возвращался 0 и вызывающий код падал на NULL).
  - Машинный сертификат берётся из `LocalMachine\My` (`CertOpenStore(CERT_STORE_PROV_SYSTEM, ...)`) и должен иметь EKU Server Auth.
  - Убран `ASC_REQ_ALLOCATE_MEMORY`; хендшейк использует caller-allocated output token.
  - `DecryptMessage` в Schannel WS2022 отдаёт plaintext в буфере с типом `SECBUFFER_DATA` (у нас `bufs[1]`), а не в `bufs[0]`; остаток шифротекста — `SECBUFFER_EXTRA`.
  - plaintext копируется наружу **до** `memmove` остатка (иначе затирается).
  - `tls_read` не вызывает `DecryptMessage` с пустым входным буфером (сначала `recv`).
- `service.c`: relay-потоки без отладочных логов (события 1490–1495 были временными); выбор helper'а по блокировке консоли (событие 1406).
- `session.c`: парсинг аргументов с `argv[0]` (в `WinMain` `lpCmdLine` не содержит имени exe); `listenInterface = inet_addr(...)` (в LibVNCServer 0.9.14 это `in_addr_t`, не строка); `rfbInitServer` возвращает void → проверка через `rfbIsActive`; режим `--secure` для захвата Winlogon (события 1320–1324).
- `agent_main.c`: `--session` **и** `--secure` диспатчатся в `session_run` (иначе `--secure` уходил в `service_run` и мгновенно выходил по мутексу).
- `auth.c`: LDAP-поиск с базой `defaultNamingContext` из RootDSE. **Правило `AllowLocalAdmins`** (реестр `...\Parameters\AllowLocalAdmins`, DWORD, по умолчанию 1): доступ разрешается учётке, входящей в локальную группу Administrators этой машины, даже если её нет в `AllowedGroups`; проверка через SAM/RPC `NetUserGetLocalGroups`, SAM-имя берётся обратным преобразованием `LookupAccountNameW` → `LookupAccountSidW` (UPN ≠ sAMAccountName; до этого `fullAccount` собирался с неинициализированным `account`). Правило срабатывает и когда UPN вообще не найден в AD. События 1206 (разрешено), 1207/1208 (ошибки SAM), 1209 (нет `AllowedGroups` и не локальный админ), 1214 (`LookupAccountSidW`). Настройка: `configure-agent.ps1 -AllowLocalAdmins $true|$false`.
- `capture.c`: `BitBlt(SRCCOPY | CAPTUREBLT)`; в secure-режиме источник — `CreateDC("DISPLAY")`, а не `GetDC(NULL)` (событие 1330 при ошибке BitBlt). **Курсор мыши дорисовывается поверх кадра** (`GetCursorInfo` + `DrawIconEx`), т.к. BitBlt сам курсор не захватывает. **Убран отдельный буфер `s_prevBits` и полнокадровый `memcmp`** (агент падал с 0xc0000005 в `VCRUNTIME140.dll`, viewer получал `EndOfStreamException`); сравнение с предыдущим кадром теперь идёт построчно прямо при копировании во фреймбуфер LibVNCServer. **`capture_screen` возвращает ограничивающий прямоугольник изменённых пикселей** — клиенту уходит только грязная область, а не весь экран.
- `capture.c` + `dxgi_capture.c` (агент): захват в отдельном потоке, следящем за активным рабочим столом. **DXGI Desktop Duplication** (`dxgi_capture.c`, D3D11 + `IDXGIOutputDuplication`, пересоздание при `ACCESS_LOST`; события 1350/1351/1362) для обычного стола, GDI `BitBlt(SRCCOPY | CAPTUREBLT)` как фолбэк и для защищённого стола (`CreateDC("DISPLAY")`). **Курсор мыши дорисовывается поверх кадра** (`GetCursorInfo` + `DrawIconEx`; на VM курсор может быть `CURSOR_SUPPRESSED` — тогда не рисуем, как и сама ОС). **Убран отдельный буфер `s_prevBits` и полнокадровый `memcmp`** (агент падал с 0xc0000005 в `VCRUNTIME140.dll`, viewer получал `EndOfStreamException`); сравнение идёт построчно при копировании, заодно считается **ограничивающий прямоугольник изменений** — клиенту уходит только грязная область.
- `input.c` (агент): RFB-колбэки только кладут события в очередь, `SendInput` выполняет поток захвата (привязан к активному рабочему столу) — иначе ввод не доходит до UAC/экрана блокировки. Печатаемые символы шлются как Unicode-символы (`KEYEVENTF_UNICODE`: регистр/раскладка как на клиенте), а непечатаемые (Enter, Tab, стрелки, Win) и акселераторы при зажатом Ctrl/Alt — **скан-кодом** (`fill_vk`: `KEYEVENTF_SCANCODE` + `MapVirtualKey`). Инъекция только через `wVk` (с нулевым `wScan`) возвращала `SendInput == 1`, но клавиши не срабатывали — перешли на скан-код. SAS: keysym `0xFFFFFF00` → `SendSAS(FALSE)` из `sas.dll` с предварительно включённой привилегией `SeTcbPrivilege`. Диагностика — см. §7.2.
- **SAS требует настройки целевой машины:** `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\SoftwareSASGeneration = 2` (или 3) и запуск агента как SYSTEM. На `win11-host` политика выставлена и `SeTcbPrivilege` включается, `SendSAS` возвращает «нет ошибки», но экран безопасности в Win11 25H2 в лаборатории так и не появился — на Windows Server не проверялось.
- `session.c`: единый SYSTEM-helper вместо выбора «secure/user» при подключении; завершение helper'а после отключения клиента (`hadClient`); цикл `rfbProcessEvents(server, 10000)` без `Sleep(30)`; **`server->deferUpdateTime = 0` и `deferPtrUpdateTime = 0`** (иначе отложенная отправка добавляла лишний цикл ~30 мс и резала fps вдвое).

**Важно про захват Winlogon:** рабочий стол выбирается **динамически** — поток захвата следит за `OpenInputDesktop`, поэтому UAC/экран блокировки появляются в уже открытом сеансе без переподключения (события 1360–1362).

---

## 7. Viewer (WPF .NET 8)

Исходники: `/home/UCBerkeley/Projects/RemoteControl/src/Viewer` = `C:\Projects\RemoteControl\src\Viewer` на VM.

Сборка self-contained (не требует установленного .NET на целевой машине):

```powershell
dotnet publish C:\Projects\RemoteControl\src\Viewer\RemoteControl.Viewer\RemoteControl.Viewer.csproj -c Release -r win-x64 --self-contained true -o C:\Projects\RemoteControl\dist\viewer-sc
```

Готовая сборка на VM:

- `C:\Projects\RemoteControl\dist\viewer-sc\RemoteControl.Viewer.exe`
- Рядом лежит копия `RemoteControlAgent.exe` (для push-режима)
- `RemoteControl.Viewer.dll.config` — конфиг (порт, путь агента, PushAgentByDefault)

Запуск на VM в интерактивной сессии (через SSH напрямую не выйдет — WPF в session 0 не показывается):

```powershell
schtasks /run /tn 'RemoteControlViewer'
# задача создана так:
# schtasks /create /tn 'RemoteControlViewer' /tr 'C:\Projects\RemoteControl\dist\viewer-sc\RemoteControl.Viewer.exe' /sc once /st 00:00 /f /ru Administrator /rp 'LabAdmin!2026' /it
```

Конфиг `App.config` (в publish попадает как `RemoteControl.Viewer.dll.config`):

- `AgentPort` = 5900
- `AgentExePath` = `C:\Program Files\RemoteControl\RemoteControlAgent.exe` (для push)
- `PushAgentByDefault` = true

Исправленные баги:

- `AdBrowser.cs`: `DirectoryEntry` в .NET 8 **обязательно** с префиксом `LDAP://` (иначе COMException 0x80004005). Добавлен `EnsureLdapPath()`.
- `RfbClient.cs`: в `FramebufferUpdate` после типа сообщения пропускается 1 padding-байт. **После каждого полученного кадра сразу отправляется следующий `FramebufferUpdateRequest` (incremental)** — иначе экран обновлялся только один раз после подключения.
- `RfbStream.cs`: **буферизованное чтение** (64 КБ) — убирает тысячи мелких `ReadAsync` при разборе Hextile и сильно ускоряет декодирование кадров.
- `capture.c` (агент): кадр помечается изменённым только если реально изменился (`memcmp` с предыдущим кадром) — на статичной картинке сеть/CPU не тратятся.
- `MainWindow.xaml(.cs)`: клавиатура перехватывается на уровне окна (`PreviewKeyDown/Up` на Window), а не на `ScreenHost` — ввод уходит в удалённую машину, пока фокус не в локальном `TextBox`/`ListView`. **Обновление картинки — одним `WritePixels` на dirty-прямоугольник** (раньше было по вызову на каждую строку: до 800 вызовов на кадр).
- `Decoders.cs`: порядок Hextile-субпрямоугольников по LibVNCServer — сначала `count`, затем для каждого цвет (если coloured) + XY/WH. **Пиксельное значение в Hextile (background/foreground/цвет субрект) читается как 4 байта в порядке байт пиксельного формата (B,G,R,X), а НЕ как big-endian uint32** — иначе экран розово-маджентовый.
- `TlsTransport.cs`: `userCertificateSelectionCallback: null`; `CipherSuitesPolicy` только не на Windows.
- `RemoteServiceManager.cs`: приведение `dwWin32ExitCode` к `int`.
- `KeysymMapper.cs`: убран дубликат `Key.Enter`/`Key.Return`. **Обычный ввод отдаёт реальный символ с учётом Shift/CapsLock/раскладки** (`ToUnicodeEx`) — до этого буквы всегда уходили в нижнем регистре, и пароль с заглавными/символами набрать было нельзя. При зажатом Ctrl/Alt отдаётся базовый keysym → агент шлёт виртуальную клавишу (акселераторы).
- `RfbStream.cs` (**фикс вылетов консоли**): все операции записи сериализуются `SemaphoreSlim`. `SslStream` не допускает параллельных `WriteAsync` («This method may not be called when another write operation is pending»), а `MouseMove` приходит пачками — консоль падала через несколько секунд после начала движения мыши (`.NET Runtime` id 1026 в Application log). Запись использует отдельный `_writeBuffer`, чтобы не конфликтовать с буфером чтения.
- `MainWindow.xaml.cs`: `MouseMove` отправляется в режиме коалесцирования (если предыдущий кадр ещё в полёте — пропускается, чтобы не копить очередь), кнопки/колесо/клавиши — с ожиданием; исключения ввода гасятся, разрыв соединения не роняет UI.
- `App.xaml.cs`: `DispatcherUnhandledException` + `AppDomain.UnhandledException` пишутся в `viewer-errors.log` рядом с exe, ошибка в обработчике события больше не завершает процесс.

Важно: на VM `dotnet` установлен в `C:\dotnet` через dotnet-install и **не зарегистрирован в реестре**, поэтому обычный (не self-contained) exe выдаёт «You must install .NET Desktop Runtime». Используй self-contained сборку или запуск через `C:\dotnet\dotnet.exe App.dll`.

### 7.1. Захват клавиатуры (низкоуровневый хук)

Файл `src/Viewer/RemoteControl.Viewer/KeyboardHook.cs` — `WH_KEYBOARD_LL`.

Зачем хук: события WPF `PreviewKeyDown/Up` не видят системные сочетания (Win, Alt+Tab, Ctrl+Esc, Alt+Esc), и Windows обрабатывает их локально. Из-за этого Win открывал «Пуск» и на локальной, и на удалённой машине.

Как устроено:

- хук ставится на UI-поток при подключении (`MainWindow.StartInputCapture`) и снимается при отключении/разрыве
  (`StopInputCaptureAsync`), чтобы клавиши не «исчезали» в никуда;
- хук **глобальный**, поэтому перехватывает ввод только когда выполнено
  `GetForegroundWindow() == HWND окна консоли && !IsLocalInputTarget()` (фокус не в локальном `TextBox`/`ListView`);
  иначе клавиши уходят в локальную ОС (например, в поле «Компьютер» или в модальный диалог);
- колбэк хука не пишет в сокет сам: он кладёт `(keysym, down)` в `Channel`, а отдельная задача (`PumpKeysAsync`)
  отправляет их в RFB. Иначе колбэк превышает `LowLevelHooksTimeout` и Windows молча снимает хук;
- `Ctrl+Alt+End` (как в RDP) → SAS: в RFB уходит keysym `0xFFFFFF00`;
- `Ctrl+Alt+Shift` (удерживать) + любая не-модификаторная клавиша → клавиша отдаётся локальной ОС;
- `CapsLock`/`NumLock` уходят на удалённый стол (иначе состояние переключателей расходится с тем, где печатают);
- WPF-обработчики `PreviewKeyDown/Up` оставлены фолбэком: они работают, только если хук не установился.

Грабли, на которые уже наступили (не откатывать):

- событие хука нельзя называть `Key` — оно затеняет тип `System.Windows.Input.Key` и ломает `Key.None` (CS1061);
- `SetWindowsHookEx` в `Start()` требует `bool`-результата и не-readonly поля с делегатом (CS0191/CS8618).

### 7.2. Отладка клавиатуры

- Viewer: `KeyboardTrace=true` в `RemoteControl.Viewer.dll.config` → рядом с exe пишется `keyboard-trace.log`
  (установка хука + каждый отправленный keysym). По умолчанию выключено.
- Агент: если существует файл-маркер `C:\Windows\Temp\rc-input.enable`, агент пишет
  `C:\Windows\Temp\rc-input.log` (принятые keysym, скан-код, результат `SendInput`, окно в фокусе).
  Без маркера трассировка выключена — иначе на каждое нажатие была бы запись в файл.

---

## 8. Push-режим (DameWare-стиль)

`PushSession` копирует агент через `\\host\admin$\Temp\RemoteControlAgent.exe` (admin$ = `C:\Windows`, поэтому подкаталог только `Temp`; служба при этом создаётся с путём `C:\Windows\Temp\RemoteControlAgent.exe`), создаёт/запускает службу `RemoteControlAgent`, после сеанса — останавливает, удаляет службу и файл.

Требования к целевой машине (на `win11-host` уже настроено):

- файрвол: входящие `FPS-SMB-In-TCP*` (SMB 445) и `RemoteSvcAdmin-*` (SCM/RPC);
- в `LocalMachine\Root` — `Lab Root CA`, в `LocalMachine\My` — машинный сертификат `CN=WIN11HOST.corp.local` (EKU Server Auth) с SYSTEM ACL на ключе;
- в `C:\Windows\System32` — `vcruntime140.dll` и `vcruntime140_1.dll` (иначе агент падает со `0xC0000135`/1053 при старте службы).

**На `win-dc` служба `RemoteControlAgent` установлена постоянно.** Если подключаться с галкой «Push-агент»:
- push откроет существующую службу (код обрабатывает `ERROR_SERVICE_EXISTS`);
- при отключении/закрытии окна служба будет **удалена**.

После теста push-режима переустанови службу:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Setup\redeploy2.ps1
```

Чтобы просто посмотреть картинку без этих последствий — сними галку «Push-агент» и подключайся напрямую (агент уже слушает 5900).

---

## 9. Как прогнать тесты

### Полный цикл viewer-стека (консольный)

На VM в `C:\Setup\core-smoke`:

```powershell
dotnet run --project C:\Setup\core-smoke\core-smoke.csproj -c Release
```

Ожидаемый вывод:

```
TLS OK: Tls12, cipher=TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384
RFB OK: 1280x800, desktop='Remote Control Agent', connected=True
FRAME UPDATE OK
```

### Позитивный mTLS-тест openssl (с хоста)

```bash
openssl x509 -inform der -in /tmp/lab-root.cer -out /tmp/lab-root.pem   # если ещё нет
openssl pkcs12 -in /tmp/client-rctest.pfx -clcerts -nokeys -passin pass:LabAdmin!2026 -out /tmp/client-cert.pem
openssl pkcs12 -in /tmp/client-rctest.pfx -nocerts -nodes -passin pass:LabAdmin!2026 -out /tmp/client-key.pem

timeout 25 openssl s_client -connect 192.168.122.10:5900 \
  -cert /tmp/client-cert.pem -key /tmp/client-key.pem \
  -CAfile /tmp/lab-root.pem -tls1_2 -quiet
# ожидание: RFB 003.008
```

### Негативный тест (без клиентского сертификата)

```bash
timeout 25 openssl s_client -connect 192.168.122.10:5900 -CAfile /tmp/lab-root.pem -tls1_2 -quiet
# handshake должен быть отклонён, служба остаётся RUNNING
```

### Тест правила AllowLocalAdmins (локальный админ без AD-группы)

`locadmin` — доменная учётка, которая входит в `BUILTIN\Administrators`, но **не** в
`Remote Control Operators`; `rctest` — наоборот, член группы операторов.

```bash
# оба клиентских сертификата (пароль контейнера LabAdmin!2026)
for n in rctest locadmin; do
  openssl pkcs12 -in /tmp/client-$n.pfx -clcerts -nokeys -passin pass:LabAdmin!2026 -out /tmp/$n.crt
  openssl pkcs12 -in /tmp/client-$n.pfx -nocerts -nodes -passin pass:LabAdmin!2026 -out /tmp/$n.key
done

# AllowLocalAdmins=1: locadmin → RFB 003.008 (событие 1206), rctest → RFB 003.008 (событие 1204)
( sleep 6 ) | timeout 12 openssl s_client -connect 192.168.122.10:5900 \
  -cert /tmp/locadmin.crt -key /tmp/locadmin.key -CAfile /tmp/lab-root.pem -no_ign_eof
```

Отключить правило и убедиться, что доступ пропадает только у `locadmin`:

```powershell
# на VM
.\scripts\configure-agent.ps1 -AllowedGroups @("CN=Remote Control Operators,OU=Groups,DC=corp,DC=local") -AllowLocalAdmins $false
# locadmin → соединение закрывается без RFB, события 1401/1205; rctest → по-прежнему RFB 003.008
# вернуть как было:
.\scripts\configure-agent.ps1 -AllowedGroups @("CN=Remote Control Operators,OU=Groups,DC=corp,DC=local") -AllowLocalAdmins $true
```

Проверено 20.09.2026: `AllowLocalAdmins=1` → `locadmin` разрешён (1206), `rctest` разрешён (1204);
`AllowLocalAdmins=0` → `locadmin` отклонён (1401/1205), `rctest` разрешён (1204).

### События агента (Application log)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Setup\allevents.ps1
```

Ключевые ID: `1204` доступ разрешён по AD-группе, `1205` доступ запрещён, `1206` доступ разрешён локальному администратору машины, `1102` нет клиентского сертификата, `1402` подключение, `1302` helper слушает, `1404` ретрансляция, `1405` сеанс завершён, `1406` консоль заблокирована → захват Winlogon, `1324` secure-helper захватывает Winlogon, `1320`/`1321`/`1322`/`1323` проблемы запуска secure-helper, `1330` ошибка BitBlt. Проверка локального админа: `1207`/`1208`/`1214` ошибки SAM, `1209` нет `AllowedGroups` и не локальный админ.

### Стресс-тест параллельного ввода (защита от вылетов консоли)

Воспроизводит сценарий падения viewer при быстром движении мыши (параллельные `WriteAsync` в `SslStream`). Проект: `C:\Setup\stress`.

```powershell
dotnet run --project C:\Setup\stress\stress.csproj -c Release
# ожидание: ok=300 failed=0, still connected: True
```

Если консоль всё же упала — смотри `viewer-errors.log` рядом с exe (`C:\Program Files\Remote Control Viewer\`) и `.NET Runtime` (id 1026) в Application log.

---

## 10. Известные нюансы / грабли

1. **Захват заблокированной консоли работает, но источник выбирается при подключении**: если консоль была заблокирована на момент Connect — увидишь экран входа (Winlogon). Заблокировал/разблокировал во время сеанса — переподключись (агент перевыберет helper).
2. **CRL-сервер на хосте должен работать** (`http://192.168.122.1:8080/lab-root3.crl`), иначе .NET viewer падает с `RevocationStatusUnknown`.
3. **Не используй старые CRL-URL `lab-root.crl`/`lab-root2.crl`** — они закэшированы CryptnetUrlCache; текущий URL — `lab-root3.crl`.
4. **`dotnet` на VM не зарегистрирован в реестре** — не-self-contained exe не стартует; используй self-contained или `dotnet dll`.
5. **Push-режим удаляет службу** при отключении (см. раздел 8).
6. **virt-viewer на хосте** ставился после обновления gstreamer-набора (`gstreamer gst-plugins-base gst-plugins-base-libs gst-plugins-bad-libs gst-plugins-good`), т.к. полное `pacman -Syu` падало на сетевых таймаутах.
7. **Рабочий стол VM** при бездействии блокируется; после ребута VM нужно снова разблокировать консоль (или настроить отключение блокировки). На `win-dc` блокировку для тестов сняли: `powercfg /change monitor-timeout-ac 0`, `standby-timeout-ac 0`, `ScreenSaveActive=0`, `InactivityTimeoutSecs=0`.
8. `qemu-full` не ставился из-за конфликта gstreamer — используется `qemu-base`; видео VM — `vga`.
9. **Клавиатура доходит не до всех приложений.** На Win11 современные (UWP/WinUI) окна — «Блокнот», «Пуск» — принимают Unicode-символы и системные клавиши (Win, Alt+F4, Escape), но **игнорируют инжектированные Enter/стрелки/WM_KEYDOWN** в поле ввода. Это ограничение Win11/UWP, а не агента: на классических Win32-окнах и на самом рабочем столе клавиши (Alt+F4, Win) срабатывают. Если понадобится полноценный ввод в UWP — нужен путь через UI Automation/Text Services, а не `SendInput`.
10. **Хук клавиатуры работает только когда окно консоли на переднем плане** (`GetForegroundWindow`). Если поверх открыт «Пуск», диалог или другое приложение — перехвата нет, клавиши идут в локальную ОС. Это by design: глобальный хук иначе «съедал» бы ввод у чужих приложений.
11. **Ctrl+Alt+End (SAS)** перехватывается хуком и уходит агенту; на локальной машине экран безопасности при этом не появляется. Полный сценарий («экран безопасности на удалённой машине») в Win11 25H2 не воспроизвёлся — см. §6.

---

## 11. Быстрый старт после перерыва

```bash
# 1. Проверить, что VM запущена
virsh -c qemu:///system list --all

# 2. Проверить CRL-сервер (если не работает — поднять, см. раздел 3)
curl -s -o /dev/null -w '%{http_code}' http://192.168.122.1:8080/lab-root3.crl

# 3. Проверить службу агента на VM
sshpass -p 'LabAdmin!2026' ssh -o StrictHostKeyChecking=no Administrator@192.168.122.10 "sc.exe query RemoteControlAgent"

# 4. Разблокировать консоль VM, если нужно (virt-viewer)
# 5. Запустить viewer на VM
sshpass -p 'LabAdmin!2026' ssh -o StrictHostKeyChecking=no Administrator@192.168.122.10 "schtasks /run /tn 'RemoteControlViewer'"
```

---

## 12. MSI-установщики (WiX)

Собираются из `installer/` — два пакета:

| MSI | Что ставит | Размер |
|---|---|---|
| `RemoteControlAgent.msi` | `RemoteControlAgent.exe` в `C:\Program Files\RemoteControl`, службу `RemoteControlAgent` (LocalSystem, автостарт), правило брандмауэра TCP 5900 | ~0.3 МБ |
| `RemoteControlViewer.msi` | консоль оператора (self-contained) в `C:\Program Files\Remote Control Viewer`, ярлыки в Start Menu и на рабочем столе | ~57 МБ |

### Требования к сборочной машине

```powershell
# WiX v5 (v6+ требует платную OSMF-подписку)
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.Firewall.wixext/5.0.2
```

На этой VM .NET стоит в `C:\dotnet` без регистрации в реестре — обязательно `$env:DOTNET_ROOT='C:\dotnet'`, иначе apphost WiX не стартует.

### Сборка

```powershell
$env:DOTNET_ROOT='C:\dotnet'
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Projects\RemoteControl\installer\build-msi.ps1
```

Результат: `C:\Projects\RemoteControl\dist\msi\*.msi`.

Скрипт сам: копирует exe агента в `installer\agent\`, публикацию консоли — в `installer\viewer\payload\`, и запускает `wix build -arch x64`.

### Установка / удаление

```powershell
msiexec /i C:\Projects\RemoteControl\dist\msi\RemoteControlAgent.msi /qn /l*v C:\Setup\msi-agent.log
msiexec /i C:\Projects\RemoteControl\dist\msi\RemoteControlViewer.msi /qn /l*v C:\Setup\msi-viewer.log

# удаление
msiexec /x C:\Projects\RemoteControl\dist\msi\RemoteControlAgent.msi /qn
msiexec /x C:\Projects\RemoteControl\dist\msi\RemoteControlViewer.msi /qn
```

Проверка после установки агента:

```powershell
sc.exe query RemoteControlAgent                       # STATE: RUNNING
Test-Path 'C:\Program Files\RemoteControl\RemoteControlAgent.exe'   # True
netsh advfirewall firewall show rule name="Remote Control Agent (TCP 5900)"
```

MSI агента **заменяет** ручную установку из `redeploy2.ps1`: перед первым `msiexec /i` удали ручную службу (`sc.exe stop/delete RemoteControlAgent`), иначе возможен конфликт за имя службы.

Важные детали разметки (грабли, на которые уже наступили):

- `ComponentGroup`/`Component` в WiX v4+ должны лежать внутри `<Fragment>` — иначе `WIX0005`.
- `RegistryValue` не поддерживает атрибут `Bitness` (в отличие от v3) — `WIX0004`.
- Кодировка: `Codepage="1251"` в `<Package>`, иначе кириллица в строках даёт `WIX0311` (CP 1252).
- Обязателен `-arch x64`, иначе пакет собирается как 32-битный и ставится в `C:\Program Files (x86)`.
- Condition 64-битности: `Installed OR VersionNT64` (без `NOT`), иначе установка падает с 1603.
- `<Files Include="payload\**" />` требует cwd = каталог с `payload` (в скрипте используется `Set-Location`), опции `-bindpath` в v5 нет.
- Скрипты `.ps1` с кириллицей сохранять **в UTF-8 с BOM**, иначе PowerShell 5.1 не парсит файл.

