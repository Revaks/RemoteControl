#include <windows.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#include "capture.h"
#include "dxgi_capture.h"
#include "input.h"
#include "eventlog.h"

static int s_width;
static int s_height;

static HDC      s_memDc;        // DC кадра (s_bitmap выбран), сюда рисуем курсор
static HDC      s_cleanDc;      // DC «чистого» стола (s_cleanBitmap выбран)
static HBITMAP  s_bitmap;
static HBITMAP  s_cleanBitmap;
static HGDIOBJ  s_oldBitmap;
static HGDIOBJ  s_oldCleanBitmap;
static void*    s_bits;         // кадр: рабочий стол + курсор
static void*    s_clean;        // рабочий стол без курсора

static HDC      s_screenDc;     // DC источника для активного стола
static BOOL     s_screenDcIsDisplay;

static dxgi_capture* s_dxgi;
static BOOL          s_dxgiFailed;

static HANDLE           s_thread;
static volatile LONG    s_stop;
static BOOL             s_lockInit;
static CRITICAL_SECTION s_lock;
static int              s_frameReady;
static BOOL             s_needInitialGrab;

static wchar_t s_deskName[64];
static HDESK   s_desk;
static BOOL    s_secureDesk;

static BOOL     s_cursorValid;
static BOOL     s_cursorShowing;
static POINT    s_cursorPos;
static HCURSOR  s_cursorHandle;

// ---------- вспомогательное ----------

static HBITMAP create_dib(HDC dc, int w, int h, void** bits)
{
    BITMAPINFO bi;
    ZeroMemory(&bi, sizeof(bi));
    bi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth       = w;
    bi.bmiHeader.biHeight      = -h;   // top-down DIB
    bi.bmiHeader.biPlanes      = 1;
    bi.bmiHeader.biBitCount    = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    void* p = NULL;
    HBITMAP bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &p, NULL, 0);
    if (bmp == NULL)
        return NULL;
    *bits = p;
    return bmp;
}

// ---------- смена активного рабочего стола ----------

static void close_capture_dc(void)
{
    if (s_screenDc != NULL)
    {
        if (s_screenDcIsDisplay)
            DeleteDC(s_screenDc);
        else
            ReleaseDC(NULL, s_screenDc);
        s_screenDc = NULL;
    }
}

static void open_capture_dc(void)
{
    close_capture_dc();

    if (s_secureDesk)
    {
        // DC текущего стола на защищённом рабочем столе кадров не даёт —
        // берём DC физического дисплея.
        s_screenDc = CreateDCW(L"DISPLAY", NULL, NULL, NULL);
        s_screenDcIsDisplay = TRUE;
    }
    else
    {
        s_screenDc = GetDC(NULL);
        s_screenDcIsDisplay = FALSE;
    }
}

static void on_desktop_changed(void)
{
    if (s_secureDesk)
    {
        // На защищённом столе DXGI не используем: там надёжнее GDI + DISPLAY DC.
        dxgi_capture_destroy(s_dxgi);
        s_dxgi = NULL;
    }
    else if (s_dxgi == NULL && !s_dxgiFailed)
    {
        s_dxgi = dxgi_capture_create(s_width, s_height);
        if (s_dxgi == NULL)
            s_dxgiFailed = TRUE;
    }

    open_capture_dc();
    s_needInitialGrab = TRUE;
    s_cursorValid = FALSE;
}

static void follow_input_desktop(void)
{
    HDESK d = OpenInputDesktop(0, FALSE,
        DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_CREATEMENU |
        DESKTOP_CREATEWINDOW | DESKTOP_ENUMERATE | DESKTOP_HOOKCONTROL |
        DESKTOP_JOURNALPLAYBACK | DESKTOP_JOURNALRECORD | DESKTOP_SWITCHDESKTOP);
    if (d == NULL)
        return;

    wchar_t name[64] = { 0 };
    DWORD need = 0;
    if (!GetUserObjectInformationW(d, UOI_NAME, name, (DWORD)sizeof(name), &need))
    {
        CloseDesktop(d);
        return;
    }

    if (s_deskName[0] != L'\0' && _wcsicmp(name, s_deskName) == 0)
    {
        CloseDesktop(d);
        return;
    }

    BOOL attached = SetThreadDesktop(d);
    if (!attached)
    {
        rc_event_log(EVENTLOG_WARNING_TYPE, 1360,
            L"SetThreadDesktop(%s) failed: %lu", name, GetLastError());
    }

    if (s_desk != NULL)
        CloseDesktop(s_desk);
    s_desk = attached ? d : NULL;
    if (!attached)
        CloseDesktop(d);

    wcsncpy_s(s_deskName, _countof(s_deskName), name, _TRUNCATE);
    s_secureDesk = (_wcsicmp(name, L"Winlogon") == 0 || _wcsicmp(name, L"Secure") == 0);

    rc_event_log(EVENTLOG_INFORMATION_TYPE, 1361,
        L"Активный рабочий стол: %s (%s)", name, s_secureDesk ? L"secure" : L"user");

    on_desktop_changed();
}

