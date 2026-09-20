namespace RemoteControl.Core.Rfb;

/// <summary>
/// Декодеры пиксельных кодировок RFB. На данный момент: Raw, CopyRect, Hextile.
/// TODO(этап 5): добавить Tight/ZRLE для сжатия на медленных каналах.
/// </summary>
internal static class Decoders
{
    public static async Task DecodeRawAsync(
        RfbStream s, Framebuffer fb, ushort x, ushort y, ushort w, ushort h, CancellationToken ct)
    {
        int byteCount = checked(w * h * 4);
        byte[] pixels = new byte[byteCount];
        await s.ReadExactAsync(pixels, ct).ConfigureAwait(false);
        fb.FillRect(x, y, w, h, pixels);
    }

    public static async Task DecodeCopyRectAsync(
        RfbStream s, Framebuffer fb, ushort x, ushort y, ushort w, ushort h, CancellationToken ct)
    {
        ushort srcX = await s.ReadUInt16Async(ct).ConfigureAwait(false);
        ushort srcY = await s.ReadUInt16Async(ct).ConfigureAwait(false);
        fb.CopyRect(srcX, srcY, x, y, w, h);
    }

    public static async Task DecodeHextileAsync(
        RfbStream s, Framebuffer fb, ushort x, ushort y, ushort w, ushort h, CancellationToken ct)
    {
        int tileX = 0, tileY = 0;
        uint background = 0, foreground = 0;

        while (tileY < h)
        {
            int subencoding = await s.ReadByteAsync(ct).ConfigureAwait(false);
            int tileW = Math.Min(16, w - tileX);
            int tileH = Math.Min(16, h - tileY);

            if ((subencoding & 0x01) != 0)
            {
                // Raw-тайл
                await DecodeRawAsync(s, fb, (ushort)(x + tileX), (ushort)(y + tileY),
                                     (ushort)tileW, (ushort)tileH, ct).ConfigureAwait(false);
            }
            else
            {
                if ((subencoding & 0x02) != 0)
                    background = await ReadPixelValueAsync(s, ct).ConfigureAwait(false);
                if ((subencoding & 0x04) != 0)
                    foreground = await ReadPixelValueAsync(s, ct).ConfigureAwait(false);

                fb.FillSolid(x + tileX, y + tileY, tileW, tileH, background);

                if ((subencoding & 0x08) != 0)
                {
                    bool colored = (subencoding & 0x10) != 0;
                    int subrectCount = await s.ReadByteAsync(ct).ConfigureAwait(false);
                    for (int i = 0; i < subrectCount; i++)
                    {
                        // Порядок (LibVNCServer 0.9.14): для каждого субпрямоугольника
                        // сначала его цвет (если coloured), затем координаты XY и размер WH.
                        uint subrectColor = foreground;
                        if (colored)
                            subrectColor = await ReadPixelValueAsync(s, ct).ConfigureAwait(false);

                        byte b1 = await s.ReadByteAsync(ct).ConfigureAwait(false);
                        byte b2 = await s.ReadByteAsync(ct).ConfigureAwait(false);
                        int sx = b1 >> 4;
                        int sy = b1 & 0x0F;
                        int sw = (b2 >> 4) + 1;
                        int sh = (b2 & 0x0F) + 1;

                        if (sx + sw > tileW || sy + sh > tileH)
                            throw new InvalidDataException("Hextile: субпрямоугольник вне тайла.");

                        fb.FillSolid(x + tileX + sx, y + tileY + sy, sw, sh, subrectColor);
                    }
                }
            }

            tileX += 16;
            if (tileX >= w)
            {
                tileX = 0;
                tileY += 16;
            }
        }
    }

    /// <summary>
    /// Читает значение пикселя в согласованном формате (у нас 32bpp BGRA).
    /// ВНИМАНИЕ: LibVNCServer пишет значение пикселя как 4 байта в порядке
    /// байт пиксельного формата (B,G,R,X), а НЕ как big-endian uint32.
    /// </summary>
    private static async Task<uint> ReadPixelValueAsync(RfbStream s, CancellationToken ct)
    {
        byte[] b = new byte[4];
        await s.ReadExactAsync(b, ct).ConfigureAwait(false);
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }
}
