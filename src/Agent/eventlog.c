#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include "config.h"
#include "eventlog.h"

void rc_event_log(WORD type, DWORD eventId, const wchar_t* fmt, ...)
{
    wchar_t message[1024];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(message, _countof(message), _TRUNCATE, fmt, args);
    va_end(args);

    HANDLE source = RegisterEventSourceW(NULL, RC_EVENTLOG_SOURCE);
    if (source != NULL)
    {
        const wchar_t* strings[] = { message };
        ReportEventW(source, type, 0, eventId, NULL, 1, 0, strings, NULL);
        DeregisterEventSource(source);
    }

    OutputDebugStringW(message);
}
