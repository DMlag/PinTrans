using AiPhoto.Core.Imaging;
using AiPhoto.Core.Stitching;

namespace AiPhoto.Core.Tests.Stitching;

public class ScrollStitcherTests
{
    private const int Width = 320;
    private const int ScrollbarWidth = 14;          // < StitchOptions.IgnoreRightColumns (24)
    private const int CompareWidth = Width - 24;    // columns the stitcher actually compares

    private static uint Hash(int a, int b, int seed = 0)
    {
        unchecked
        {
            var h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)seed * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du;
            h ^= h >> 12; h *= 0x297A2D39u;
            h ^= h >> 15;
            return h;
        }
    }

    /// <summary>
    /// A tall synthetic "page": mostly gradient + noise rows (every row distinct), with a few uniform blank
    /// bands, and text-like bands (short noisy lines separated by blank rows).
    /// </summary>
    private static Bgra32Image MakePage(int height, int seed = 1)
    {
        var page = new Bgra32Image(Width, height);
        var white = Bgra32.FromRgb(255, 255, 255);
        for (var y = 0; y < height; y++)
        {
            var band = y % 700;
            var blank = band is >= 300 and < 345 || band is >= 600 and < 606;
            var textBand = band is >= 400 and < 560;
            var textBlankRow = textBand && (band - 400) % 20 >= 13;
            for (var x = 0; x < Width; x++)
            {
                if (blank || textBlankRow)
                {
                    page.SetPixel(x, y, white);
                    continue;
                }
                var n = Hash(x, y, seed);
                if (textBand)
                {
                    // Sparse dark "glyph" pixels on white.
                    page.SetPixel(x, y, (n & 7) == 0 ? Bgra32.FromRgb(20, 20, 20) : white);
                }
                else
                {
                    page.SetPixel(x, y, Bgra32.FromRgb((byte)(x * 255 / Width), (byte)(y % 256), (byte)(n & 0x3F)));
                }
            }
        }
        return page;
    }

    private static Bgra32Image MakeFixedBand(int height, int seed)
    {
        var img = new Bgra32Image(Width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < Width; x++)
        {
            var n = Hash(x, y, seed);
            img.SetPixel(x, y, Bgra32.FromRgb((byte)(100 + (n & 31)), (byte)(150 + (n >> 8 & 31)), 200));
        }
        return img;
    }

    private sealed record Scenario(Bgra32Image Page, int Viewport, Bgra32Image? Header, Bgra32Image? Footer)
    {
        public int HeaderHeight => Header?.Height ?? 0;
        public int FooterHeight => Footer?.Height ?? 0;
        public int FrameHeight => HeaderHeight + Viewport + FooterHeight;
        public int MaxScroll => Page.Height - Viewport;

        public Bgra32Image Frame(int scroll)
        {
            var frame = new Bgra32Image(Width, FrameHeight);
            if (Header is not null) frame.Blit(Header, 0, 0);
            frame.Blit(Page.Crop(new PixelRect(0, scroll, Width, Viewport)), 0, HeaderHeight);
            if (Footer is not null) frame.Blit(Footer, 0, HeaderHeight + Viewport);

            // Scroll bar: track + a thumb whose position depends on the scroll offset.
            var thumbTop = HeaderHeight + (int)((long)scroll * (Viewport - 30) / Math.Max(1, MaxScroll));
            for (var y = 0; y < FrameHeight; y++)
            for (var x = Width - ScrollbarWidth; x < Width; x++)
            {
                var inThumb = y >= thumbTop && y < thumbTop + 30;
                frame.SetPixel(x, y, inThumb ? Bgra32.FromRgb(90, 90, 90) : Bgra32.FromRgb(230, 230, 230));
            }
            return frame;
        }

        /// <summary>What a perfect stitch of scroll range [0, lastScroll] looks like (ignoring the scroll bar).</summary>
        public Bgra32Image Expected(int lastScroll)
        {
            var img = new Bgra32Image(Width, HeaderHeight + lastScroll + Viewport + FooterHeight);
            if (Header is not null) img.Blit(Header, 0, 0);
            img.Blit(Page.Crop(new PixelRect(0, 0, Width, lastScroll + Viewport)), 0, HeaderHeight);
            if (Footer is not null) img.Blit(Footer, 0, HeaderHeight + lastScroll + Viewport);
            return img;
        }
    }

    private static void AssertSameContent(Bgra32Image expected, Bgra32Image actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (var y = 0; y < expected.Height; y++)
        {
            var e = expected.GetRowReadOnly(y)[..(CompareWidth * 4)];
            var a = actual.GetRowReadOnly(y)[..(CompareWidth * 4)];
            if (!e.SequenceEqual(a))
                Assert.Fail($"Row {y} of {expected.Height} differs.");
        }
    }

    private static IEnumerable<int> ScrollPositions(int step, int max)
    {
        for (var s = 0; s < max; s += step) yield return s;
        yield return max;
    }

    public static TheoryData<int, int, int> StepCases() => new()
    {
        // step, header, footer
        { 37, 0, 0 },
        { 120, 0, 0 },
        { 5, 0, 0 },
        { 37, 48, 0 },
        { 37, 0, 32 },
        { 37, 48, 32 },
        { 120, 48, 32 },
        { 5, 48, 32 },
        { 200, 60, 40 },
        { 1, 20, 20 },
    };

    [Theory]
    [MemberData(nameof(StepCases))]
    public void Compose_ReproducesHeaderPageAndFooter(int step, int header, int footer)
    {
        var page = MakePage(step == 1 ? 900 : 2600);
        var scenario = new Scenario(page, 400,
            header > 0 ? MakeFixedBand(header, 7) : null,
            footer > 0 ? MakeFixedBand(footer, 9) : null);

        var stitcher = new ScrollStitcher();
        var prev = -1;
        foreach (var s in ScrollPositions(step, scenario.MaxScroll))
        {
            var result = stitcher.AddFrame(scenario.Frame(s));
            if (prev < 0)
            {
                Assert.Equal(StitchStatus.First, result.Status);
                Assert.Equal(scenario.FrameHeight, result.TotalHeight);
            }
            else
            {
                Assert.True(result.Status == StitchStatus.Appended, $"scroll {prev}->{s}: {result.Status}");
                Assert.Equal(s - prev, result.Offset);
                Assert.Equal(scenario.FrameHeight + s, result.TotalHeight);
            }
            prev = s;
        }

        // Re-adding the final frame (page end reached) is reported as NoChange and does not alter the output.
        Assert.Equal(StitchStatus.NoChange, stitcher.AddFrame(scenario.Frame(scenario.MaxScroll)).Status);

        var composed = stitcher.Compose();
        Assert.Equal(stitcher.TotalHeight, composed.Height);
        AssertSameContent(scenario.Expected(scenario.MaxScroll), composed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Compose_RandomizedScenarios(int seed)
    {
        var rng = new Random(seed);
        var header = rng.Next(0, 3) == 0 ? 0 : rng.Next(1, 80);
        var footer = rng.Next(0, 3) == 0 ? 0 : rng.Next(1, 60);
        var viewport = rng.Next(200, 500);
        var scenario = new Scenario(MakePage(rng.Next(1500, 3000), seed),
            viewport,
            header > 0 ? MakeFixedBand(header, seed * 11) : null,
            footer > 0 ? MakeFixedBand(footer, seed * 13) : null);

        var stitcher = new ScrollStitcher();
        stitcher.AddFrame(scenario.Frame(0));
        var s = 0;
        while (s < scenario.MaxScroll)
        {
            var step = Math.Min(rng.Next(1, viewport / 2), scenario.MaxScroll - s);
            s += step;
            var r = stitcher.AddFrame(scenario.Frame(s));
            Assert.True(r.Status == StitchStatus.Appended && r.Offset == step,
                $"seed {seed}: scroll to {s} (step {step}, header {header}, footer {footer}, viewport {viewport}) -> {r}");
        }
        AssertSameContent(scenario.Expected(s), stitcher.Compose());
    }

    [Fact]
    public void Compose_WithIrregularSteps()
    {
        var scenario = new Scenario(MakePage(2600), 400, MakeFixedBand(40, 3), MakeFixedBand(25, 4));
        var stitcher = new ScrollStitcher();
        int[] steps = [33, 90, 7, 150, 64, 3, 220, 41, 99, 12];
        var s = 0;
        stitcher.AddFrame(scenario.Frame(0));
        foreach (var d in steps)
        {
            s += d;
            var r = stitcher.AddFrame(scenario.Frame(s));
            Assert.Equal(StitchStatus.Appended, r.Status);
            Assert.Equal(d, r.Offset);
        }
        AssertSameContent(scenario.Expected(s), stitcher.Compose());
        Assert.Equal(steps.Length + 1, stitcher.FrameCount);
        Assert.Equal(Width, stitcher.Width);
    }

    [Fact]
    public void FirstFrame_ComposeReturnsCopyOfIt()
    {
        var scenario = new Scenario(MakePage(1000), 300, null, null);
        var stitcher = new ScrollStitcher();
        var frame = scenario.Frame(0);
        var r = stitcher.AddFrame(frame);
        Assert.Equal(new StitchFrameResult(StitchStatus.First, 0, 300), r);
        Assert.Equal(1, stitcher.FrameCount);

        var composed = stitcher.Compose();
        Assert.Equal(frame.Pixels, composed.Pixels);
        frame.SetPixel(0, 0, Bgra32.FromRgb(1, 2, 3)); // stitcher must have cloned the frame
        Assert.NotEqual(frame.GetPixel(0, 0), stitcher.Compose().GetPixel(0, 0));
    }

    [Fact]
    public void Compose_WithoutFrames_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ScrollStitcher().Compose());
    }

    [Fact]
    public void IdenticalFrame_IsNoChange()
    {
        var scenario = new Scenario(MakePage(1500), 400, MakeFixedBand(40, 1), null);
        var stitcher = new ScrollStitcher();
        stitcher.AddFrame(scenario.Frame(100));
        var r = stitcher.AddFrame(scenario.Frame(100));
        Assert.Equal(StitchStatus.NoChange, r.Status);
        Assert.Equal(440, r.TotalHeight);
        Assert.Equal(1, stitcher.FrameCount);
    }

    [Fact]
    public void OnlyScrollbarChanged_IsNoChange()
    {
        var scenario = new Scenario(MakePage(1500), 400, null, null);
        var stitcher = new ScrollStitcher();
        stitcher.AddFrame(scenario.Frame(100));
        var frame = scenario.Frame(100);
        for (var y = 0; y < frame.Height; y++)
        for (var x = Width - ScrollbarWidth; x < Width; x++)
            frame.SetPixel(x, y, Bgra32.FromRgb(0, (byte)y, 0));
        Assert.Equal(StitchStatus.NoChange, stitcher.AddFrame(frame).Status);
    }

    [Theory]
    [InlineData(395)]   // only 5 rows overlap
    [InlineData(380)]   // overlap below the minimum (middle / 10)
    [InlineData(800)]   // no overlap at all
    public void JumpTooFar_IsNoOverlap(int jump)
    {
        var scenario = new Scenario(MakePage(2600), 400, MakeFixedBand(40, 1), MakeFixedBand(30, 2));
        var stitcher = new ScrollStitcher();
        stitcher.AddFrame(scenario.Frame(0));
        var r = stitcher.AddFrame(scenario.Frame(jump));
        Assert.Equal(StitchStatus.NoOverlap, r.Status);
        Assert.Equal(scenario.FrameHeight, r.TotalHeight);

        // The stitcher keeps comparing against the last accepted frame, so a sane next frame still works.
        var ok = stitcher.AddFrame(scenario.Frame(50));
        Assert.Equal(StitchStatus.Appended, ok.Status);
        Assert.Equal(50, ok.Offset);
    }

    [Fact]
    public void ScrollingUp_IsNoOverlap()
    {
        var scenario = new Scenario(MakePage(2600), 400, null, null);
        var stitcher = new ScrollStitcher();
        stitcher.AddFrame(scenario.Frame(500));
        Assert.Equal(StitchStatus.NoOverlap, stitcher.AddFrame(scenario.Frame(450)).Status);
    }

    [Fact]
    public void CompletelyDifferentContent_IsNoOverlap()
    {
        var a = new Scenario(MakePage(1000, seed: 1), 400, null, null);
        var b = new Scenario(MakePage(1000, seed: 2), 400, null, null);
        var stitcher = new ScrollStitcher();
        stitcher.AddFrame(a.Frame(0));
        Assert.Equal(StitchStatus.NoOverlap, stitcher.AddFrame(b.Frame(0)).Status);
    }

    [Fact]
    public void MaxHeight_IsLimitReached()
    {
        var scenario = new Scenario(MakePage(2600), 400, null, null);
        var stitcher = new ScrollStitcher(new StitchOptions { MaxHeight = 400 + 100 });
        stitcher.AddFrame(scenario.Frame(0));
        Assert.Equal(StitchStatus.Appended, stitcher.AddFrame(scenario.Frame(60)).Status);
        var r = stitcher.AddFrame(scenario.Frame(120));
        Assert.Equal(StitchStatus.LimitReached, r.Status);
        Assert.Equal(460, r.TotalHeight);
        AssertSameContent(scenario.Expected(60), stitcher.Compose());
    }

    [Fact]
    public void DifferentFrameSize_Throws()
    {
        var stitcher = new ScrollStitcher();
        stitcher.AddFrame(new Bgra32Image(Width, 100));
        Assert.Throws<ArgumentException>(() => stitcher.AddFrame(new Bgra32Image(Width, 101)));
        Assert.Throws<ArgumentException>(() => stitcher.AddFrame(new Bgra32Image(Width + 1, 100)));
    }

    [Fact]
    public void BlankViewport_IsNotMatched()
    {
        // A viewport that is entirely uniform carries no information -> no offset can be trusted.
        var stitcher = new ScrollStitcher();
        var white = new Bgra32Image(Width, 300);
        Array.Fill(white.Pixels, (byte)255);
        stitcher.AddFrame(white);
        var other = white.Clone();
        for (var x = 0; x < Width; x++) other.SetPixel(x, 299, Bgra32.FromRgb(0, 0, 0));
        Assert.NotEqual(StitchStatus.Appended, stitcher.AddFrame(other).Status);
    }
}
