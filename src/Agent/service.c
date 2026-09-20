#include <winsock2.h>
#include <windows.h>
#include <ws2tcpip.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "config.h"
#include "service.h"
#include "session.h"
#include "tls.h"
#include "auth.h"
#include "eventlog.h"

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "advapi32.lib")

static SERVICE_STATUS        g_status;
static SERVICE_STATUS_HANDLE g_statusHandle;
static HANDLE                g_stopEvent;
static SOCKET                g_listenSocket = INVALID_SOCKET;

static DWORD WINAPI listener_main(LPVOID param);

// ---------- конфигурация ----------

static unsigned short config_get_port(void)
{
    DWORD port = RC_AGENT_DEFAULT_PORT;
    HKEY key;

    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, RC_REG_PARAMETERS_KEY, 0, KEY_QUERY_VALUE, &key) == ERROR_SUCCESS)
    {
        DWORD type = 0, size = sizeof(port);
        if (RegQueryValueExW(key, RC_REG_VALUE_PORT, NULL, &type, (LPBYTE)&port, &size) == ERROR_SUCCESS &&
            type == REG_DWORD && port > 0 && port <= 65535)
        {
            // ok
        }
        else
        {
            port = RC_AGENT_DEFAULT_PORT;
        }
        RegCloseKey(key);
    }

    return (unsigned short)port;
}

// ---------- SCM ----------

static void update_service_status(DWORD state, DWORD exitCode, DWORD waitHint)
{
    g_status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
    g_status.dwCurrentState = state;
    g_status.dwControlsAccepted = (state == SERVICE_RUNNING) ? SERVICE_ACCEPT_STOP : 0;
    g_status.dwWin32ExitCode = exitCode;
    g_status.dwServiceSpecificExitCode = 0;
    g_status.dwCheckPoint = 0;
    g_status.dwWaitHint = waitHint;

    if (g_statusHandle != NULL)
        SetServiceStatus(g_statusHandle, &g_status);
}

static DWORD WINAPI service_ctrl_handler(DWORD ctrl, DWORD eventType, LPVOID eventData, LPVOID context)
{
    (void)eventType; (void)eventData; (void)context;

    switch (ctrl)
    {
        case SERVICE_CONTROL_STOP:
        case SERVICE_CONTROL_SHUTDOWN:
            update_service_status(SERVICE_STOP_PENDING, NO_ERROR, 3000);
            if (g_stopEvent != NULL)
                SetEvent(g_stopEvent);
            if (g_listenSocket != INVALID_SOCKET)
                closesocket(g_listenSocket); // разбудить accept/select
            return NO_ERROR;
        case SERVICE_CONTROL_INTERROGATE:
            SetServiceStatus(g_statusHandle, &g_status);
            return NO_ERROR;
        default:
            return NO_ERROR;
    }
}

static void WINAPI service_main(DWORD argc, wchar_t** argv)
{
    (void)argc; (void)argv;

    g_statusHandle = RegisterServiceCtrlHandlerExW(RC_AGENT_SERVICE_NAME, service_ctrl_handler, NULL);
    if (g_statusHandle == NULL)
        return;

    update_service_status(SERVICE_START_PENDING, NO_ERROR, 3000);

    g_stopEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (g_stopEvent == NULL)
    {
        update_service_status(SERVICE_STOPPED, GetLastError(), 0);
        return;
    }

    update_service_status(SERVICE_RUNNING, NO_ERROR, 0);

    listener_main(NULL);

    update_service_status(SERVICE_STOPPED, NO_ERROR, 0);
}

// ---------- сеть ----------

static SOCKET connect_loopback(unsigned short port)
{
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET)
        return INVALID_SOCKET;

    struct sockaddr_in sa;
    ZeroMemory(&sa, sizeof(sa));
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    sa.sin_port = htons(port);

    // Хелперу нужно время на запуск и rfbInitServer — пробуем до 10 секунд.
    for (int attempt = 0; attempt < 50; attempt++)
    {
        if (connect(s, (struct sockaddr*)&sa, sizeof(sa)) == 0)
            return s;
        Sleep(200);
    }

    closesocket(s);
    return INVALID_SOCKET;
}

typedef struct relay_ctx
{
    tls_conn* tls;
    SOCKET    plain;
} relay_ctx;

