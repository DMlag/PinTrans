namespace AiPhoto.Core.Imaging;

public enum ToneMapOperator
{
    /// <summary>Scale so SDR white = 1.0 and hard-clip everything brighter.</summary>
    Clip,
    /// <summary>Extended Reinhard on luminance – keeps SDR content almost untouched, compresses highlights smoothly.</summary>
    Reinhard,
    /// <summary>ACES filmic approximation (Narkowicz) – more contrast, film-like highlight roll-off.</summary>
    Aces,
}

public sealed record ToneMapOptions
{
    /// <summary>Brightness that Windows uses for SDR white on the HDR display ("SDR content brightness" slider).</summary>
    public float SdrWhiteNits { get; init; } = ToneMapper.DefaultSdrWhiteNits;

    /// <summary>Brightest value expected in the source (peak display luminance). Used by Reinhard.</summary>
    public float PeakNits { get; init; } = 1000f;

    public ToneMapOperator Operator { get; init; } = ToneMapOperator.Reinhard;
}

/// <summary>Converts scRGB HDR pixels into displayable sRGB 8-bit pixels.</summary>
public static class ToneMapper
{
    /// <summary>scRGB value 1.0 corresponds to 80 nits.</summary>
    public const float ScRgbReferenceNits = 80f;

    /// <summary>Windows default SDR white level on HDR displays.</summary>
    public const float DefaultSdrWhiteNits = 200f;

    public static Bgra32Image ToSdr(RgbaHalfImage hdr, ToneMapOptions options)
    {
        var result = new Bgra32Image(hdr.Width, hdr.Height);
        var exposure = ScRgbReferenceNits / Math.Max(1f, options.SdrWhiteNits);
        var whitePoint = Math.Max(1f, options.PeakNits / Math.Max(1f, options.SdrWhiteNits));
        var encode = BuildEncodeLut();

        Parallel.For(0, hdr.Height, y =>
        {
            var src = hdr.Pixels.AsSpan(y * hdr.Width * 4, hdr.Width * 4);
            var dst = result.GetRow(y);
            for (var x = 0; x < hdr.Width; x++)
            {
                var r = Sanitize((float)src[x * 4]) * exposure;
                var g = Sanitize((float)src[x * 4 + 1]) * exposure;
                var b = Sanitize((float)src[x * 4 + 2]) * exposure;

                MapPixel(options.Operator, whitePoint, ref r, ref g, ref b);

                dst[x * 4] = Encode(encode, b);
                dst[x * 4 + 1] = Encode(encode, g);
                dst[x * 4 + 2] = Encode(encode, r);
                dst[x * 4 + 3] = 255;
            }
        });
        return result;
    }

    /// <summary>Tone maps a single linear pixel already normalised so SDR white = 1.0.</summary>
    public static void MapPixel(ToneMapOperator op, float whitePoint, ref float r, ref float g, ref float b)
    {
        switch (op)
        {
            case ToneMapOperator.Clip:
                break;

            case ToneMapOperator.Reinhard:
            {
                // Linear below the knee (so ordinary SDR content is untouched), extended Reinhard on the
                // excess above it so that `whitePoint` maps exactly to 1.0. Applied to luminance to keep hue.
                var l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                if (l > ReinhardKnee && whitePoint > 1f)
                {
                    var mapped = ReinhardShoulder(l, whitePoint);
                    var scale = mapped / l;
                    r *= scale; g *= scale; b *= scale;
                }
                break;
            }

            case ToneMapOperator.Aces:
                r = Aces(r * 0.6f); g = Aces(g * 0.6f); b = Aces(b * 0.6f);
                break;
        }
    }

    /// <summary>Luminance (SDR white = 1.0) below which the Reinhard operator is the identity.</summary>
    public const float ReinhardKnee = 0.75f;

    /// <summary>Maps luminance <paramref name="l"/> (&gt; knee) into [knee, 1], reaching 1 at <paramref name="whitePoint"/>.</summary>
    public static float ReinhardShoulder(float l, float whitePoint)
    {
        const float k = ReinhardKnee;
        if (l <= k) return l;
        var range = 1f - k;
        var x = (l - k) / range;
        var wx = Math.Max(1e-3f, (whitePoint - k) / range);
        var mapped = x * (1f + x / (wx * wx)) / (1f + x);
        return k + range * Math.Min(mapped, 1f);
    }

    private static float Aces(float x)
    {
        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return Math.Clamp(x * (a * x + b) / (x * (c * x + d) + e), 0f, 1f);
    }

    private static float Sanitize(float v) => float.IsFinite(v) && v > 0f ? v : 0f;

    private const int LutSize = 4096;

    private static byte[] BuildEncodeLut()
    {
        var lut = new byte[LutSize + 1];
        for (var i = 0; i <= LutSize; i++)
        {
            lut[i] = (byte)Math.Clamp((int)MathF.Round(ColorMath.LinearToSrgb(i / (float)LutSize) * 255f), 0, 255);
        }
        return lut;
    }

    private static byte Encode(byte[] lut, float linear)
    {
        if (linear <= 0f) return 0;
        if (linear >= 1f) return 255;
        return lut[(int)(linear * LutSize + 0.5f)];
    }
}

public static class ColorMath
{
    public static float SrgbToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c)
        => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
}
