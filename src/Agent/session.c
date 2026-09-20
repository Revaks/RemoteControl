#include <winsock2.h>
#include <windows.h>
#include <wtsapi32.h>
#include <tlhelp32.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <rfb/rfb.h>

#include "config.h"
#include "session.h"
#include "capture.h"
#include "input.h"
#include "eventlog.h"

#pragma comment(lib, "wtsapi32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "advapi32.lib")

static int   g_port = 0;
static char  g_listenIp[64] = "127.0.0.1";
static BOOL  g_secure = FALSE;

// ---------- колбэки RFB ----------

static void on_key(rfbBool down, rfbKeySym key, rfbClientPtr cl)
{
    (void)cl;
    input_send_key(down, key);
}

static void on_pointer(int buttonMask, int x, int y, rfbClientPtr cl)
{
    (void)cl;
    input_send_pointer(buttonMask, x, y);
}

// ---------- запуск хелпера ----------

int session_run(int argc, wchar_t** argv)
{
    // argv получен из CommandLineToArgvW(lpCmdLine), где argv[0] — первый
    // переданный аргумент (имени exe в lpCmdLine нет).
    for (int i = 0; i < argc; i++)
    {
        if (wcscmp(argv[i], L"--port") == 0 && i + 1 < argc)
            g_port = _wtoi(argv[++i]);
        else if (wcscmp(argv[i], L"--listen") == 0 && i + 1 < argc)
        {
            WideCharToMultiByte(CP_UTF8, 0, argv[++i], -1, g_listenIp,
                (int)sizeof(g_listenIp), NULL, NULL);
        }
        else if (wcscmp(argv[i], L"--secure") == 0)
            g_secure = TRUE;
    }

    // Защищённый режим: убеждаемся, что консоль действительно заблокирована
    // (активный рабочий стол — Winlogon). Если нет — быстро выходим, служба
    // должна использовать обычный хелпер пользовательской сессии.
    if (g_secure)
    {
        HDESK input = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
        if (input == NULL)
        {
            rc_event_log(EVENTLOG_ERROR_TYPE, 1321, L"Secure helper: OpenInputDesktop failed: %lu", GetLastError());
            return 2;
        }

        wchar_t name[64] = { 0 };
        DWORD need = 0;
        BOOL named = GetUserObjectInformationW(input, UOI_NAME, name, (DWORD)sizeof(name), &need);
        CloseDesktop(input);

        if (!named || _wcsicmp(name, L"Winlogon") != 0)
        {
            rc_event_log(EVENTLOG_INFORMATION_TYPE, 1320,
                L"Secure helper: консоль не заблокирована (desktop=%s), выход", named ? name : L"?");
            return 2;
        }

        rc_event_log(EVENTLOG_INFORMATION_TYPE, 1324, L"Secure helper: захват рабочего стола Winlogon");
    }

    // В защищённом режиме снимаем физический дисплей (DC рабочего стола Winlogon
    // не отдаёт кадры через BitBlt).
    if (g_secure)
        capture_set_display_dc();

    // Корректный размер экрана при высоком DPI.
    SetProcessDPIAware();

    int width = GetSystemMetrics(SM_CXSCREEN);
    int height = GetSystemMetrics(SM_CYSCREEN);
    if (width <= 0 || height <= 0)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1300, L"Не удалось получить размер экрана");
        return 1;
    }

    char* framebuffer = (char*)malloc((size_t)width * height * 4);
    if (framebuffer == NULL)
        return 1;

    int fakeArgc = 1;
    char* fakeArgv[] = { (char*)"RemoteControlAgent", NULL };
    rfbScreenInfoPtr server = rfbGetScreen(&fakeArgc, fakeArgv, width, height, 8, 3, 4);
    if (server == NULL)
    {
        free(framebuffer);
        return 1;
    }

    server->frameBuffer = framebuffer;
    server->port = g_port;
    // В LibVNCServer 0.9.14 listenInterface — in_addr_t (сетевой порядок байт),
    // а не строка: используем inet_addr().
    server->listenInterface = inet_addr(g_listenIp);
    server->autoPort = FALSE;
    server->ipv6port = 0;
    server->alwaysShared = TRUE;
    server->neverShared = FALSE;
    server->desktopName = "Remote Control Agent";
    server->httpDir = NULL;

    server->kbdAddEvent = on_key;
    server->ptrAddEvent = on_pointer;

    // Отключаем отложенную отправку обновлений/ввода (deferUpdateTime = 5 мс
    // по умолчанию добавляет лишний цикл ~30 мс в нашем медленном цикле захвата,
    // из-за чего fps падал вдвое).
    server->deferUpdateTime = 0;
    server->deferPtrUpdateTime = 0;

    // Формат кадра: 32bpp BGRA.
    server->serverFormat.bitsPerPixel = 32;
    server->serverFormat.depth = 24;
    server->serverFormat.bigEndian = FALSE;
    server->serverFormat.trueColour = TRUE;
    server->serverFormat.redMax = 255;
    server->serverFormat.greenMax = 255;
    server->serverFormat.blueMax = 255;
    server->serverFormat.redShift = 16;
    server->serverFormat.greenShift = 8;
    server->serverFormat.blueShift = 0;

    // rfbInitServer() в LibVNCServer 0.9.14 возвращает void; проверяем
    // активность сервера через rfbIsActive().
    rfbInitServer(server);
    if (!rfbIsActive(server))
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1301, L"rfbInitServer failed");
        rfbScreenCleanup(server);
        free(framebuffer);
        return 1;
    }

    if (capture_init(width, height) != 0)
    {
        rfbShutdownServer(server, TRUE);
        rfbScreenCleanup(server);
        free(framebuffer);
        return 1;
    }

    rc_event_log(EVENTLOG_INFORMATION_TYPE, 1302, L"Session helper слушает %S:%d", g_listenIp, g_port);

    // Главный цикл: обрабатываем RFB-события и обновляем кадр, пока есть клиенты.
    // Короткий таймаут select задаёт темп кадров, события ввода при этом
    // обрабатываются сразу.
    while (rfbIsActive(server))
    {
        rfbProcessEvents(server, 10000); // до 10 мс на события

        if (server->clientHead != NULL)
        {
            int dirtyX = 0, dirtyY = 0, dirtyW = 0, dirtyH = 0;
            int changed = capture_screen(framebuffer, width, height, width * 4,
                                         &dirtyX, &dirtyY, &dirtyW, &dirtyH);
            if (changed > 0)
                rfbMarkRectAsModified(server, dirtyX, dirtyY,
                                      dirtyX + dirtyW, dirtyY + dirtyH);
        }
    }

    rc_event_log(EVENTLOG_INFORMATION_TYPE, 1303, L"Session helper завершает работу");

    capture_cleanup();
    rfbShutdownServer(server, TRUE);
    rfbScreenCleanup(server);
    free(framebuffer);
    return 0;
}

