#include <windows.h>
#include <string.h>
#include <rfb/rfb.h>
#include "input.h"

// Сопоставление X11 keysym -> виртуальный код Windows (для непечатаемых клавиш).
static WORD keysym_to_vk(rfbKeySym key)
{
    switch (key)
    {
        case 0xff08: return VK_BACK;
        case 0xff09: return VK_TAB;
        case 0xff0d: return VK_RETURN;
        case 0xff1b: return VK_ESCAPE;
        case 0x0020: return VK_SPACE;
        case 0xff50: return VK_HOME;
        case 0xff51: return VK_LEFT;
        case 0xff52: return VK_UP;
        case 0xff53: return VK_RIGHT;
        case 0xff54: return VK_DOWN;
        case 0xff55: return VK_PRIOR;
        case 0xff56: return VK_NEXT;
        case 0xff57: return VK_END;
        case 0xff63: return VK_INSERT;
        case 0xffff: return VK_DELETE;
        case 0xffe1: return VK_LSHIFT;
        case 0xffe2: return VK_RSHIFT;
        case 0xffe3: return VK_LCONTROL;
        case 0xffe4: return VK_RCONTROL;
        case 0xffe9: return VK_LMENU;
        case 0xffea: return VK_RMENU;
        case 0xffeb: return VK_LWIN;
        case 0xffec: return VK_RWIN;
        case 0xffe5: return VK_CAPITAL;
        case 0xff7f: return VK_NUMLOCK;
        case 0xff14: return VK_SCROLL;
        default: break;
    }

    if (key >= 0xffbe && key <= 0xffc9) // F1..F12
        return (WORD)(VK_F1 + (key - 0xffbe));
    if (key >= 0xffb0 && key <= 0xffb9) // NumPad 0..9
        return (WORD)(VK_NUMPAD0 + (key - 0xffb0));

    return 0;
}

static int is_extended_vk(WORD vk)
{
    switch (vk)
    {
        case VK_LEFT: case VK_RIGHT: case VK_UP: case VK_DOWN:
        case VK_HOME: case VK_END: case VK_PRIOR: case VK_NEXT:
        case VK_INSERT: case VK_DELETE:
        case VK_LWIN: case VK_RWIN: case VK_RMENU: case VK_RCONTROL:
            return 1;
        default:
            return 0;
    }
}

void input_send_key(rfbBool down, rfbKeySym key)
{
    INPUT in;
    ZeroMemory(&in, sizeof(in));
    in.type = INPUT_KEYBOARD;

    if (key >= 0x20 && key <= 0x7e)
    {
        // Печатаемые ASCII отправляем Unicode-символом — не зависит от локальной раскладки.
        in.ki.wVk = 0;
        in.ki.wScan = (WORD)key;
        in.ki.dwFlags = KEYEVENTF_UNICODE | (down ? 0 : KEYEVENTF_KEYUP);
    }
    else
    {
        WORD vk = keysym_to_vk(key);
        if (vk == 0)
            return;
        in.ki.wVk = vk;
        in.ki.dwFlags = down ? 0 : KEYEVENTF_KEYUP;
        if (is_extended_vk(vk))
            in.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
    }

    SendInput(1, &in, sizeof(in));
}

static int g_prevButtonMask = 0;

void input_send_pointer(int buttonMask, int x, int y)
{
    int width = GetSystemMetrics(SM_CXSCREEN);
    int height = GetSystemMetrics(SM_CYSCREEN);
    if (width <= 0 || height <= 0)
        return;

    INPUT in;
    ZeroMemory(&in, sizeof(in));
    in.type = INPUT_MOUSE;

    // Абсолютные координаты в виртуальном рабочем столе (0..65535).
    in.mi.dx = (LONG)(((long long)x * 65535) / (width - 1));
    in.mi.dy = (LONG)(((long long)y * 65535) / (height - 1));
    in.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;

    int changed = buttonMask ^ g_prevButtonMask;
    if (changed & 0x01) in.mi.dwFlags |= (buttonMask & 0x01) ? MOUSEEVENTF_LEFTDOWN   : MOUSEEVENTF_LEFTUP;
    if (changed & 0x02) in.mi.dwFlags |= (buttonMask & 0x02) ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
    if (changed & 0x04) in.mi.dwFlags |= (buttonMask & 0x04) ? MOUSEEVENTF_RIGHTDOWN  : MOUSEEVENTF_RIGHTUP;

    // Колесо: бит 3 = вверх, бит 4 = вниз.
    if (changed & 0x08)
    {
        in.mi.mouseData = WHEEL_DELTA;
        in.mi.dwFlags |= MOUSEEVENTF_WHEEL;
    }
    else if (changed & 0x10)
    {
        in.mi.mouseData = (DWORD)(-(LONG)WHEEL_DELTA);
        in.mi.dwFlags |= MOUSEEVENTF_WHEEL;
    }

    SendInput(1, &in, sizeof(in));

    // Колесо не «держим»: следующее событие без битов колеса не должно вызывать повторный скролл.
    g_prevButtonMask = buttonMask & 0x07;
}
