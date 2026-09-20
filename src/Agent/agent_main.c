#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include "config.h"
#include "service.h"
#include "session.h"

// Точка входа агента (GUI-subsystem).
//   без аргументов                    — запуск как служба (или отладка в консоли SCM-обвязки)
//   --session --listen <ip> --port N  — хелпер пользовательской сессии (запускается службой)
int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, PWSTR cmdLine, int showCmd)
{
    (void)hInstance;
    (void)hPrevInstance;
    (void)showCmd;

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(cmdLine, &argc);

    int rc;
    // lpCmdLine в WinMain не содержит имени exe, поэтому первый аргумент — argv[0].
    if (argc > 0 && argv != NULL &&
        (wcscmp(argv[0], L"--session") == 0 || wcscmp(argv[0], L"--secure") == 0))
        rc = session_run(argc, argv);
    else
        rc = service_run();

    if (argv != NULL)
        LocalFree(argv);

    return rc;
}