// ---------- захват ----------

// GDI BitBlt в «чистый» буфер. Используется как фолбэк DXGI и как основной
// путь на защищённом рабочем столе.
static int grab_gdi(void)
{
    if (s_screenDc == NULL || s_cleanDc == NULL)
        return 0;

    if (!BitBlt(s_cleanDc, 0, 0, s_width, s_height, s_screenDc, 0, 0, SRCCOPY | CAPTUREBLT))
        return 0;

    GdiFlush();
    return 1;
}

static BOOL cursor_changed(void)
{
    CURSORINFO ci;
    ZeroMemory(&ci, sizeof(ci));
    ci.cbSize = sizeof(ci);
    if (!GetCursorInfo(&ci))
        return FALSE;

    BOOL showing = (ci.flags & CURSOR_SHOWING) != 0;
    BOOL changed;

    if (!s_cursorValid)
        changed = TRUE;
    else if (showing != s_cursorShowing)
        changed = TRUE;
    else if (showing && (ci.ptScreenPos.x != s_cursorPos.x ||
                         ci.ptScreenPos.y != s_cursorPos.y ||
                         ci.hCursor != s_cursorHandle))
        changed = TRUE;
    else
        changed = FALSE;

    s_cursorValid = TRUE;
    s_cursorShowing = showing;
    s_cursorPos = ci.ptScreenPos;
    s_cursorHandle = ci.hCursor;
    return changed;
}

// Формирует кадр: копия «чистого» стола + курсор.
static void compose(void)
{
    memcpy(s_bits, s_clean, (size_t)s_width * s_height * 4);

    CURSORINFO ci;
    ZeroMemory(&ci, sizeof(ci));
    ci.cbSize = sizeof(ci);
    if (GetCursorInfo(&ci) && (ci.flags & CURSOR_SHOWING) && ci.hCursor != NULL)
    {
        ICONINFO ii;
        ZeroMemory(&ii, sizeof(ii));
        if (GetIconInfo(ci.hCursor, &ii))
        {
            int hotX = (int)ii.xHotspot;
            int hotY = (int)ii.yHotspot;
            if (ii.hbmColor != NULL) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != NULL) DeleteObject(ii.hbmMask);

            DrawIconEx(s_memDc,
                ci.ptScreenPos.x - hotX,
                ci.ptScreenPos.y - hotY,
                ci.hCursor, 0, 0, 0, NULL, DI_NORMAL);
        }
    }

    GdiFlush();
}

static void publish(void)
{
    EnterCriticalSection(&s_lock);
    compose();
    s_frameReady = 1;
    LeaveCriticalSection(&s_lock);
}

static DWORD WINAPI capture_worker(LPVOID param)
{
    (void)param;

    while (!s_stop)
    {
        follow_input_desktop();
        input_process_pending();

        int gotFrame = 0;

        if (s_secureDesk || s_dxgi == NULL)
        {
            gotFrame = grab_gdi();
        }
        else
        {
            int r = dxgi_capture_grab(s_dxgi, s_clean, s_width * 4, 5);
            if (r == 1)
            {
                gotFrame = 1;
            }
            else if (r < 0)
            {
                // Смена режима/потеря доступа — пересоздаём дупликатор.
                dxgi_capture_destroy(s_dxgi);
                s_dxgi = NULL;
                rc_event_log(EVENTLOG_WARNING_TYPE, 1362,
                    L"DXGI: доступ потерян, повторная инициализация");
                s_dxgi = dxgi_capture_create(s_width, s_height);
                if (s_dxgi == NULL)
                    s_dxgiFailed = TRUE;
                gotFrame = grab_gdi();
            }
            else if (s_needInitialGrab)
            {
                gotFrame = grab_gdi();
            }
        }

        if (gotFrame)
            s_needInitialGrab = FALSE;

        if (gotFrame || cursor_changed())
            publish();
    }

    return 0;
}

// ---------- публичный интерфейс ----------

