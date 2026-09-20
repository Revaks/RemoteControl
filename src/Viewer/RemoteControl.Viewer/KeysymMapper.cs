using System.Windows.Input;

namespace RemoteControl.Viewer;

/// <summary>
/// Сопоставление клавиш WPF с keysym X11 (RFC 6143). Покрывает базовый набор;
/// TODO(этап 5): полная раскладка с учётом Shift/AltGr и русской раскладки.
/// </summary>
public static class KeysymMapper
{
    public static bool TryGetKeysym(Key key, out uint keysym)
    {
        // Буквы и цифры основного ряда
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
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            keysym = (uint)(0xffb0 + (key - Key.NumPad0));
            return true;
        }
        if (key >= Key.F1 && key <= Key.F12)
        {
            keysym = (uint)(0xffbe + (key - Key.F1));
            return true;
        }

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
            Key.OemMinus   => 0x002d, // '-'
            Key.OemPlus    => 0x003d, // '='
            Key.OemComma   => 0x002c, // ','
            Key.OemPeriod  => 0x002e, // '.'
            Key.OemQuestion => 0x002f, // '/'
            Key.OemSemicolon => 0x003b, // ';'
            Key.OemQuotes  => 0x0027, // '\''
            Key.OemOpenBrackets  => 0x005b, // '['
            Key.OemCloseBrackets => 0x005d, // ']'
            Key.OemBackslash     => 0x005c, // '\'
            Key.OemTilde         => 0x0060, // '`'
            _ => 0,
        };

        return keysym != 0;
    }
}
