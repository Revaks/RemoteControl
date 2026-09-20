#pragma once

#include <rfb/rfb.h>

void input_send_key(rfbBool down, rfbKeySym keysym);
void input_send_pointer(int buttonMask, int x, int y);