// viewer -> агент: читаем расшифрованный TLS, пишем в loopback.
static DWORD WINAPI relay_tls_to_plain(LPVOID param)
{
    relay_ctx* ctx = (relay_ctx*)param;
    BYTE buf[RC_RELAY_BUFFER_SIZE];

    for (;;)
    {
        int n = tls_read(ctx->tls, buf, sizeof(buf));
        if (n <= 0)
            break;

        int off = 0;
        while (off < n)
        {
            int sent = send(ctx->plain, (char*)buf + off, n - off, 0);
            if (sent == SOCKET_ERROR)
                goto done;
            off += sent;
        }
    }

done:
    // Разбудить встречный поток.
    shutdown(ctx->plain, SD_BOTH);
    tls_abort(ctx->tls);
    return 0;
}

// агент -> viewer: читаем loopback, шифруем и отправляем в TLS.
static DWORD WINAPI relay_plain_to_tls(LPVOID param)
{
    relay_ctx* ctx = (relay_ctx*)param;
    BYTE buf[RC_RELAY_BUFFER_SIZE];

    for (;;)
    {
        int n = recv(ctx->plain, (char*)buf, sizeof(buf), 0);
        if (n <= 0)
            break;
        if (tls_write(ctx->tls, buf, n) != 0)
            break;
    }

    shutdown(ctx->plain, SD_BOTH);
    tls_abort(ctx->tls);
    return 0;
}

static void relay(tls_conn* tls, SOCKET plain)
{
    relay_ctx ctx;
    ctx.tls = tls;
    ctx.plain = plain;

    HANDLE threads[2];
    threads[0] = CreateThread(NULL, 0, relay_tls_to_plain, &ctx, 0, NULL);
    threads[1] = CreateThread(NULL, 0, relay_plain_to_tls, &ctx, 0, NULL);

    if (threads[0] != NULL && threads[1] != NULL)
        WaitForMultipleObjects(2, threads, TRUE, INFINITE);
    else if (threads[0] != NULL)
        WaitForSingleObject(threads[0], INFINITE);
    else if (threads[1] != NULL)
        WaitForSingleObject(threads[1], INFINITE);

    if (threads[0] != NULL) CloseHandle(threads[0]);
    if (threads[1] != NULL) CloseHandle(threads[1]);
}

// Обработка одного входящего подключения.
static DWORD WINAPI handle_client(LPVOID param)
{
    SOCKET client = (SOCKET)(INT_PTR)param;
    tls_conn* tls = NULL;
    SOCKET plain = INVALID_SOCKET;
    HANDLE helper = NULL;
    PCCERT_CONTEXT machineCert = NULL;
    unsigned short helperPort = 0;
    int rc = -1;

    // 1. TLS с обязательным клиентским сертификатом.
    machineCert = tls_find_machine_certificate();
    if (machineCert == NULL)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1400,
            L"Не найден сертификат машины (LocalMachine\\MY). Настройте авторегистрацию через AD CS.");
        goto cleanup;
    }

    if (tls_server_accept(client, machineCert, &tls) != 0)
        goto cleanup;

    // 2. Аутентификация и авторизация по сертификату.
    {
        PCCERT_CONTEXT peer = tls_get_peer_certificate(tls);
        if (peer == NULL)
            goto cleanup;

        wchar_t upn[256];
        if (auth_check_certificate(peer, upn, _countof(upn)) != 0)
        {
            rc_event_log(EVENTLOG_WARNING_TYPE, 1401, L"Отказано в доступе (UPN: %s)", upn);
            goto cleanup;
        }
        rc_event_log(EVENTLOG_INFORMATION_TYPE, 1402, L"Подключение: %s", upn);
    }

    // 3. Хелпер: если консоль заблокирована — SYSTEM-хелпер на рабочем столе
    //    Winlogon (захват экрана входа), иначе обычный хелпер в сессии пользователя.
    helper = session_spawn_secure_helper(&helperPort);
    if (helper != NULL)
    {
        // Secure-хелпер быстро завершается с кодом 2, если консоль НЕ заблокирована.
        if (WaitForSingleObject(helper, 1500) == WAIT_OBJECT_0)
        {
            CloseHandle(helper);
            helper = NULL;
        }
    }

    if (helper != NULL)
    {
        rc_event_log(EVENTLOG_INFORMATION_TYPE, 1406, L"Консоль заблокирована: захват Winlogon (%u)", helperPort);
    }
    else
    {
        helper = session_spawn_helper(&helperPort);
    }

    if (helper == NULL)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1403, L"Не удалось запустить session helper");
        goto cleanup;
    }

    plain = connect_loopback(helperPort);
    if (plain == INVALID_SOCKET)
        goto cleanup;

    rc_event_log(EVENTLOG_INFORMATION_TYPE, 1404, L"Ретрансляция: TLS -> 127.0.0.1:%u", helperPort);

    // 4. Проксирование байтов.
    relay(tls, plain);
    rc = 0;

