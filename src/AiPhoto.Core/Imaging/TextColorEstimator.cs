namespace AiPhoto.Core.Imaging;

/// <summary>Background and foreground colours estimated for a text block.</summary>
public readonly record struct TextColors(Bgra32 Background, Bgra32 Foreground);

/// <summary>
/// Estimates the background colour around a text block (so the original text can be painted over)
/// and a readable foreground colour for the translated text.
/// </summary>
public static class TextColorEstimator
{
    private static readonly Bgra32 Black = Bgra32.FromRgb(20, 20, 20);
    private static readonly Bgra32 White = Bgra32.FromRgb(250, 250, 250);

    public static TextColors Estimate(Bgra32Image image, PixelRect block)
    {
        block = block.Intersect(image.Bounds);
        if (block.IsEmpty) return new TextColors(White, Black);

        var background = EstimateBackground(image, block);
        var foreground = EstimateForeground(image, block, background);
        return new TextColors(background, foreground);
    }

    /// <summary>
    /// Median colour of a 2px ring just outside (and on) the block border. The median is robust against
    /// stray glyph pixels touching the border.
    /// </summary>
    public static Bgra32 EstimateBackground(Bgra32Image image, PixelRect block)
    {
        var ring = block.Inflate(2).Intersect(image.Bounds);
        var samples = new List<Bgra32>(2 * (ring.Width + ring.Height) * 3);
        var step = Math.Max(1, (ring.Width + ring.Height) / 400);

        for (var d = 0; d < 3; d++)
        {
            var top = ring.Y + d;
            var bottom = ring.Bottom - 1 - d;
            var left = ring.X + d;
            var right = ring.Right - 1 - d;
            if (top > bottom || left > right) break;
            for (var x = left; x <= right; x += step)
            {
                samples.Add(image.GetPixel(x, top));
                samples.Add(image.GetPixel(x, bottom));
            }
            for (var y = top; y <= bottom; y += step)
            {
                samples.Add(image.GetPixel(left, y));
                samples.Add(image.GetPixel(right, y));
            }
        }

        if (samples.Count == 0) return image.GetPixel(block.X, block.Y);
        return Median(samples);
    }

    /// <summary>
    /// Picks the colour of the pixels inside the block that differ most from the background (likely glyphs).
    /// Falls back to black/white when that colour does not contrast enough.
    /// </summary>
    public static Bgra32 EstimateForeground(Bgra32Image image, PixelRect block, Bgra32 background)
    {
        var step = Math.Max(1, (int)Math.Sqrt((double)block.Width * block.Height / 20000));
        var candidates = new List<(int Dist, Bgra32 Color)>();
        for (var y = block.Y; y < block.Bottom; y += step)
        for (var x = block.X; x < block.Right; x += step)
        {
            var c = image.GetPixel(x, y);
            var dist = c.DistanceSquared(background);
            if (dist > 60 * 60) candidates.Add((dist, c));
        }

        var fallback = background.Luma > 0.55 ? Black : White;
        if (candidates.Count < 4) return fallback;

        // Average the most distinct quarter of glyph pixels (anti-aliased edges are discarded).
        candidates.Sort((a, b) => b.Dist.CompareTo(a.Dist));
        var take = Math.Max(1, candidates.Count / 4);
        long r = 0, g = 0, b = 0;
        for (var i = 0; i < take; i++)
        {
            r += candidates[i].Color.R; g += candidates[i].Color.G; b += candidates[i].Color.B;
        }
        var fg = Bgra32.FromRgb((byte)(r / take), (byte)(g / take), (byte)(b / take));

        return ContrastRatio(fg, background) >= 3.0 ? fg : fallback;
    }

    /// <summary>WCAG contrast ratio between two colours (1..21).</summary>
    public static double ContrastRatio(Bgra32 a, Bgra32 b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static double RelativeLuminance(Bgra32 c)
        => 0.2126 * ColorMath.SrgbToLinear(c.R / 255f)
         + 0.7152 * ColorMath.SrgbToLinear(c.G / 255f)
         + 0.0722 * ColorMath.SrgbToLinear(c.B / 255f);

    private static Bgra32 Median(List<Bgra32> samples)
    {
        static byte MedianOf(List<Bgra32> s, Func<Bgra32, byte> sel)
        {
            Span<int> hist = stackalloc int[256];
            foreach (var c in s) hist[sel(c)]++;
            var half = s.Count / 2;
            var acc = 0;
            for (var i = 0; i < 256; i++)
            {
                acc += hist[i];
                if (acc > half) return (byte)i;
            }
            return 255;
        }

        return Bgra32.FromRgb(MedianOf(samples, c => c.R), MedianOf(samples, c => c.G), MedianOf(samples, c => c.B));
    }
}
