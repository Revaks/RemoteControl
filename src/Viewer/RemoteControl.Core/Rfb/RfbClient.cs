namespace RemoteControl.Core.Rfb;

/// <summary>Аргументы события обновления кадра — список изменённых прямоугольников.</summary>
public sealed class FrameUpdateEventArgs : EventArgs
{
    public FrameUpdateEventArgs(IReadOnlyList<(int X, int Y, int W, int H)> rects)
        => Rectangles = rects;

    public IReadOnlyList<(int X, int Y, int W, int H)> Rectangles { get; }
}

/// <summary>
/// Клиент протокола RFB. Работает поверх уже установленного потока (у нас — SslStream с mTLS,
/// поэтому RFB-аутентификация не используется: сервер после TLS отдаёт SecurityType None).
/// </summary>
public sealed class RfbClient : IAsyncDisposable
{
    private readonly RfbStream _s;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private bool _disposed;

    public RfbClient(Stream stream)
    {
        _s = new RfbStream(stream);
        Framebuffer = new Framebuffer(1, 1);
    }

    public Framebuffer Framebuffer { get; }
    public string DesktopName { get; private set; } = string.Empty;
    public bool IsConnected { get; private set; }

    public event EventHandler<FrameUpdateEventArgs>? FrameUpdated;
    public event EventHandler? Bell;
    public event EventHandler<string>? ServerCutText;
    public event EventHandler? Disconnected;

    /// <summary>Выполняет RFB-хендшейк поверх уже установленного (TLS) соединения.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. Версия сервера (12 байт), затем отправляем свою.
        byte[] version = new byte[RfbProtocol.VersionLength];
        await _s.ReadExactAsync(version, ct).ConfigureAwait(false);
        if (!version.AsSpan().StartsWith("RFB "u8))
            throw new InvalidDataException("Сервер не является RFB/VNC.");

        await _s.WriteExactAsync(RfbProtocol.VersionString, ct).ConfigureAwait(false);

