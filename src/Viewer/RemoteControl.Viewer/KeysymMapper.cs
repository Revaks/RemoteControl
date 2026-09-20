using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace RemoteControl.Viewer;

/// <summary>
/// Сопоставление клавиш WPF с keysym X11 (RFC 6143).
/// Обычный ввод отдаёт НЕ скан-код, а реальный символ с учётом Shift/CapsLock и
/// текущей раскладки: агент вводит его как Unicode, поэтому пароли и текст
/// набираются в правильном регистре. Если зажат Ctrl/Alt — отдаём «базовый»
/// keysym, агент преобразует его в виртуальную клавишу (работают акселераторы
/// вида Ctrl+C, Alt+Y).
/// </summary>
public static class KeysymMapper
{
    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff,
        uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>Состояние модификаторов, снятое вызывающей стороной (хук или WPF).</summary>
    public readonly record struct KeyState(bool Shift, bool Ctrl, bool Alt, bool CapsLock);

    public static bool TryGetKeysym(Key key, ModifierKeys modifiers, out uint keysym)
    {
        bool caps = (Keyboard.GetKeyStates(Key.CapsLock) & KeyStates.Toggled) != 0;
        return TryGetKeysym(key,
            new KeyState(
                (modifiers & ModifierKeys.Shift) != 0,
                (modifiers & ModifierKeys.Control) != 0,
                (modifiers & ModifierKeys.Alt) != 0,
                caps),
            out keysym);
    }

    public static bool TryGetKeysym(Key key, bool shift, bool ctrl, bool alt, bool capsLock, out uint keysym) =>
        TryGetKeysym(key, new KeyState(shift, ctrl, alt, capsLock), out keysym);

    public static bool TryGetKeysym(Key key, KeyState state, out uint keysym)
    {
        // Спецклавиши с фиксированным keysym.
        keysym = key switch
        {
            Key.Back       => 0xff08,
            Key.Tab        => 0xff09,
            Key.Return     => 0xff0d,
            Key.Escape     => 0xff1b,
            Key.Space      => 0x0020,
            Key.Left       => 0xff51,
            Key.Up         => 0xff52,
            Key.Right      => 0xff53,
            Key.Down       => 0xff54,
            Key.Home       => 0xff50,
            Key.End        => 0xff57,
            Key.PageUp     => 0xff55,
            Key.PageDown   => 0xff56,
            Key.Insert     => 0xff63,
            Key.Delete     => 0xffff,
            Key.LeftShift  => 0xffe1,
            Key.RightShift => 0xffe2,
            Key.LeftCtrl   => 0xffe3,
            Key.RightCtrl  => 0xffe4,
            Key.LeftAlt    => 0xffe9,
            Key.RightAlt   => 0xffea,
            Key.LWin       => 0xffeb,
            Key.RWin       => 0xffec,
            // Переключатели: их состояние должно уходить на удалённый стол,
            // иначе CapsLock/NumLock включаются локально, а не там, где печатают.
            Key.CapsLock   => 0xffe5,
            Key.NumLock    => 0xff7f,
            _ => 0,
        };
        if (keysym != 0)
            return true;

        if (key >= Key.F1 && key <= Key.F12)
        {
            keysym = (uint)(0xffbe + (key - Key.F1));
            return true;
        }
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            keysym = (uint)(0xffb0 + (key - Key.NumPad0));
            return true;
        }

        // Ctrl/Alt: базовый keysym -> агент отправит виртуальную клавишу.
        if (state.Ctrl || state.Alt)
            return TryGetBaseKeysym(key, out keysym);

        // Обычный ввод: реальный символ (регистр, Shift, CapsLock, раскладка).
        if (TryGetChar(key, state, out uint ch))
        {
            keysym = ch;
            return true;
        }

        // Фолбэк, если трансляция символа не удалась.
        return TryGetBaseKeysym(key, out keysym);
    }

    private static bool TryGetBaseKeysym(Key key, out uint keysym)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            keysym = (uint)(0x61 + (key - Key.A)); // 'a'..'z'
            return true;
        }
        if (key >= Key.D0 && key <= Key.D9)
        {
            keysym = (uint)(0x30 + (key - Key.D0)); // '0'..'9'
            return true;
        }

        keysym = key switch
        {
            Key.OemMinus        => 0x002d, // '-'
            Key.OemPlus         => 0x003d, // '='
            Key.OemComma        => 0x002c, // ','
            Key.OemPeriod       => 0x002e, // '.'
            Key.OemQuestion     => 0x002f, // '/'
            Key.OemSemicolon    => 0x003b, // ';'
            Key.OemQuotes       => 0x0027, // '\''
            Key.OemOpenBrackets => 0x005b, // '['
            Key.OemCloseBrackets=> 0x005d, // ']'
            Key.OemBackslash    => 0x005c, // '\'
            Key.OemTilde        => 0x0060, // '`'
            _ => 0,
        };

        return keysym != 0;
    }

    /// <summary>Символ, который даст клавиша при заданных модификаторах и раскладке.</summary>
    private static bool TryGetChar(Key key, KeyState state, out uint codepoint)
    {
        codepoint = 0;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
            return false;

        byte[] kstate = new byte[256];
        if (state.Shift) kstate[0x10] = 0x80;
        if (state.Ctrl) kstate[0x11] = 0x80;
        if (state.Alt) kstate[0x12] = 0x80;
        if (state.CapsLock) kstate[0x14] = 1;

        uint scan = MapVirtualKey(vk, 0); // MAPVK_VK_TO_VSC
        var sb = new StringBuilder(8);

        int n = ToUnicodeEx(vk, scan, kstate, sb, sb.Capacity, 0, GetKeyboardLayout(0));
        if (n <= 0 || sb.Length == 0)
            return false;

        char c = sb[0];
        if (char.IsControl(c))
            return false;

        codepoint = c;
        return true;
    }
}
