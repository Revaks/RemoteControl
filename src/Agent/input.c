#include <windows.h>
#include <string.h>
#include <stdio.h>
#include <stdarg.h>
#include <rfb/rfb.h>
#include "input.h"

// Отладочный лог ввода (разбираем «клавиша не дошла»). Включён, только если
// существует маркер C:\Windows\Temp\rc-input.enable — иначе на каждое нажатие
// шла бы запись в файл (это дорого и бьёт по таймингу инъекции).
static int input_trace_enabled(void)
{
    static int enabled = -1;
    if (enabled < 0)
        enabled = (GetFileAttributesA("C:\\Windows\\Temp\\rc-input.enable") != INVALID_FILE_ATTRIBUTES) ? 1 : 0;
    return enabled;
}

static void input_trace(const char *fmt, ...)
{
    if (!input_trace_enabled())
        return;

    FILE *f = fopen("C:\\Windows\\Temp\\rc-input.log", "a");
    if (f == NULL)
        return;

    va_list ap;
    va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);
    fputc('\n', f);
    fclose(f);
}

// ---------------------------------------------------------------------------
// Secure Attention Sequence (Ctrl+Alt+Del на удалённом столе).
// SendInput сгенерировать её не может — Windows блокирует SAS из пользовательского
// режима. Нужен экспорт SendSAS из sas.dll; он доступен процессу-службе, а наш
// helper запускается службой как SYSTEM в консольной сессии. Viewer присылает
// keysym 0xFFFFFF00 (см. MainWindow.SasKeysym).
// ---------------------------------------------------------------------------
#define SAS_KEYSYM 0xFFFFFF00

// Настоящая сигнатура: void WINAPI SendSAS(BOOL AsUser) — ошибку смотрим в GetLastError.
typedef void (WINAPI *send_sas_fn)(BOOL asUser);

// SendSAS(FALSE) требует включённой привилегии SeTcbPrivilege в токене процесса
// (в SYSTEM-токене она есть, но по умолчанию отключена). Иначе функция тихо
// ничего не делает — SAS не появляется.
static void enable_tcb_privilege(void)
{
    HANDLE token = NULL;
    if (!OpenProcessToken(GetCurrentProcess(),
                          TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token))
        return;

    LUID luid;
    if (LookupPrivilegeValueW(NULL, SE_TCB_NAME, &luid))
    {
        TOKEN_PRIVILEGES tp;
        tp.PrivilegeCount = 1;
        tp.Privileges[0].Luid = luid;
        tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
        BOOL ok = AdjustTokenPrivileges(token, FALSE, &tp, sizeof(tp), NULL, NULL);
        input_trace("SAS: AdjustTokenPrivileges(SeTcb) ok=%d err=%lu", (int)ok, GetLastError());
    }
    else
    {
        input_trace("SAS: LookupPrivilegeValue(SeTcb) failed: %lu", GetLastError());
    }

    CloseHandle(token);
}

static void send_secure_attention(void)
{
    static send_sas_fn send_sas = NULL;
    static int         resolved = 0;

    if (!resolved)
    {
        resolved = 1;
        enable_tcb_privilege();

        HMODULE sas = LoadLibraryA("sas.dll");
        if (sas != NULL)
            send_sas = (send_sas_fn)GetProcAddress(sas, "SendSAS");

        input_trace("SAS: sas.dll=%p SendSAS=%p", (void *)sas, (void *)send_sas);
    }

    if (send_sas != NULL)
    {
        enable_tcb_privilege();
        send_sas(FALSE);
        input_trace("SAS: SendSAS(FALSE) done, err=%lu", GetLastError());
    }
}

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

// Заполняет KEYBDINPUT для виртуальной клавиши.
//
// Инъектим скан-кодом (KEYEVENTF_SCANCODE), а не виртуальной клавишей: так
// событие максимально похоже на аппаратное и его принимают современные
// (UWP/WinUI) приложения. С wVk-инъекцией «Блокнот» в Win11 не получал
// Enter/стрелки, хотя SendInput возвращал успех, а Unicode-символы доходили.
static void fill_vk(INPUT *in, WORD vk, rfbBool down)
{
    WORD scan = (WORD)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);

    BOOL extended = is_extended_vk(vk);

    if (scan == 0)
    {
        // В текущей раскладке скан-кода нет — откатываемся на виртуальную клавишу.
        in->ki.wVk = vk;
        in->ki.wScan = 0;
        in->ki.dwFlags = down ? 0 : KEYEVENTF_KEYUP;
    }
    else
    {
        in->ki.wVk = 0;
        in->ki.wScan = scan;
        in->ki.dwFlags = KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP);
    }

    if (extended)
        in->ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
}

// Кто сейчас в фокусе — для разбора «клавиша ушла не туда».
static void trace_foreground(void)
{
    HWND fg = GetForegroundWindow();
    if (fg == NULL)
    {
        input_trace("  fg=<null>");
        return;
    }

    DWORD pid = 0;
    GetWindowThreadProcessId(fg, &pid);

    wchar_t cls[64] = { 0 };
    GetClassNameW(fg, cls, 64);

    input_trace("  fg=0x%p pid=%lu class=%ls", (void *)fg, pid, cls);
}