cleanup:
    if (plain != INVALID_SOCKET)
        closesocket(plain);
    if (tls != NULL)
        tls_close(tls);
    if (machineCert != NULL)
        CertFreeCertificateContext(machineCert);
    if (helper != NULL)
    {
        // Даём хелперу завершиться и закрываем.
        WaitForSingleObject(helper, 5000);
        CloseHandle(helper);
    }
    closesocket(client);

    if (rc == 0)
        rc_event_log(EVENTLOG_INFORMATION_TYPE, 1405, L"Сеанс завершён");

    return (DWORD)rc;
}

DWORD WINAPI listener_main(LPVOID param)
{
    (void)param;

    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);

    unsigned short port = config_get_port();

    g_listenSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (g_listenSocket == INVALID_SOCKET)
        return 1;

    BOOL reuse = TRUE;
    setsockopt(g_listenSocket, SOL_SOCKET, SO_REUSEADDR, (const char*)&reuse, sizeof(reuse));

    struct sockaddr_in sa;
    ZeroMemory(&sa, sizeof(sa));
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = INADDR_ANY;
    sa.sin_port = htons(port);

    if (bind(g_listenSocket, (struct sockaddr*)&sa, sizeof(sa)) != 0 ||
        listen(g_listenSocket, SOMAXCONN) != 0)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1410, L"Не удалось слушать порт %u: %d", port, WSAGetLastError());
        closesocket(g_listenSocket);
        g_listenSocket = INVALID_SOCKET;
        return 1;
    }

    rc_event_log(EVENTLOG_INFORMATION_TYPE, 1411, L"Служба слушает порт %u (TLS 1.3/mTLS)", port);

    while (WaitForSingleObject(g_stopEvent, 0) == WAIT_TIMEOUT)
    {
        fd_set readSet;
        FD_ZERO(&readSet);
        FD_SET(g_listenSocket, &readSet);

        struct timeval tv;
        tv.tv_sec = 1;
        tv.tv_usec = 0;

        int sel = select(0, &readSet, NULL, NULL, &tv);
        if (sel == SOCKET_ERROR)
        {
            if (WSAGetLastError() == WSAEINTR)
                continue;
            break;
        }
        if (sel == 0)
            continue;

        SOCKET client = accept(g_listenSocket, NULL, NULL);
        if (client == INVALID_SOCKET)
            continue;

        HANDLE thread = CreateThread(NULL, 0, handle_client, (LPVOID)(INT_PTR)client, 0, NULL);
        if (thread != NULL)
            CloseHandle(thread);
        else
            closesocket(client);
    }

    closesocket(g_listenSocket);
    g_listenSocket = INVALID_SOCKET;
    WSACleanup();
    return 0;
}

// ---------- точка входа ----------

int service_run(void)
{
    // Один экземпляр службы.
    HANDLE mutex = CreateMutexW(NULL, TRUE, RC_SERVICE_MUTEX_NAME);
    if (mutex != NULL && GetLastError() == ERROR_ALREADY_EXISTS)
    {
        CloseHandle(mutex);
        return 0;
    }

    SERVICE_TABLE_ENTRYW table[] =
    {
        { (LPWSTR)RC_AGENT_SERVICE_NAME, (LPSERVICE_MAIN_FUNCTIONW)service_main },
        { NULL, NULL },
    };

    if (!StartServiceCtrlDispatcherW(table))
    {
        // Запущено не через SCM — отладочный режим в консоли.
        rc_event_log(EVENTLOG_WARNING_TYPE, 1420, L"Запуск вне SCM (отладка)");

        g_stopEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
        if (g_stopEvent != NULL)
        {
            listener_main(NULL);
            CloseHandle(g_stopEvent);
        }
    }

    if (mutex != NULL)
        CloseHandle(mutex);
    return 0;
}