// ---------- запуск хелпера службой ----------

HANDLE session_spawn_helper(unsigned short* out_port)
{
    if (out_port == NULL)
        return NULL;

    DWORD sessionId = WTSGetActiveConsoleSessionId();
    if (sessionId == 0xFFFFFFFF)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1310, L"Нет активной консольной сессии");
        return NULL;
    }

    HANDLE token = NULL;
    if (!WTSQueryUserToken(sessionId, &token))
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1311, L"WTSQueryUserToken failed: %lu", GetLastError());
        return NULL;
    }

    // Выбираем свободный loopback-порт.
    SOCKET probe = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (probe == INVALID_SOCKET)
    {
        CloseHandle(token);
        return NULL;
    }

    struct sockaddr_in sa;
    ZeroMemory(&sa, sizeof(sa));
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    sa.sin_port = 0;

    if (bind(probe, (struct sockaddr*)&sa, sizeof(sa)) != 0)
    {
        closesocket(probe);
        CloseHandle(token);
        return NULL;
    }

    int len = sizeof(sa);
    if (getsockname(probe, (struct sockaddr*)&sa, &len) != 0)
    {
        closesocket(probe);
        CloseHandle(token);
        return NULL;
    }
    closesocket(probe);

    *out_port = ntohs(sa.sin_port);

    // Запускаем этот же exe в пользовательской сессии на её рабочем столе.
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(NULL, exePath, MAX_PATH);

    wchar_t cmd[1024];
    swprintf_s(cmd, _countof(cmd), L"\"%s\" --session --listen 127.0.0.1 --port %u",
        exePath, (unsigned)*out_port);

    STARTUPINFOW si;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.lpDesktop = L"winsta0\\default";
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;

    PROCESS_INFORMATION pi;
    ZeroMemory(&pi, sizeof(pi));

    if (!CreateProcessAsUserW(token, NULL, cmd, NULL, NULL, FALSE,
            CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
            NULL, NULL, &si, &pi))
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1312, L"CreateProcessAsUserW failed: %lu", GetLastError());
        CloseHandle(token);
        return NULL;
    }

    CloseHandle(token);
    CloseHandle(pi.hThread);
    return pi.hProcess;
}