// Печатаемый символ -> виртуальная клавиша (для акселераторов Ctrl/Alt+клавиша).
static WORD char_to_vk(rfbKeySym key)
{
    if (key >= 0x61 && key <= 0x7a) return (WORD)(key - 0x20); // 'a'..'z' -> VK 0x41..0x5A
    if (key >= 0x41 && key <= 0x5a) return (WORD)key;          // 'A'..'Z'
    if (key >= 0x30 && key <= 0x39) return (WORD)key;          // '0'..'9'

    switch (key)
    {
        case 0x2d: return VK_OEM_MINUS;   // '-'
        case 0x3d: return VK_OEM_PLUS;    // '='
        case 0x2c: return VK_OEM_COMMA;   // ','
        case 0x2e: return VK_OEM_PERIOD;  // '.'
        case 0x2f: return VK_OEM_2;       // '/'
        case 0x3b: return VK_OEM_1;       // ';'
        case 0x27: return VK_OEM_7;       // '\''
        case 0x5b: return VK_OEM_4;       // '['
        case 0x5d: return VK_OEM_6;       // ']'
        case 0x5c: return VK_OEM_5;       // '\'
        case 0x60: return VK_OEM_3;       // '`'
        default: return 0;
    }
}

static BOOL g_ctrlDown = FALSE;
static BOOL g_altDown = FALSE;

static void send_key(rfbBool down, rfbKeySym key)
{
    input_trace("send_key key=0x%04X down=%d ctrl=%d alt=%d",
                (unsigned)key, (int)down, (int)g_ctrlDown, (int)g_altDown);
    trace_foreground();

    // SAS приходит парой down/up — реагируем только на нажатие.
    if (key == SAS_KEYSYM)
    {
        if (down)
            send_secure_attention();
        return;
    }

    // Отслеживаем Ctrl/Alt: при зажатом модификаторе печатаемые клавиши нужно
    // слать виртуальной клавишей, иначе не работают Ctrl+C, Alt+Y и т.п.
    switch (key)
    {
        case 0xffe3: case 0xffe4: g_ctrlDown = down; break;
        case 0xffe9: case 0xffea: g_altDown = down; break;
        default: break;
    }

    INPUT in;
    ZeroMemory(&in, sizeof(in));
    in.type = INPUT_KEYBOARD;

    if (key >= 0x20 && key <= 0x7e)
    {
        if (g_ctrlDown || g_altDown)
        {
            WORD vk = char_to_vk(key);
            if (vk != 0)
            {
                fill_vk(&in, vk, down);
                UINT sent = SendInput(1, &in, sizeof(in));
                input_trace("  ctrl/alt vk=0x%02X scan=0x%02X flags=0x%04X sent=%u err=%lu",
                            vk, in.ki.wScan, in.ki.dwFlags, sent, GetLastError());
                return;
            }
        }

        // По умолчанию — Unicode-символ: не зависит от локальной раскладки.
        in.ki.wVk = 0;
        in.ki.wScan = (WORD)key;
        in.ki.dwFlags = KEYEVENTF_UNICODE | (down ? 0 : KEYEVENTF_KEYUP);
    }
    else
    {
        WORD vk = keysym_to_vk(key);
        if (vk == 0)
        {
            input_trace("  vk=0 -> drop");
            return;
        }
        fill_vk(&in, vk, down);
    }

    {
        UINT sent = SendInput(1, &in, sizeof(in));
        input_trace("  send vk=0x%02X scan=0x%02X flags=0x%04X sent=%u err=%lu",
                    in.ki.wVk, in.ki.wScan, in.ki.dwFlags, sent, GetLastError());
    }
}

static int g_prevButtonMask = 0;

static void send_pointer(int buttonMask, int x, int y)
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

// ---------- очередь событий ----------

typedef struct
{
    int        type;   // 0 — клавиша, 1 — указатель
    int        down;   // для клавиши
    rfbKeySym  key;    // для клавиши
    int        mask;   // для указателя
    int        x;
    int        y;
} input_event;

#define INPUT_QUEUE_SIZE 2048

static input_event       s_queue[INPUT_QUEUE_SIZE];
static volatile LONG     s_head;
static volatile LONG     s_tail;
static CRITICAL_SECTION  s_lock;

void input_init(void)
{
    InitializeCriticalSection(&s_lock);
    s_head = 0;
    s_tail = 0;
}

static void enqueue(const input_event* e)
{
    EnterCriticalSection(&s_lock);
    LONG next = (s_tail + 1) % INPUT_QUEUE_SIZE;
    if (next != s_head) // при переполнении событие теряется
    {
        s_queue[s_tail] = *e;
        s_tail = next;
    }
    LeaveCriticalSection(&s_lock);
}

void input_queue_key(rfbBool down, rfbKeySym keysym)
{
    input_event e;
    ZeroMemory(&e, sizeof(e));
    e.type = 0;
    e.down = down ? 1 : 0;
    e.key = keysym;
    enqueue(&e);
}

void input_queue_pointer(int buttonMask, int x, int y)
{
    input_event e;
    ZeroMemory(&e, sizeof(e));
    e.type = 1;
    e.mask = buttonMask;
    e.x = x;
    e.y = y;
    enqueue(&e);
}

void input_process_pending(void)
{
    for (;;)
    {
        input_event e;

        EnterCriticalSection(&s_lock);
        if (s_head == s_tail)
        {
            LeaveCriticalSection(&s_lock);
            break;
        }
        e = s_queue[s_head];
        s_head = (s_head + 1) % INPUT_QUEUE_SIZE;
        LeaveCriticalSection(&s_lock);

        if (e.type == 0)
            send_key(e.down ? TRUE : FALSE, e.key);
        else
            send_pointer(e.mask, e.x, e.y);
    }
}
