#pragma once

#include <windows.h>

// Захват экрана. Работает в отдельном потоке, который привязан к активному
// рабочему столу (Default или Winlogon) и потому видит в том числе:
//   * обычный рабочий стол пользователя — через DXGI Desktop Duplication
//     (быстро), с фолбэком на GDI BitBlt;
//   * защищённый рабочий стол (UAC, экран блокировки/Ctrl+Alt+Del) — через
//     GDI BitBlt с DC физического дисплея (CreateDC("DISPLAY")).
// Поток захвата также отправляет накопленный ввод (SendInput) — иначе ввод
// не доходил бы до защищённых окон.

// Инициализация и запуск потока захвата. 0 — успех, -1 — ошибка.
int capture_start(int width, int height);
void capture_stop(void);

// Забирает сформированный кадр: сравнивает его с фреймбуфером dstBgra,
// копирует изменения и возвращает ограничивающий прямоугольник.
// Возврат: 1 — есть изменения (outX/outY/outW/outH заполнены), 0 — нет.
int capture_take_dirty(void* dstBgra, int stride,
                       int* outX, int* outY, int* outW, int* outH);
