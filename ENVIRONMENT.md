# RemoteControl — лабораторный стенд (Windows Server 2022 + DC)

Дата актуальности: 19.09.2026. Описывает, что поднято, как устроено и как перезапустить после перерыва.

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
- Тестовый пользователь: `rctest@corp.local` / пароль `LabAdmin!2026`
- Клиентский сертификат `rctest` лежит в `CurrentUser\My` у пользователя Administrator на VM (и в `C:\Setup\client-rctest.pfx`)

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
- CRL URL вынесен в отдельный файл `lab-root2.crl`, чтобы не попадать в кэш CryptnetUrlCache от старых CRL

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
3. Если консоль **разблокирована**: в интерактивной сессии (session 1) запускается helper (`--session --listen 127.0.0.1 --port N`) — LibVNCServer VNC-сервер, захватывает экран через `BitBlt`.
4. Если консоль **заблокирована**: служба берёт SYSTEM-токен `winlogon.exe` консольной сессии и запускает helper на защищённом рабочем столе `WinSta0\Winlogon` (`--secure ...`). Он снимает экран входа/блокировки через DC физического дисплея (`CreateDC("DISPLAY")`).
5. Служба ретранслирует: TLS-клиент ↔ loopback-порт helper.

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
- `auth.c`: LDAP-поиск с базой `defaultNamingContext` из RootDSE.
- `capture.c`: `BitBlt(SRCCOPY | CAPTUREBLT)`; в secure-режиме источник — `CreateDC("DISPLAY")`, а не `GetDC(NULL)` (событие 1330 при ошибке BitBlt). **Курсор мыши дорисовывается поверх кадра** (`GetCursorInfo` + `DrawIconEx`), т.к. BitBlt сам курсор не захватывает. **Убран отдельный буфер `s_prevBits` и полнокадровый `memcmp`** (агент падал с 0xc0000005 в `VCRUNTIME140.dll`, viewer получал `EndOfStreamException`); сравнение с предыдущим кадром теперь идёт построчно прямо при копировании во фреймбуфер LibVNCServer. **`capture_screen` возвращает ограничивающий прямоугольник изменённых пикселей** — клиенту уходит только грязная область, а не весь экран.
- `session.c`: цикл `rfbProcessEvents(server, 10000)` без `Sleep(30)`; **`server->deferUpdateTime = 0` и `deferPtrUpdateTime = 0`** (иначе отложенная отправка добавляла лишний цикл ~30 мс и резала fps вдвое).

**Важно про захват Winlogon:** источник захвата выбирается **в момент подключения**. Если консоль заблокировали/разблокировали во время активного сеанса — нужно переподключиться, чтобы агент перевыбрал helper.

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
- `KeysymMapper.cs`: убран дубликат `Key.Enter`/`Key.Return`.

Важно: на VM `dotnet` установлен в `C:\dotnet` через dotnet-install и **не зарегистрирован в реестре**, поэтому обычный (не self-contained) exe выдаёт «You must install .NET Desktop Runtime». Используй self-contained сборку или запуск через `C:\dotnet\dotnet.exe App.dll`.

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

### События агента (Application log)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Setup\allevents.ps1
```

Ключевые ID: `1204` доступ разрешён, `1102` нет клиентского сертификата, `1402` подключение, `1302` helper слушает, `1404` ретрансляция, `1405` сеанс завершён, `1406` консоль заблокирована → захват Winlogon, `1324` secure-helper захватывает Winlogon, `1320`/`1321`/`1322`/`1323` проблемы запуска secure-helper, `1330` ошибка BitBlt.

---

## 10. Известные нюансы / грабли

1. **Захват заблокированной консоли работает, но источник выбирается при подключении**: если консоль была заблокирована на момент Connect — увидишь экран входа (Winlogon). Заблокировал/разблокировал во время сеанса — переподключись (агент перевыберет helper).
2. **CRL-сервер на хосте должен работать** (`http://192.168.122.1:8080/lab-root3.crl`), иначе .NET viewer падает с `RevocationStatusUnknown`.
3. **Не используй старые CRL-URL `lab-root.crl`/`lab-root2.crl`** — они закэшированы CryptnetUrlCache; текущий URL — `lab-root3.crl`.
4. **`dotnet` на VM не зарегистрирован в реестре** — не-self-contained exe не стартует; используй self-contained или `dotnet dll`.
5. **Push-режим удаляет службу** при отключении (см. раздел 8).
6. **virt-viewer на хосте** ставился после обновления gstreamer-набора (`gstreamer gst-plugins-base gst-plugins-base-libs gst-plugins-bad-libs gst-plugins-good`), т.к. полное `pacman -Syu` падало на сетевых таймаутах.
7. **Рабочий стол VM** при бездействии блокируется; после ребута VM нужно снова разблокировать консоль (или настроить отключение блокировки).
8. `qemu-full` не ставился из-за конфликта gstreamer — используется `qemu-base`; видео VM — `vga`.

---

## 11. Быстрый старт после перерыва

```bash
# 1. Проверить, что VM запущена
virsh -c qemu:///system list --all

# 2. Проверить CRL-сервер (если не работает — поднять, см. раздел 3)
curl -s -o /dev/null -w '%{http_code}' http://192.168.122.1:8080/lab-root2.crl

# 3. Проверить службу агента на VM
sshpass -p 'LabAdmin!2026' ssh -o StrictHostKeyChecking=no Administrator@192.168.122.10 "sc.exe query RemoteControlAgent"

# 4. Разблокировать консоль VM, если нужно (virt-viewer)
# 5. Запустить viewer на VM
sshpass -p 'LabAdmin!2026' ssh -o StrictHostKeyChecking=no Administrator@192.168.122.10 "schtasks /run /tn 'RemoteControlViewer'"
```
