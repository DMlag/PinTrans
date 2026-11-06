namespace AiPhoto.Core.Imaging;

/// <summary>
/// Fast $O(W \times H)$ separable 3-pass sliding-window Gaussian blur approximation for <see cref="Bgra32Image"/>.
/// Running time is independent of blur <c>radius</c>.
/// </summary>
public static class GaussianBlurProcessor
{
    public static Bgra32Image BlurRegion(Bgra32Image source, PixelRect region, int radius)
    {
        ArgumentNullException.ThrowIfNull(source);
        region = region.Intersect(source.Bounds);
        if (region.IsEmpty) return new Bgra32Image(0, 0);

        radius = Math.Clamp(radius, 1, 64);
        var w = region.Width;
        var h = region.Height;
        var cur = source.Crop(region);
        if (w <= 1 && h <= 1) return cur;

        var tmp = new byte[cur.Pixels.Length];
        // 3 passes of separable horizontal + vertical box filter converge to a smooth Gaussian kernel.
        for (var pass = 0; pass < 3; pass++)
        {
            BlurHorizontal(cur.Pixels, tmp, w, h, cur.Stride, radius);
            BlurVertical(tmp, cur.Pixels, w, h, cur.Stride, radius);
        }

        return cur;
    }

    private static void BlurHorizontal(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        var window = 2 * r + 1;
        for (var y = 0; y < h; y++)
        {
            var rowOffset = y * stride;
            int sumB = 0, sumG = 0, sumR = 0;

            for (var k = -r; k <= r; k++)
            {
                var sx = Math.Clamp(k, 0, w - 1);
                var p = rowOffset + sx * 4;
                sumB += src[p];
                sumG += src[p + 1];
                sumR += src[p + 2];
            }

            for (var x = 0; x < w; x++)
            {
                var d = rowOffset + x * 4;
                dst[d] = (byte)((sumB + window / 2) / window);
                dst[d + 1] = (byte)((sumG + window / 2) / window);
                dst[d + 2] = (byte)((sumR + window / 2) / window);
                dst[d + 3] = 255;

                var outX = Math.Clamp(x - r, 0, w - 1);
                var inX = Math.Clamp(x + r + 1, 0, w - 1);
                var pOut = rowOffset + outX * 4;
                var pIn = rowOffset + inX * 4;

                sumB += src[pIn] - src[pOut];
                sumG += src[pIn + 1] - src[pOut + 1];
                sumR += src[pIn + 2] - src[pOut + 2];
            }
        }
    }

    private static void BlurVertical(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        var window = 2 * r + 1;
        for (var x = 0; x < w; x++)
        {
            var colOffset = x * 4;
            int sumB = 0, sumG = 0, sumR = 0;

            for (var k = -r; k <= r; k++)
            {
                var sy = Math.Clamp(k, 0, h - 1);
                var p = sy * stride + colOffset;
                sumB += src[p];
                sumG += src[p + 1];
                sumR += src[p + 2];
            }

            for (var y = 0; y < h; y++)
            {
                var d = y * stride + colOffset;
                dst[d] = (byte)((sumB + window / 2) / window);
                dst[d + 1] = (byte)((sumG + window / 2) / window);
                dst[d + 2] = (byte)((sumR + window / 2) / window);
                dst[d + 3] = 255;

                var outY = Math.Clamp(y - r, 0, h - 1);
                var inY = Math.Clamp(y + r + 1, 0, h - 1);
                var pOut = outY * stride + colOffset;
                var pIn = inY * stride + colOffset;

                sumB += src[pIn] - src[pOut];
                sumG += src[pIn + 1] - src[pOut + 1];
                sumR += src[pIn + 2] - src[pOut + 2];
            }
        }
    }
}
