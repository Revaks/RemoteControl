#include <windows.h>
#include <string.h>
#include "capture.h"
#include "eventlog.h"

static HDC      s_screenDc;
static HDC      s_memDc;
static HBITMAP  s_bitmap;
static HGDIOBJ  s_oldBitmap;
static void*    s_bits;
static int      s_width;
static int      s_height;
static BOOL     s_useDisplayDc;
static BOOL     s_bitbltErrorLogged;

void capture_set_display_dc(void)
{
    s_useDisplayDc = TRUE;
}

int capture_init(int width, int height)
{
    s_width = width;
    s_height = height;

    if (s_useDisplayDc)
        s_screenDc = CreateDCW(L"DISPLAY", NULL, NULL, NULL);
    else
        s_screenDc = GetDC(NULL);
    if (s_screenDc == NULL)
        return -1;

    s_memDc = CreateCompatibleDC(s_screenDc);
    if (s_memDc == NULL)
        return -1;

    BITMAPINFO bi;
    ZeroMemory(&bi, sizeof(bi));
    bi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth       = width;
    bi.bmiHeader.biHeight      = -height;   // отрицательная высота = top-down DIB
    bi.bmiHeader.biPlanes      = 1;
    bi.bmiHeader.biBitCount    = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    s_bitmap = CreateDIBSection(s_memDc, &bi, DIB_RGB_COLORS, &s_bits, NULL, 0);
    if (s_bitmap == NULL)
        return -1;

    s_oldBitmap = SelectObject(s_memDc, s_bitmap);

    return 0;
}

int capture_screen(void* dstBgra, int width, int height, int stride,
                   int* outX, int* outY, int* outW, int* outH)
{
    if (s_memDc == NULL || width != s_width || height != s_height)
        return -1;

    // CAPTUREBLT захватывает и слои поверх (важно для корректного скриншота).
    if (!BitBlt(s_memDc, 0, 0, width, height, s_screenDc, 0, 0, SRCCOPY | CAPTUREBLT))
    {
        if (!s_bitbltErrorLogged)
        {
            rc_event_log(EVENTLOG_ERROR_TYPE, 1330,
                L"BitBlt failed: %lu (useDisplayDc=%d)", GetLastError(), (int)s_useDisplayDc);
            s_bitbltErrorLogged = TRUE;
        }
        return -1;
    }

    // BitBlt не захватывает курсор мыши — дорисовываем его поверх кадра,
    // иначе на удалённой картинке курсора не видно.
    {
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
    }

    GdiFlush();

    // Если кадр не изменился — не копируем и не помечаем (экономит CPU и сеть).
    // Сравниваем новый кадр прямо с текущим фреймбуфером (dstBgra) построчно,
    // без отдельного буфера предыдущего кадра. Параллельно собираем
    // ограничивающий прямоугольник изменённых пикселей.
    int changed = 0;
    int minX = width, minY = height, maxX = -1, maxY = -1;

    BYTE* src = (BYTE*)s_bits;
    BYTE* dst = (BYTE*)dstBgra;
    size_t rowBytes = (size_t)width * 4;
    for (int y = 0; y < height; y++)
    {
        if (memcmp(src, dst, rowBytes) != 0)
        {
            if (!changed)
                changed = 1;

            // Первый и последний изменившийся пиксель в строке.
            const DWORD* a = (const DWORD*)src;
            const DWORD* b = (const DWORD*)dst;
            int first = -1, last = -1;
            for (int x = 0; x < width; x++)
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

            memcpy(dst, src, rowBytes);
        }
        src += rowBytes;
        dst += stride;
    }

    if (changed)
    {
        if (minX > maxX || minY > maxY)
        {
            minX = 0;
            minY = 0;
            maxX = width - 1;
            maxY = height - 1;
        }
        if (outX) *outX = minX;
        if (outY) *outY = minY;
        if (outW) *outW = maxX - minX + 1;
        if (outH) *outH = maxY - minY + 1;
    }
    return changed;
}

void capture_cleanup(void)
{
    if (s_memDc != NULL)
    {
        if (s_oldBitmap != NULL)
            SelectObject(s_memDc, s_oldBitmap);
        if (s_bitmap != NULL)
            DeleteObject(s_bitmap);
        DeleteDC(s_memDc);
        s_memDc = NULL;
    }
    if (s_screenDc != NULL)
    {
        if (s_useDisplayDc)
            DeleteDC(s_screenDc);
        else
            ReleaseDC(NULL, s_screenDc);
        s_screenDc = NULL;
    }
}
