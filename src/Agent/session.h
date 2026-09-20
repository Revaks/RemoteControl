#pragma once

#include <windows.h>

// Режим хелпера пользовательской сессии: слушает loopback и отдаёт RFB через LibVNCServer.
int session_run(int argc, wchar_t** argv);

// Запускает хелпер в активной консольной сессии. Возвращает HANDLE процесса
// и выбранный свободный loopback-порт в out_port. NULL при ошибке.
HANDLE session_spawn_helper(unsigned short* out_port);

// Запускает SYSTEM-хелпер на защищённом рабочем столе Winlogon (для захвата
// экрана входа/блокировки). Возвращает HANDLE процесса и выбранный свободный
// loopback-порт в out_port. Если консоль не заблокирована, хелпер сам быстро
// завершается с кодом 2. NULL при ошибке запуска.
HANDLE session_spawn_secure_helper(unsigned short* out_port);