int capture_start(int width, int height)
{
    if (width <= 0 || height <= 0)
        return -1;

    s_width = width;
    s_height = height;

    HDC ref = GetDC(NULL);
    if (ref == NULL)
        return -1;

    s_memDc   = CreateCompatibleDC(ref);
    s_cleanDc = CreateCompatibleDC(ref);
    ReleaseDC(NULL, ref);

    if (s_memDc == NULL || s_cleanDc == NULL)
        return -1;

    s_bitmap      = create_dib(s_memDc, width, height, &s_bits);
    s_cleanBitmap = create_dib(s_cleanDc, width, height, &s_clean);
    if (s_bitmap == NULL || s_cleanBitmap == NULL)
        return -1;

    s_oldBitmap      = SelectObject(s_memDc, s_bitmap);
    s_oldCleanBitmap = SelectObject(s_cleanDc, s_cleanBitmap);

    memset(s_clean, 0, (size_t)width * height * 4);
    memset(s_bits, 0, (size_t)width * height * 4);

    InitializeCriticalSection(&s_lock);
    s_lockInit = TRUE;
    input_init();

    s_thread = CreateThread(NULL, 0, capture_worker, NULL, 0, NULL);
    if (s_thread == NULL)
        return -1;

    return 0;
}

void capture_stop(void)
{
    if (s_thread != NULL)
    {
        InterlockedExchange(&s_stop, 1);
        WaitForSingleObject(s_thread, 5000);
        CloseHandle(s_thread);
        s_thread = NULL;
    }

    dxgi_capture_destroy(s_dxgi);
    s_dxgi = NULL;
    close_capture_dc();

    if (s_desk != NULL)
    {
        CloseDesktop(s_desk);
        s_desk = NULL;
    }

    if (s_lockInit)
    {
        DeleteCriticalSection(&s_lock);
        s_lockInit = FALSE;
    }

    if (s_memDc != NULL)
    {
        if (s_oldBitmap != NULL) SelectObject(s_memDc, s_oldBitmap);
        if (s_bitmap != NULL) DeleteObject(s_bitmap);
        DeleteDC(s_memDc);
        s_memDc = NULL;
        s_bitmap = NULL;
    }
    if (s_cleanDc != NULL)
    {
        if (s_oldCleanBitmap != NULL) SelectObject(s_cleanDc, s_oldCleanBitmap);
        if (s_cleanBitmap != NULL) DeleteObject(s_cleanBitmap);
        DeleteDC(s_cleanDc);
        s_cleanDc = NULL;
        s_cleanBitmap = NULL;
    }
}

static int compare_and_copy(const BYTE* src, int srcStride, BYTE* dst, int dstStride,
                            int* outX, int* outY, int* outW, int* outH)
{
    int changed = 0;
    int minX = s_width, minY = s_height, maxX = -1, maxY = -1;
    size_t rowBytes = (size_t)s_width * 4;

    for (int y = 0; y < s_height; y++)
    {
        const BYTE* s = src + (size_t)y * srcStride;
        BYTE* d = dst + (size_t)y * dstStride;

        if (memcmp(s, d, rowBytes) != 0)
        {
            changed = 1;

            const DWORD* a = (const DWORD*)s;
            const DWORD* b = (const DWORD*)d;
            int first = -1, last = -1;
            for (int x = 0; x < s_width; x++)
            {
                if (a[x] != b[x])
                {
                    if (first < 0) first = x;
                    last = x;
                }
            }

            if (first >= 0)
            {
                if (first < minX) minX = first;
                if (last > maxX) maxX = last;
            }
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;

            memcpy(d, s, rowBytes);
        }
    }

    if (changed)
    {
        if (minX > maxX || minY > maxY)
        {
            minX = 0;
            minY = 0;
            maxX = s_width - 1;
            maxY = s_height - 1;
        }
        if (outX) *outX = minX;
        if (outY) *outY = minY;
        if (outW) *outW = maxX - minX + 1;
        if (outH) *outH = maxY - minY + 1;
    }

    return changed;
}

int capture_take_dirty(void* dstBgra, int stride,
                       int* outX, int* outY, int* outW, int* outH)
{
    int changed = 0;

    EnterCriticalSection(&s_lock);
    if (s_frameReady)
    {
        s_frameReady = 0;
        changed = compare_and_copy((const BYTE*)s_bits, s_width * 4,
                                   (BYTE*)dstBgra, stride, outX, outY, outW, outH);
    }
    LeaveCriticalSection(&s_lock);

    return changed;
}