// ---------- запуск SYSTEM-хелпера на рабочем столе Winlogon ----------

static BOOL enable_debug_privilege(void)
{
    HANDLE token = NULL;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token))
        return FALSE;

    TOKEN_PRIVILEGES tp;
    LUID luid;
    ZeroMemory(&tp, sizeof(tp));
    BOOL ok = FALSE;
    if (LookupPrivilegeValueW(NULL, SE_DEBUG_NAME, &luid))
    {
        tp.PrivilegeCount = 1;
        tp.Privileges[0].Luid = luid;
        tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
        AdjustTokenPrivileges(token, FALSE, &tp, sizeof(tp), NULL, NULL);
        ok = (GetLastError() == ERROR_SUCCESS);
    }
    CloseHandle(token);
    return ok;
}

// Дублирует первичный SYSTEM-токен winlogon.exe активной консольной сессии.
static HANDLE get_console_system_token(void)
{
    DWORD sessionId = WTSGetActiveConsoleSessionId();
    if (sessionId == 0xFFFFFFFF)
        return NULL;

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE)
        return NULL;

    PROCESSENTRY32W pe;
    ZeroMemory(&pe, sizeof(pe));
    pe.dwSize = sizeof(pe);

    HANDLE sysToken = NULL;
    if (Process32FirstW(snap, &pe))
    {
        do
        {
            if (_wcsicmp(pe.szExeFile, L"winlogon.exe") != 0)
                continue;

            DWORD pidSession = 0;
            if (!ProcessIdToSessionId(pe.th32ProcessID, &pidSession) || pidSession != sessionId)
                continue;

            HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pe.th32ProcessID);
            if (proc == NULL)
                continue;

            HANDLE token = NULL;
            if (OpenProcessToken(proc, TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY, &token))
            {
                HANDLE dup = NULL;
                if (DuplicateTokenEx(token, MAXIMUM_ALLOWED, NULL, SecurityImpersonation, TokenPrimary, &dup))
                    sysToken = dup;
                CloseHandle(token);
            }
            CloseHandle(proc);

            if (sysToken != NULL)
                break;
        } while (Process32NextW(snap, &pe));
    }

    CloseHandle(snap);
    return sysToken;
}

HANDLE session_spawn_secure_helper(unsigned short* out_port)
{
    if (out_port == NULL)
        return NULL;
    *out_port = 0;

    enable_debug_privilege();

    HANDLE token = get_console_system_token();
    if (token == NULL)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1322, L"Не удалось получить SYSTEM-токен консольной сессии");
        return NULL;
    }

    // Выбираем свободный loopback-порт.
    SOCKET probe = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (probe == INVALID_SOCKET)
    {
        CloseHandle(token);
        return NULL;
    }

    struct sockaddr_in sa;
    ZeroMemory(&sa, sizeof(sa));
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    sa.sin_port = 0;

    if (bind(probe, (struct sockaddr*)&sa, sizeof(sa)) != 0)
    {
        closesocket(probe);
        CloseHandle(token);
        return NULL;
    }

    int len = sizeof(sa);
    if (getsockname(probe, (struct sockaddr*)&sa, &len) != 0)
    {
        closesocket(probe);
        CloseHandle(token);
        return NULL;
    }
    closesocket(probe);

    *out_port = ntohs(sa.sin_port);

    // Запускаем этот же exe как SYSTEM на защищённом рабочем столе Winlogon.
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(NULL, exePath, MAX_PATH);

    wchar_t cmd[1024];
    swprintf_s(cmd, _countof(cmd), L"\"%s\" --secure --listen 127.0.0.1 --port %u",
        exePath, (unsigned)*out_port);

    STARTUPINFOW si;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.lpDesktop = L"winsta0\\winlogon";
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;

    PROCESS_INFORMATION pi;
    ZeroMemory(&pi, sizeof(pi));

    if (!CreateProcessAsUserW(token, NULL, cmd, NULL, NULL, FALSE,
            CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
            NULL, NULL, &si, &pi))
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1323, L"CreateProcessAsUserW(winlogon) failed: %lu", GetLastError());
        CloseHandle(token);
        return NULL;
    }

    CloseHandle(token);
    CloseHandle(pi.hThread);
    return pi.hProcess;
}
