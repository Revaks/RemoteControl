#pragma once

// Захват рабочего стола через DXGI Desktop Duplication (D3D11).
// Быстрее GDI BitBlt на виртуальных адаптерах и отдаёт кадр только тогда,
// когда рабочий стол реально изменился.

typedef struct dxgi_capture dxgi_capture;

// Создаёт D3D11-устройство и дупликатор вывода монитора 0.
// Возвращает NULL, если Desktop Duplication недоступен.
dxgi_capture* dxgi_capture_create(int width, int height);

// Забирает очередной кадр в dstBgra (BGRA32, построчный шаг dstStride).
// Возвращает:
//    1 — кадр скопирован,
//    0 — нового кадра нет (timeout), содержимое dstBgra не менялось,
//   -1 — ошибка (дупликатор нужно пересоздать).
int dxgi_capture_grab(dxgi_capture* c, void* dstBgra, int dstStride, int timeoutMs);

void dxgi_capture_destroy(dxgi_capture* c);
