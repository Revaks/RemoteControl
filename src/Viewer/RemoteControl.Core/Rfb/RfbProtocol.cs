namespace RemoteControl.Core.Rfb;

/// <summary>
/// Константы протокола RFB (RFC 6143) — сообщения клиент/сервер и кодировки.
/// </summary>
public static class RfbProtocol
{
    // Сообщения сервер -> клиент
    public const byte ServerFramebufferUpdate  = 0;
    public const byte ServerSetColourMapEntries = 1;
    public const byte ServerBell               = 2;
    public const byte ServerCutText            = 3;

    // Сообщения клиент -> сервер
    public const byte ClientSetPixelFormat       = 0;
    public const byte ClientSetEncodings         = 2;
    public const byte ClientFramebufferUpdateReq = 3;
    public const byte ClientKeyEvent             = 4;
    public const byte ClientPointerEvent         = 5;
    public const byte ClientCutText              = 6;

    // Security types
    public const byte SecurityTypeInvalid = 0;
    public const byte SecurityTypeNone    = 1;
    public const byte SecurityTypeVncAuth = 2;

    // Кодировки
    public const int EncodingRaw         = 0;
    public const int EncodingCopyRect    = 1;
    public const int EncodingRRE         = 2;
    public const int EncodingHextile     = 5;
    public const int EncodingZRLE        = 16;
    public const int EncodingDesktopSize = -223; // псевдо-кодировка
    public const int EncodingCursor      = -239; // псевдо-кодировка

    public static readonly byte[] VersionString = "RFB 003.008\n"u8.ToArray();
    public const int VersionLength = 12;

    /// <summary>Максимальный размер одного сообщения, который мы принимаем (защита от битых потоков).</summary>
    public const int MaxMessageSize = 64 * 1024 * 1024;
}
