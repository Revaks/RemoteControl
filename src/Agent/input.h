#pragma once

#include <rfb/rfb.h>

// Ввод устроен так: RFB-колбэки (главный поток) только кладут события в очередь,
// а сам SendInput выполняется в потоке захвата — он привязан к активному рабочему
// столу (Default/Winlogon), поэтому ввод доходит и до защищённых окон (UAC,
// экран блокировки), где SendInput из другого потока/стола не работает.

void input_init(void);

// Очередь событий (главный поток, RFB-колбэки).
void input_queue_key(rfbBool down, rfbKeySym keysym);
void input_queue_pointer(int buttonMask, int x, int y);

// Отправляет накопленные события через SendInput.
// Вызывается из потока, привязанного к активному рабочему столу.
void input_process_pending(void);
