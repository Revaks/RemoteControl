using System.Runtime.InteropServices;
using System.Windows.Input;

namespace RemoteControl.Viewer;

/// <summary>
/// Низкоуровневый хук клавиатуры (WH_KEYBOARD_LL).
///
/// Зачем: WPF-события <c>PreviewKeyDown/Up</c> не видят системные сочетания
/// (Win, Alt+Tab, Ctrl+Esc, Alt+Esc), а сама Windows обрабатывает их локально.
/// Из-за этого нажатие Win открывало «Пуск» и на удалённой машине, и на локальной
/// (дублирование). Хук перехватывает клавиши раньше оболочки, отдаёт их в RFB и
/// НЕ пропускает в локальную ОС, поэтому хоткеи уходят только на удалённый стол.
///
/// Хук активен, только пока окно консоли в фокусе и фокус не в локальном поле
/// ввода (иначе нельзя было бы печатать в «Компьютер»/поиск).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint LLKHF_INJECTED = 0x10;

    private const uint VK_SHIFT = 0x10;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU = 0x12;   // Alt
    private const uint VK_CAPITAL = 0x14;
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4;
    private const uint VK_RMENU = 0xA5;
    private const uint VK_END = 0x23;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private HookProc? _proc;   // держим ссылку, иначе GC соберёт делегат
    private IntPtr _hook = IntPtr.Zero;

    private bool _shift;
    private bool _ctrl;
    private bool _alt;
    private bool _caps;

    /// <summary>keysym, down. Имя с суффиксом, чтобы не затенять тип
    /// <see cref="System.Windows.Input.Key"/> (иначе не видно Key.None и т.п.).</summary>
    public event Action<uint, bool>? KeyEvent;

    /// <summary>Оператор запросил Secure Attention Sequence (Ctrl+Alt+Del).</summary>
    public event Action? SecureAttention;

    /// <summary>Разрешено ли перехватывать ввод прямо сейчас.</summary>
    public Func<bool>? CaptureAllowed { get; set; }

    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>Ставит хук. Возвращает false, если систему не удалось захукать —
    /// тогда viewer остаётся на событиях WPF.</summary>
    public bool Start()
    {
        if (_hook != IntPtr.Zero)
            return true;

        _proc = HookCallback;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc!, GetModuleHandleW(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _shift = _ctrl = _alt = _caps = false;
    }

    private static bool IsModifier(uint vk) =>
        vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU ||
        vk == VK_LSHIFT || vk == VK_RSHIFT || vk == VK_LCONTROL ||
        vk == VK_RCONTROL || vk == VK_LMENU || vk == VK_RMENU;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((data.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

        // Состояние модификаторов ведём сами: в этот момент Keyboard.Modifiers
        // ещё не отражает нажатую клавишу.
        bool isShift = data.vkCode == VK_SHIFT || data.vkCode == VK_LSHIFT || data.vkCode == VK_RSHIFT;
        bool isCtrl = data.vkCode == VK_CONTROL || data.vkCode == VK_LCONTROL || data.vkCode == VK_RCONTROL;
        bool isAlt = data.vkCode == VK_MENU || data.vkCode == VK_LMENU || data.vkCode == VK_RMENU;

        if (isShift) _shift = down;
        if (isCtrl) _ctrl = down;
        if (isAlt) _alt = down;
        if (data.vkCode == VK_CAPITAL && down) _caps = !_caps;

        if (CaptureAllowed is null || !CaptureAllowed())
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        // Локальный режим: Ctrl+Alt+Shift удерживается — клавиатура отдаётся ОС
        // (например, чтобы переключить окно или нажать локальный хоткей).
        if (_ctrl && _alt && _shift && !IsModifier(data.vkCode))
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        // Ctrl+Alt+End (как в RDP) — Secure Attention Sequence на удалённой машине.
        if (_ctrl && _alt && data.vkCode == VK_END)
        {
            if (down)
                SecureAttention?.Invoke();
            return (IntPtr)1;
        }

        Key key = KeyInterop.KeyFromVirtualKey((int)data.vkCode);
        if (key == Key.None)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        if (KeysymMapper.TryGetKeysym(key, _shift, _ctrl, _alt, _caps, out uint keysym))
        {
            KeyEvent?.Invoke(keysym, down);
            return (IntPtr)1;   // не пропускаем в локальную ОС
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
