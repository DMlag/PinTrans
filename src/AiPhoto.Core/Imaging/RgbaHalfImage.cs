namespace AiPhoto.Core.Imaging;

/// <summary>
/// 64-bit RGBA image with IEEE half-float channels in scRGB (linear, BT.709 primaries, 1.0 = 80 nits).
/// This is the native format produced by DXGI desktop duplication on HDR displays
/// (DXGI_FORMAT_R16G16B16A16_FLOAT) and consumed by the JPEG XR encoder (64bppRGBAHalf).
/// </summary>
public sealed class RgbaHalfImage
{
    public const int ChannelsPerPixel = 4;
    public const int BytesPerPixel = 8;

    public RgbaHalfImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new Half[checked(width * height * ChannelsPerPixel)];
    }

    public RgbaHalfImage(int width, int height, Half[] pixels)
    {
        if (pixels.Length < width * height * ChannelsPerPixel)
            throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Tightly packed R,G,B,A half values.</summary>
    public Half[] Pixels { get; }

    public int Stride => Width * BytesPerPixel;

    public PixelRect Bounds => new(0, 0, Width, Height);

    public Span<Half> GetRow(int y) => Pixels.AsSpan(y * Width * ChannelsPerPixel, Width * ChannelsPerPixel);

    public RgbaHalfImage Crop(PixelRect rect)
    {
        rect = rect.Intersect(Bounds);
        var result = new RgbaHalfImage(rect.Width, rect.Height);
        for (var y = 0; y < rect.Height; y++)
        {
            Array.Copy(Pixels, ((rect.Y + y) * Width + rect.X) * ChannelsPerPixel,
                result.Pixels, y * rect.Width * ChannelsPerPixel, rect.Width * ChannelsPerPixel);
        }
        return result;
    }

    public void Blit(RgbaHalfImage source, int destX, int destY)
    {
        var dest = new PixelRect(destX, destY, source.Width, source.Height).Intersect(Bounds);
        if (dest.IsEmpty) return;
        var srcX = dest.X - destX;
        var srcY = dest.Y - destY;
        for (var y = 0; y < dest.Height; y++)
        {
            Array.Copy(source.Pixels, ((srcY + y) * source.Width + srcX) * ChannelsPerPixel,
                Pixels, ((dest.Y + y) * Width + dest.X) * ChannelsPerPixel, dest.Width * ChannelsPerPixel);
        }
    }

    /// <summary>
    /// Converts an SDR BGRA image into scRGB so SDR and HDR monitors can be combined in one HDR canvas.
    /// SDR white is mapped to <paramref name="sdrWhiteNits"/>.
    /// </summary>
    public static RgbaHalfImage FromSdr(Bgra32Image sdr, float sdrWhiteNits = ToneMapper.DefaultSdrWhiteNits)
    {
        var result = new RgbaHalfImage(sdr.Width, sdr.Height);
        var scale = sdrWhiteNits / ToneMapper.ScRgbReferenceNits;
        Span<float> lut = stackalloc float[256];
        for (var i = 0; i < 256; i++) lut[i] = ColorMath.SrgbToLinear(i / 255f) * scale;

        for (var y = 0; y < sdr.Height; y++)
        {
            var src = sdr.GetRowReadOnly(y);
            var dst = result.GetRow(y);
            for (var x = 0; x < sdr.Width; x++)
            {
                dst[x * 4] = (Half)lut[src[x * 4 + 2]];
                dst[x * 4 + 1] = (Half)lut[src[x * 4 + 1]];
                dst[x * 4 + 2] = (Half)lut[src[x * 4]];
                dst[x * 4 + 3] = (Half)1f;
            }
        }
        return result;
    }
}
