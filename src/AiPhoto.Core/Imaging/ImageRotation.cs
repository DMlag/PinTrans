namespace AiPhoto.Core.Imaging;

/// <summary>Lossless 90° rotations (used for rotated monitors returned by desktop duplication).</summary>
public static class ImageRotation
{
    /// <summary>Rotates clockwise by <paramref name="quarterTurns"/> × 90°.</summary>
    public static Bgra32Image RotateClockwise(Bgra32Image src, int quarterTurns)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0) return src;

        var (w, h) = quarterTurns == 2 ? (src.Width, src.Height) : (src.Height, src.Width);
        var dst = new Bgra32Image(w, h);
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(src.Pixels.AsSpan());
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.Pixels.AsSpan());
        var sStride = src.Stride / 4;
        Rotate(s, sStride, src.Width, src.Height, d, w, h, quarterTurns);
        return dst;
    }

    public static RgbaHalfImage RotateClockwise(RgbaHalfImage src, int quarterTurns)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0) return src;

        var (w, h) = quarterTurns == 2 ? (src.Width, src.Height) : (src.Height, src.Width);
        var dst = new RgbaHalfImage(w, h);
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ulong>(src.Pixels.AsSpan());
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ulong>(dst.Pixels.AsSpan());
        Rotate(s, src.Width, src.Width, src.Height, d, w, h, quarterTurns);
        return dst;
    }

    private static void Rotate<T>(ReadOnlySpan<T> s, int sStride, int sw, int sh, Span<T> d, int dw, int dh, int quarterTurns)
    {
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                int sx, sy;
                switch (quarterTurns)
                {
                    case 1: sx = y; sy = sh - 1 - x; break;            // 90° CW
                    case 2: sx = sw - 1 - x; sy = sh - 1 - y; break;   // 180°
                    default: sx = sw - 1 - y; sy = x; break;           // 270° CW (= 90° CCW)
                }
                d[y * dw + x] = s[sy * sStride + sx];
            }
        }
    }
}