        // 2. Security types
        int securityTypeCount = await _s.ReadByteAsync(ct).ConfigureAwait(false);
        if (securityTypeCount == 0)
        {
            int reasonLength = checked((int)await _s.ReadUInt32Async(ct).ConfigureAwait(false));
            string reason = await _s.ReadLatin1Async(reasonLength, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Сервер отклонил подключение: {reason}");
        }

        var securityTypes = new byte[securityTypeCount];
        await _s.ReadExactAsync(securityTypes, ct).ConfigureAwait(false);
        if (!securityTypes.Contains(RfbProtocol.SecurityTypeNone))
            throw new NotSupportedException("Сервер не поддерживает None-аутентификацию после TLS.");

        await _s.WriteByteAsync(RfbProtocol.SecurityTypeNone, ct).ConfigureAwait(false);

        // 3. SecurityResult
        uint securityResult = await _s.ReadUInt32Async(ct).ConfigureAwait(false);
        if (securityResult != 0)
        {
            int reasonLength = checked((int)await _s.ReadUInt32Async(ct).ConfigureAwait(false));
            string reason = await _s.ReadLatin1Async(reasonLength, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Ошибка аутентификации: {reason}");
        }

        // 4. ClientInit (shared = 1)
        await _s.WriteByteAsync(1, ct).ConfigureAwait(false);

        // 5. ServerInit
        int width = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
        int height = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new InvalidDataException($"Некорректный размер экрана: {width}x{height}");

        // Пиксельный формат сервера (16 байт) — принимаем, но затем просим свой.
        byte[] pixelFormat = new byte[16];
        await _s.ReadExactAsync(pixelFormat, ct).ConfigureAwait(false);

        int nameLength = checked((int)await _s.ReadUInt32Async(ct).ConfigureAwait(false));
        DesktopName = await _s.ReadLatin1Async(nameLength, ct).ConfigureAwait(false);

        Framebuffer.Resize(width, height);

        // 6. Запрашиваем удобный нам формат: 32bpp BGRA (R=16,G=8,B=0).
        await SendSetPixelFormatAsync(ct).ConfigureAwait(false);

        // 7. Поддерживаемые кодировки.
        await SendSetEncodingsAsync(ct).ConfigureAwait(false);

        IsConnected = true;
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
    }

    private Task SendSetPixelFormatAsync(CancellationToken ct)
    {
        var payload = new byte[20];
        payload[0] = RfbProtocol.ClientSetPixelFormat;
        // 3 байта padding
        // PixelFormat: 32bpp, depth 24, little-endian, true colour
        payload[4] = 32;      // bits-per-pixel
        payload[5] = 24;      // depth
        payload[6] = 0;       // big-endian flag
        payload[7] = 1;       // true-colour flag
        // red/green/blue max = 255
        payload[8] = 0; payload[9] = 255;
        payload[10] = 0; payload[11] = 255;
        payload[12] = 0; payload[13] = 255;
        payload[14] = 16;     // red shift
        payload[15] = 8;      // green shift
        payload[16] = 0;      // blue shift
        // 3 байта padding
        return _s.WriteExactAsync(payload, ct);
    }

    private async Task SendSetEncodingsAsync(CancellationToken ct)
    {
        int[] encodings =
        {
            RfbProtocol.EncodingHextile,
            RfbProtocol.EncodingCopyRect,
            RfbProtocol.EncodingRaw,
            RfbProtocol.EncodingDesktopSize,
        };

        var payload = new byte[4 + encodings.Length * 4];
        payload[0] = RfbProtocol.ClientSetEncodings;
        // payload[1] — padding
        payload[2] = (byte)(encodings.Length >> 8);
        payload[3] = (byte)encodings.Length;
        for (int i = 0; i < encodings.Length; i++)
        {
            payload[4 + i * 4]     = (byte)(encodings[i] >> 24);
            payload[4 + i * 4 + 1] = (byte)(encodings[i] >> 16);
            payload[4 + i * 4 + 2] = (byte)(encodings[i] >> 8);
            payload[4 + i * 4 + 3] = (byte)encodings[i];
        }

        await _s.WriteExactAsync(payload, ct).ConfigureAwait(false);
    }

    public Task RequestUpdateAsync(bool incremental, CancellationToken ct)
    {
        int x = 0, y = 0;
        int w = Framebuffer.Width, h = Framebuffer.Height;

        var payload = new byte[10];
        payload[0] = RfbProtocol.ClientFramebufferUpdateReq;
        payload[1] = incremental ? (byte)1 : (byte)0;
        payload[2] = (byte)(x >> 8); payload[3] = (byte)x;
        payload[4] = (byte)(y >> 8); payload[5] = (byte)y;
        payload[6] = (byte)(w >> 8); payload[7] = (byte)w;
        payload[8] = (byte)(h >> 8); payload[9] = (byte)h;
        return _s.WriteExactAsync(payload, ct);
    }

    public Task SendKeyEventAsync(uint keySym, bool down, CancellationToken ct)
    {
        var payload = new byte[8];
        payload[0] = RfbProtocol.ClientKeyEvent;
        payload[1] = down ? (byte)1 : (byte)0;
        // payload[2..3] — padding
        payload[4] = (byte)(keySym >> 24);
        payload[5] = (byte)(keySym >> 16);
        payload[6] = (byte)(keySym >> 8);
        payload[7] = (byte)keySym;
        return _s.WriteExactAsync(payload, ct);
    }

    public Task SendPointerEventAsync(byte buttonMask, ushort x, ushort y, CancellationToken ct)
    {
        var payload = new byte[6];
        payload[0] = RfbProtocol.ClientPointerEvent;
        payload[1] = buttonMask;
        payload[2] = (byte)(x >> 8); payload[3] = (byte)x;
        payload[4] = (byte)(y >> 8); payload[5] = (byte)y;
        return _s.WriteExactAsync(payload, ct);
    }

    public Task SendClientCutTextAsync(string text, CancellationToken ct)
    {
        byte[] latin1 = System.Text.Encoding.Latin1.GetBytes(text);
        var payload = new byte[8 + latin1.Length];
        payload[0] = RfbProtocol.ClientCutText;
        // payload[1..3] — padding
        payload[4] = (byte)(latin1.Length >> 24);
        payload[5] = (byte)(latin1.Length >> 16);
        payload[6] = (byte)(latin1.Length >> 8);
        payload[7] = (byte)latin1.Length;
        latin1.CopyTo(payload, 8);
        return _s.WriteExactAsync(payload, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte messageType = await _s.ReadByteAsync(ct).ConfigureAwait(false);
                switch (messageType)
                {
                    case RfbProtocol.ServerFramebufferUpdate:
                        await HandleFramebufferUpdateAsync(ct).ConfigureAwait(false);
                        // Сразу запрашиваем следующий (инкрементальный) кадр, иначе
                        // экран обновится только один раз после подключения.
                        if (!ct.IsCancellationRequested)
                            await RequestUpdateAsync(incremental: true, ct).ConfigureAwait(false);
                        break;
                    case RfbProtocol.ServerSetColourMapEntries:
                        await SkipColourMapAsync(ct).ConfigureAwait(false);
                        break;
                    case RfbProtocol.ServerBell:
                        Bell?.Invoke(this, EventArgs.Empty);
                        break;
                    case RfbProtocol.ServerCutText:
                        await HandleServerCutTextAsync(ct).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException($"Неизвестный тип RFB-сообщения: {messageType}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // штатное завершение
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or System.Net.Sockets.SocketException)
        {
            // соединение разорвано
        }
        catch (Exception ex)
        {
            // Протокольная ошибка — логируем выше, здесь гасим, чтобы не ронять UI.
            Console.Error.WriteLine($"RFB receive error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsConnected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task HandleFramebufferUpdateAsync(CancellationToken ct)
    {
        await _s.ReadByteAsync(ct).ConfigureAwait(false); // padding после типа сообщения
        ushort rectCount = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
        var dirty = new List<(int, int, int, int)>(rectCount);

        for (int i = 0; i < rectCount; i++)
        {
            ushort x = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
            ushort y = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
            ushort w = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
            ushort h = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
            int encoding = await _s.ReadInt32Async(ct).ConfigureAwait(false);

            if (w == 0 || h == 0)
                continue;

            switch (encoding)
            {
                case RfbProtocol.EncodingRaw:
                    await Decoders.DecodeRawAsync(_s, Framebuffer, x, y, w, h, ct).ConfigureAwait(false);
                    break;
                case RfbProtocol.EncodingCopyRect:
                    await Decoders.DecodeCopyRectAsync(_s, Framebuffer, x, y, w, h, ct).ConfigureAwait(false);
                    break;
                case RfbProtocol.EncodingHextile:
                    await Decoders.DecodeHextileAsync(_s, Framebuffer, x, y, w, h, ct).ConfigureAwait(false);
                    break;
                case RfbProtocol.EncodingDesktopSize:
                    int newW = w, newH = h;
                    Framebuffer.Resize(newW, newH);
                    _ = RequestUpdateAsync(incremental: false, CancellationToken.None);
                    break;
                default:
                    // Незнакомую кодировку пропустить нельзя (не знаем длину), поэтому разрываем.
                    throw new NotSupportedException($"Сервер использовал неподдерживаемую кодировку: {encoding}");
            }

            dirty.Add((x, y, w, h));
        }

        if (dirty.Count > 0)
            FrameUpdated?.Invoke(this, new FrameUpdateEventArgs(dirty));
    }

    private async Task SkipColourMapAsync(CancellationToken ct)
    {
        // padding + first-colour (u16) + number-of-colours (u16) + RGB-триплеты.
        await _s.ReadByteAsync(ct).ConfigureAwait(false);
        ushort first = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
        ushort count = await _s.ReadUInt16Async(ct).ConfigureAwait(false);
        int bytes = count * 6;
        if (bytes > RfbProtocol.MaxMessageSize)
            throw new InvalidDataException("Слишком большая цветовая карта.");
        await _s.ReadExactAsync(new byte[bytes], ct).ConfigureAwait(false);
    }

    private async Task HandleServerCutTextAsync(CancellationToken ct)
    {
        // padding (3 байта) + length + Latin-1 текст.
        await _s.ReadByteAsync(ct).ConfigureAwait(false);
        await _s.ReadByteAsync(ct).ConfigureAwait(false);
        await _s.ReadByteAsync(ct).ConfigureAwait(false);
        int length = checked((int)await _s.ReadUInt32Async(ct).ConfigureAwait(false));
        if (length < 0 || length > RfbProtocol.MaxMessageSize)
            throw new InvalidDataException($"Некорректная длина буфера обмена: {length}");
        string text = await _s.ReadLatin1Async(length, ct).ConfigureAwait(false);
        ServerCutText?.Invoke(this, text);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _cts.Dispose();
        await _s.BaseStream.DisposeAsync().ConfigureAwait(false);
    }
}
