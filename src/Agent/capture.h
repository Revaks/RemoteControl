#pragma once

#include <windows.h>

// GDI-захват экрана (MVP). TODO(этап 5): DXGI Desktop Duplication + мультимонитор.
// Возврат capture_screen(): 1 = кадр изменился (скопирован в dstBgra),
// 0 = кадр не изменился, -1 = ошибка.
// Если кадр изменился, в outX/outY/outW/outH записывается ограничивающий
// прямоугольник изменённых пикселей (координаты фреймбуфера) — позволяет
// отправлять клиенту только грязную область вместо полного кадра.
int  capture_init(int width, int height);
int  capture_screen(void* dstBgra, int width, int height, int stride,
                    int* outX, int* outY, int* outW, int* outH);
void capture_cleanup(void);

// Переключает захват на DC физического дисплея (CreateDC("DISPLAY")) вместо
// GetDC(NULL). Нужно для защищённого рабочего стола Winlogon: DC текущего
// рабочего стола там не даёт кадров, а DISPLAY DC отдаёт физический экран.
// Вызывать до capture_init().
void capture_set_display_dc(void);
