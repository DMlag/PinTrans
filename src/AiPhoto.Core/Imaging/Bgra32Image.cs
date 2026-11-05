namespace AiPhoto.Core.Imaging;

/// <summary>
/// A simple, platform-independent 32-bit BGRA (8 bits per channel) image buffer.
/// Rows are tightly packed unless <see cref="Stride"/> says otherwise.
/// </summary>
public sealed class Bgra32Image
{
    public const int BytesPerPixel = 4;

    public Bgra32Image(int width, int height)
        : this(width, height, width * BytesPerPixel, new byte[checked(width * BytesPerPixel * height)])
    {
    }

    public Bgra32Image(int width, int height, int stride, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (stride < width * BytesPerPixel)
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride is smaller than a row of pixels.");
        if (pixels.Length < (long)stride * height)
            throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));

        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    public PixelRect Bounds => new(0, 0, Width, Height);

    public Span<byte> GetRow(int y) => Pixels.AsSpan(y * Stride, Width * BytesPerPixel);

    public ReadOnlySpan<byte> GetRowReadOnly(int y) => Pixels.AsSpan(y * Stride, Width * BytesPerPixel);

    public Bgra32 GetPixel(int x, int y)
    {
        var i = y * Stride + x * BytesPerPixel;
        return new Bgra32(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public void SetPixel(int x, int y, Bgra32 c)
    {
        var i = y * Stride + x * BytesPerPixel;
        Pixels[i] = c.B;
        Pixels[i + 1] = c.G;
        Pixels[i + 2] = c.R;
        Pixels[i + 3] = c.A;
    }

    /// <summary>Returns a tightly packed copy of the given region (clamped to the image bounds).</summary>
    public Bgra32Image Crop(PixelRect rect)
    {
        rect = rect.Intersect(Bounds);
        var result = new Bgra32Image(rect.Width, rect.Height);
        var rowBytes = rect.Width * BytesPerPixel;
        for (var y = 0; y < rect.Height; y++)
        {
            Buffer.BlockCopy(Pixels, (rect.Y + y) * Stride + rect.X * BytesPerPixel, result.Pixels, y * result.Stride, rowBytes);
        }
        return result;
    }

    /// <summary>Copies <paramref name="source"/> into this image at the given position (clipped).</summary>
    public void Blit(Bgra32Image source, int destX, int destY)
    {
        var dest = new PixelRect(destX, destY, source.Width, source.Height).Intersect(Bounds);
        if (dest.IsEmpty) return;
        var srcX = dest.X - destX;
        var srcY = dest.Y - destY;
        var rowBytes = dest.Width * BytesPerPixel;
        for (var y = 0; y < dest.Height; y++)
        {
            Buffer.BlockCopy(source.Pixels, (srcY + y) * source.Stride + srcX * BytesPerPixel,
                Pixels, (dest.Y + y) * Stride + dest.X * BytesPerPixel, rowBytes);
        }
    }

    public Bgra32Image Clone()
    {
        var copy = new byte[Pixels.Length];
        Buffer.BlockCopy(Pixels, 0, copy, 0, Pixels.Length);
        return new Bgra32Image(Width, Height, Stride, copy);
    }

    /// <summary>Nearest/box down-scaling so that the longest edge is at most <paramref name="maxEdge"/>.</summary>
    public Bgra32Image DownscaleToFit(int maxEdge)
    {
        var longest = Math.Max(Width, Height);
        if (longest <= maxEdge || longest == 0) return this;

        var scale = (double)maxEdge / longest;
        var nw = Math.Max(1, (int)Math.Round(Width * scale));
        var nh = Math.Max(1, (int)Math.Round(Height * scale));
        var result = new Bgra32Image(nw, nh);

        // Box filter: average all source pixels that fall into each destination pixel.
        for (var dy = 0; dy < nh; dy++)
        {
            var sy0 = (int)((long)dy * Height / nh);
            var sy1 = Math.Max(sy0 + 1, (int)((long)(dy + 1) * Height / nh));
            for (var dx = 0; dx < nw; dx++)
            {
                var sx0 = (int)((long)dx * Width / nw);
                var sx1 = Math.Max(sx0 + 1, (int)((long)(dx + 1) * Width / nw));
                long b = 0, g = 0, r = 0, a = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var row = sy * Stride;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var i = row + sx * BytesPerPixel;
                        b += Pixels[i]; g += Pixels[i + 1]; r += Pixels[i + 2]; a += Pixels[i + 3];
                        n++;
                    }
                }
                var o = dy * result.Stride + dx * BytesPerPixel;
                result.Pixels[o] = (byte)(b / n);
                result.Pixels[o + 1] = (byte)(g / n);
                result.Pixels[o + 2] = (byte)(r / n);
                result.Pixels[o + 3] = (byte)(a / n);
            }
        }
        return result;
    }
}

public readonly record struct Bgra32(byte B, byte G, byte R, byte A = 255)
{
    /// <summary>Relative luminance (0..1) using sRGB coefficients on gamma-encoded values (good enough for contrast decisions).</summary>
    public double Luma => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;

    public static Bgra32 FromRgb(byte r, byte g, byte b) => new(b, g, r);

    public int DistanceSquared(Bgra32 other)
    {
        var db = B - other.B; var dg = G - other.G; var dr = R - other.R;
        return db * db + dg * dg + dr * dr;
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
