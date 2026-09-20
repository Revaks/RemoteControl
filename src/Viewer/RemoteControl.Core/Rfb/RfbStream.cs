using System.Buffers.Binary;
using System.Net;

namespace RemoteControl.Core.Rfb;

/// <summary>
/// Асинхронные операции чтения/записи поверх Stream в сетевом порядке байт (big-endian),
/// как требует протокол RFB.
/// </summary>
internal sealed class RfbStream
{
    private readonly Stream _stream;
    private readonly byte[] _ioBuffer = new byte[16];
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private int _readPos;
    private int _readLen;

    // Запись сериализуется: SslStream не допускает параллельных Write
    // («This method may not be called when another write operation is pending»),
    // а события мыши/клавиатуры приходят пачками из UI-потока.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // Отдельный буфер для записи, чтобы не конфликтовать с _ioBuffer чтения.
    private readonly byte[] _writeBuffer = new byte[16];

    public RfbStream(Stream stream) => _stream = stream;

    public Stream BaseStream => _stream;

    public async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        await ReadExactAsync(_ioBuffer.AsMemory(0, 1), ct).ConfigureAwait(false);
        return _ioBuffer[0];
    }

    public async Task<ushort> ReadUInt16Async(CancellationToken ct)
    {
        await ReadExactAsync(_ioBuffer.AsMemory(0, 2), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(_ioBuffer);
    }

    public async Task<uint> ReadUInt32Async(CancellationToken ct)
    {
        await ReadExactAsync(_ioBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer);
    }

    public async Task<int> ReadInt32Async(CancellationToken ct)
    {
        await ReadExactAsync(_ioBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32BigEndian(_ioBuffer);
    }

    public async Task ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            // Читаем из внутреннего буфера (убирает тысячи мелких ReadAsync при
            // разборе Hextile — это сильно ускоряет декодирование кадров).
            if (_readPos < _readLen)
            {
                int n = Math.Min(buffer.Length - total, _readLen - _readPos);
                _readBuffer.AsSpan(_readPos, n).CopyTo(buffer.Span.Slice(total));
                _readPos += n;
                total += n;
                continue;
            }

            _readPos = 0;
            _readLen = await _stream.ReadAsync(_readBuffer, ct).ConfigureAwait(false);
            if (_readLen <= 0)
                throw new EndOfStreamException("Соединение RFB закрыто сервером.");
        }
    }

    public Task WriteByteAsync(byte value, CancellationToken ct)
    {
        _writeBuffer[0] = value;
        return WriteExactAsync(_writeBuffer.AsMemory(0, 1), ct);
    }

    public Task WriteUInt16Async(ushort value, CancellationToken ct)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_writeBuffer, value);
        return WriteExactAsync(_writeBuffer.AsMemory(0, 2), ct);
    }

    public Task WriteUInt32Async(uint value, CancellationToken ct)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_writeBuffer, value);
        return WriteExactAsync(_writeBuffer.AsMemory(0, 4), ct);
    }

    public Task WriteInt32Async(int value, CancellationToken ct)
    {
        BinaryPrimitives.WriteInt32BigEndian(_writeBuffer, value);
        return WriteExactAsync(_writeBuffer.AsMemory(0, 4), ct);
    }

    /// <summary>Запись в поток с сериализацией (см. комментарий к _writeLock).</summary>
    public async Task WriteExactAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Читает строку Latin-1 заданной длины (используется в ServerInit / ServerCutText).</summary>
    public async Task<string> ReadLatin1Async(int length, CancellationToken ct)
    {
        if (length < 0 || length > RfbProtocol.MaxMessageSize)
            throw new InvalidDataException($"Некорректная длина строки: {length}");

        byte[] data = new byte[length];
        await ReadExactAsync(data, ct).ConfigureAwait(false);
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = (char)data[i];
        return new string(chars);
    }
}
