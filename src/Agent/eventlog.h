#pragma once

#include <windows.h>

// Пишет сообщение в Windows Event Log и в отладчик.
void rc_event_log(WORD type, DWORD eventId, const wchar_t* fmt, ...);
