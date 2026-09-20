namespace RemoteControl.Core.Rfb;

/// <summary>
/// Фреймбуфер в формате BGRA32 (b=0, g=8, r=16 — совместимо с WPF WriteableBitmap Pbgra32
/// и с форматом, который мы запрашиваем у сервера).
/// </summary>
public sealed class Framebuffer
{
    private byte[] _data;

    public Framebuffer(int width, int height)
    {
        Width = width;
        Height = height;
        _data = new byte[checked(width * height * 4)];
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => checked(Width * 4);

    /// <summary>Непрерывный массив пикселей BGRA32, построчно.</summary>
    public byte[] Data => _data;

    public event Action<int, int, int, int>? RegionChanged;

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height)
            return;

        _data = new byte[checked(width * height * 4)];
        Width = width;
        Height = height;
        RegionChanged?.Invoke(0, 0, width, height);
    }

    /// <summary>Заливает прямоугольник пикселями из <paramref name="pixels"/> (row-major, BGRA32).</summary>
    public void FillRect(int x, int y, int w, int h, ReadOnlySpan<byte> pixels)
    {
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > Width || y + h > Height)
            return;

        int srcStride = w * 4;
        var dst = _data.AsSpan();
        for (int row = 0; row < h; row++)
        {
            pixels.Slice(row * srcStride, srcStride)
                  .CopyTo(dst.Slice((y + row) * Stride + x * 4, srcStride));
        }

        RegionChanged?.Invoke(x, y, w, h);
    }

    /// <summary>Копирует прямоугольник внутри фреймбуфера (кодировка CopyRect).</summary>
    public void CopyRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        if (srcX < 0 || srcY < 0 || dstX < 0 || dstY < 0 ||
            srcX + w > Width || srcY + h > Height ||
            dstX + w > Width || dstY + h > Height)
            return;

        int rowBytes = w * 4;
        var span = _data.AsSpan();

        if (dstY <= srcY)
        {
            for (int row = 0; row < h; row++)
                span.Slice((srcY + row) * Stride + srcX * 4, rowBytes)
                    .CopyTo(span.Slice((dstY + row) * Stride + dstX * 4, rowBytes));
        }
        else
        {
            for (int row = h - 1; row >= 0; row--)
                span.Slice((srcY + row) * Stride + srcX * 4, rowBytes)
                    .CopyTo(span.Slice((dstY + row) * Stride + dstX * 4, rowBytes));
        }

        RegionChanged?.Invoke(dstX, dstY, w, h);
    }

    /// <summary>Заливает прямоугольник одним цветом (BGRA32).</summary>
    public void FillSolid(int x, int y, int w, int h, uint colorBgra)
    {
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > Width || y + h > Height)
            return;

        var span = _data.AsSpan();
        Span<byte> row = stackalloc byte[4];
        row[0] = (byte)(colorBgra & 0xFF);
        row[1] = (byte)((colorBgra >> 8) & 0xFF);
        row[2] = (byte)((colorBgra >> 16) & 0xFF);
        row[3] = (byte)((colorBgra >> 24) & 0xFF);

        for (int yy = 0; yy < h; yy++)
        {
            int offset = (y + yy) * Stride + x * 4;
            for (int xx = 0; xx < w; xx++)
            {
                row.CopyTo(span.Slice(offset + xx * 4, 4));
            }
        }

        RegionChanged?.Invoke(x, y, w, h);
    }
}
