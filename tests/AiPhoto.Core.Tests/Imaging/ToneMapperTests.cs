using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Tests.Imaging;

public class ToneMapperTests
{
    private static RgbaHalfImage Solid(float r, float g, float b, int w = 3, int h = 2)
    {
        var img = new RgbaHalfImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            img.Pixels[i * 4] = (Half)r;
            img.Pixels[i * 4 + 1] = (Half)g;
            img.Pixels[i * 4 + 2] = (Half)b;
            img.Pixels[i * 4 + 3] = (Half)1f;
        }
        return img;
    }

    private static Bgra32 MapSolid(float scRgb, ToneMapOptions options)
        => ToneMapper.ToSdr(Solid(scRgb, scRgb, scRgb), options).GetPixel(1, 1);

    [Theory]
    [InlineData(ToneMapOperator.Clip, 200f)]
    [InlineData(ToneMapOperator.Clip, 80f)]
    [InlineData(ToneMapOperator.Clip, 480f)]
    public void SdrWhite_MapsTo255_WithClip(ToneMapOperator op, float sdrWhite)
    {
        var options = new ToneMapOptions { Operator = op, SdrWhiteNits = sdrWhite };
        var px = MapSolid(sdrWhite / ToneMapper.ScRgbReferenceNits, options);
        Assert.Equal(Bgra32.FromRgb(255, 255, 255), px);
    }

    [Fact]
    public void SdrWhite_MapsTo255_WithReinhard_WhenPeakEqualsSdrWhite()
    {
        // whitePoint == 1 => no highlight compression is needed, Reinhard must be the identity.
        var options = new ToneMapOptions { Operator = ToneMapOperator.Reinhard, SdrWhiteNits = 200, PeakNits = 200 };
        Assert.Equal(Bgra32.FromRgb(255, 255, 255), MapSolid(2.5f, options));
    }

    [Fact]
    public void Reinhard_WithHighPeak_CompressesSdrWhiteSlightly_AndPeakReaches255()
    {
        // Documents the current trade-off of the knee-based shoulder: with a 1000-nit peak, SDR white is rolled
        // off (to make room for highlights) while the peak itself maps to full white.
        var options = new ToneMapOptions { Operator = ToneMapOperator.Reinhard, SdrWhiteNits = 200, PeakNits = 1000 };
        var white = MapSolid(2.5f, options);
        var peak = MapSolid(1000f / 80f, options);
        Assert.InRange(white.R, 235, 255);
        Assert.Equal(255, peak.R);
        Assert.True(peak.R > white.R);
    }

    [Theory]
    [InlineData(ToneMapOperator.Clip)]
    [InlineData(ToneMapOperator.Reinhard)]
    [InlineData(ToneMapOperator.Aces)]
    public void Black_MapsTo0_AndNegativeOrNonFiniteAreSanitized(ToneMapOperator op)
    {
        var options = new ToneMapOptions { Operator = op };
        Assert.Equal(Bgra32.FromRgb(0, 0, 0), MapSolid(0f, options));
        Assert.Equal(Bgra32.FromRgb(0, 0, 0), MapSolid(-1f, options));
        Assert.Equal(Bgra32.FromRgb(0, 0, 0), MapSolid(float.NaN, options));

        var inf = ToneMapper.ToSdr(Solid(float.PositiveInfinity, 0, 0), options).GetPixel(0, 0);
        Assert.Equal(0, inf.R); // +Inf is treated as invalid data, not as "very bright"
        Assert.Equal(255, inf.A);
    }

    [Fact]
    public void Clip_HardClipsHighlights()
    {
        var options = new ToneMapOptions { Operator = ToneMapOperator.Clip, SdrWhiteNits = 200 };
        Assert.Equal(Bgra32.FromRgb(255, 255, 255), MapSolid(10f, options));
        float r = 3f, g = 0.5f, b = -1f;
        ToneMapper.MapPixel(ToneMapOperator.Clip, 5f, ref r, ref g, ref b);
        Assert.Equal((3f, 0.5f, -1f), (r, g, b)); // Clip is the identity; encoding clips
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.1f)]
    [InlineData(0.5f)]
    [InlineData(0.749f)]
    public void Reinhard_IsIdentityBelowKnee(float v)
    {
        float r = v, g = v * 0.5f, b = v * 0.9f;
        var l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        Assert.True(l <= ToneMapper.ReinhardKnee);
        ToneMapper.MapPixel(ToneMapOperator.Reinhard, 8f, ref r, ref g, ref b);
        Assert.Equal(v, r);
        Assert.Equal(v * 0.5f, g);
        Assert.Equal(v * 0.9f, b);
        Assert.Equal(v, ToneMapper.ReinhardShoulder(v, 8f));
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(5f)]
    [InlineData(12.5f)]
    [InlineData(50f)]
    public void ReinhardShoulder_IsMonotonic_AndReachesOneAtWhitePoint(float whitePoint)
    {
        var prev = 0f;
        for (var l = 0f; l <= whitePoint * 1.5f; l += whitePoint / 2000f)
        {
            var m = ToneMapper.ReinhardShoulder(l, whitePoint);
            Assert.True(m >= prev - 1e-6f, $"not monotonic at {l}: {m} < {prev}");
            Assert.InRange(m, 0f, 1f);
            prev = m;
        }
        Assert.Equal(1f, ToneMapper.ReinhardShoulder(whitePoint, whitePoint), 4);
        Assert.Equal(1f, ToneMapper.ReinhardShoulder(whitePoint * 10, whitePoint), 6);
        // Continuous at the knee.
        Assert.Equal(ToneMapper.ReinhardKnee, ToneMapper.ReinhardShoulder(ToneMapper.ReinhardKnee + 1e-5f, whitePoint), 4);
    }

    [Fact]
    public void Reinhard_PreservesHue_WhenCompressing()
    {
        float r = 4f, g = 2f, b = 1f;
        ToneMapper.MapPixel(ToneMapOperator.Reinhard, 5f, ref r, ref g, ref b);
        Assert.Equal(2f, r / g, 4);
        Assert.Equal(2f, g / b, 4);
        var l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        Assert.True(l <= 1f + 1e-5f);
    }

    [Fact]
    public void Aces_IsMonotonicAndBounded()
    {
        var prev = -1f;
        for (var v = 0f; v < 20f; v += 0.05f)
        {
            float r = v, g = v, b = v;
            ToneMapper.MapPixel(ToneMapOperator.Aces, 5f, ref r, ref g, ref b);
            Assert.InRange(r, 0f, 1f);
            Assert.True(r >= prev);
            prev = r;
        }
    }

    [Fact]
    public void FromSdr_ThenToSdrWithClip_RoundTrips()
    {
        var sdr = new Bgra32Image(256, 3);
        var rng = new Random(1234);
        for (var x = 0; x < 256; x++)
        {
            sdr.SetPixel(x, 0, Bgra32.FromRgb((byte)x, (byte)x, (byte)x));
            sdr.SetPixel(x, 1, Bgra32.FromRgb((byte)x, (byte)(255 - x), (byte)(x * 7)));
            sdr.SetPixel(x, 2, Bgra32.FromRgb((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
        }

        foreach (var white in new[] { 80f, 200f, 300f })
        {
            var hdr = RgbaHalfImage.FromSdr(sdr, white);
            var back = ToneMapper.ToSdr(hdr, new ToneMapOptions { Operator = ToneMapOperator.Clip, SdrWhiteNits = white });
            for (var y = 0; y < sdr.Height; y++)
            for (var x = 0; x < sdr.Width; x++)
            {
                var a = sdr.GetPixel(x, y);
                var b = back.GetPixel(x, y);
                Assert.True(Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2,
                    $"white={white} ({x},{y}): {a} -> {b}");
                Assert.Equal(255, b.A);
            }
        }
    }

    [Fact]
    public void FromSdr_MapsWhiteToSdrWhiteNits()
    {
        var sdr = new Bgra32Image(1, 1);
        sdr.SetPixel(0, 0, Bgra32.FromRgb(255, 0, 255));
        var hdr = RgbaHalfImage.FromSdr(sdr, 240f);
        Assert.Equal(3f, (float)hdr.Pixels[0], 2);
        Assert.Equal(0f, (float)hdr.Pixels[1]);
        Assert.Equal(3f, (float)hdr.Pixels[2], 2);
        Assert.Equal(1f, (float)hdr.Pixels[3]);
    }

    [Fact]
    public void RgbaHalfImage_CropAndBlit()
    {
        var img = new RgbaHalfImage(4, 4);
        for (var i = 0; i < img.Pixels.Length; i++) img.Pixels[i] = (Half)i;
        var crop = img.Crop(new PixelRect(1, 2, 2, 5));
        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal((Half)((2 * 4 + 1) * 4), crop.Pixels[0]);

        var dst = new RgbaHalfImage(3, 3);
        dst.Blit(crop, 2, -1);
        Assert.Equal(crop.Pixels[1 * 8], dst.Pixels[2 * 4]);       // src (0,1) -> dst (2,0)
        Assert.Equal((Half)0f, dst.Pixels[1 * 12 + 2 * 4]);       // dst (2,1) untouched
    }

    [Fact]
    public void ColorMath_RoundTrips()
    {
        for (var i = 0; i <= 255; i++)
        {
            var c = i / 255f;
            Assert.Equal(c, ColorMath.LinearToSrgb(ColorMath.SrgbToLinear(c)), 4);
        }
    }
}
