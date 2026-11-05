using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Tests.Imaging;

public class TextColorEstimatorTests
{
    private static Bgra32Image Fill(int w, int h, Bgra32 c)
    {
        var img = new Bgra32Image(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            img.SetPixel(x, y, c);
        return img;
    }

    /// <summary>Draws pseudo "glyphs": vertical and horizontal strokes inside <paramref name="block"/>.</summary>
    private static void DrawGlyphs(Bgra32Image img, PixelRect block, Bgra32 ink, bool touchBorder = false)
    {
        var inset = touchBorder ? 0 : 3;
        for (var gx = block.X + inset; gx + 6 < block.Right - inset; gx += 10)
        {
            for (var y = block.Y + inset; y < block.Bottom - inset; y++)
            {
                img.SetPixel(gx, y, ink);
                img.SetPixel(gx + 1, y, ink);
            }
            for (var x = gx; x < gx + 6; x++)
            {
                img.SetPixel(x, block.Y + inset, ink);
                img.SetPixel(x, (block.Y + block.Bottom) / 2, ink);
            }
        }
    }

    [Fact]
    public void WhiteBackground_BlackGlyphs()
    {
        var white = Bgra32.FromRgb(255, 255, 255);
        var img = Fill(200, 100, white);
        var block = new PixelRect(40, 30, 100, 30);
        DrawGlyphs(img, block, Bgra32.FromRgb(0, 0, 0));

        var colors = TextColorEstimator.Estimate(img, block);
        Assert.Equal(white, colors.Background);
        Assert.True(colors.Foreground.Luma < 0.2, $"foreground {colors.Foreground} is not dark");
        Assert.True(TextColorEstimator.ContrastRatio(colors.Foreground, colors.Background) >= 7);
    }

    [Fact]
    public void DarkBackground_GivesLightForeground()
    {
        var bg = Bgra32.FromRgb(30, 32, 40);
        var img = Fill(200, 100, bg);
        var block = new PixelRect(20, 20, 120, 40);
        DrawGlyphs(img, block, Bgra32.FromRgb(230, 230, 230));

        var colors = TextColorEstimator.Estimate(img, block);
        Assert.Equal(bg, colors.Background);
        Assert.True(colors.Foreground.Luma > 0.7, $"foreground {colors.Foreground} is not light");
    }

    [Fact]
    public void ColoredGlyphs_KeepTheirColour()
    {
        var img = Fill(200, 100, Bgra32.FromRgb(255, 255, 255));
        var block = new PixelRect(20, 20, 120, 40);
        var red = Bgra32.FromRgb(200, 0, 0);
        DrawGlyphs(img, block, red);
        var colors = TextColorEstimator.Estimate(img, block);
        Assert.Equal(red, colors.Foreground);
    }

    [Fact]
    public void GlyphsTouchingBorder_DoNotChangeBackgroundMedian()
    {
        var bg = Bgra32.FromRgb(240, 240, 200);
        var img = Fill(200, 100, bg);
        var block = new PixelRect(20, 20, 120, 40);
        DrawGlyphs(img, block, Bgra32.FromRgb(0, 0, 0), touchBorder: true);
        Assert.Equal(bg, TextColorEstimator.Estimate(img, block).Background);
    }

    [Fact]
    public void UniformBlock_FallsBackToContrastingBlackOrWhite()
    {
        var light = TextColorEstimator.Estimate(Fill(50, 50, Bgra32.FromRgb(250, 250, 250)), new PixelRect(10, 10, 20, 20));
        Assert.True(light.Foreground.Luma < 0.2);

        var dark = TextColorEstimator.Estimate(Fill(50, 50, Bgra32.FromRgb(10, 10, 10)), new PixelRect(10, 10, 20, 20));
        Assert.True(dark.Foreground.Luma > 0.8);
    }

    [Fact]
    public void LowContrastGlyphs_FallBackToBlackOrWhite()
    {
        // Mid-grey "glyphs" on a slightly lighter grey: distinct enough to be candidates but contrast < 3.
        var bg = Bgra32.FromRgb(160, 160, 160);
        var img = Fill(100, 60, bg);
        var block = new PixelRect(10, 10, 80, 40);
        DrawGlyphs(img, block, Bgra32.FromRgb(100, 100, 100));
        var colors = TextColorEstimator.Estimate(img, block);
        Assert.True(TextColorEstimator.ContrastRatio(colors.Foreground, colors.Background) >= 3,
            $"fg {colors.Foreground} on {colors.Background}");
    }

    [Fact]
    public void BlockAtImageEdge_And_BlockOutside()
    {
        var img = Fill(30, 30, Bgra32.FromRgb(255, 255, 255));
        var edge = TextColorEstimator.Estimate(img, new PixelRect(0, 0, 30, 30));
        Assert.Equal(Bgra32.FromRgb(255, 255, 255), edge.Background);

        var outside = TextColorEstimator.Estimate(img, new PixelRect(100, 100, 10, 10));
        Assert.True(outside.Background.Luma > 0.9);
        Assert.True(outside.Foreground.Luma < 0.2);

        var tiny = TextColorEstimator.Estimate(img, new PixelRect(29, 29, 1, 1));
        Assert.Equal(Bgra32.FromRgb(255, 255, 255), tiny.Background);
    }

    [Fact]
    public void ContrastRatio_Sanity()
    {
        var black = Bgra32.FromRgb(0, 0, 0);
        var white = Bgra32.FromRgb(255, 255, 255);
        Assert.Equal(21.0, TextColorEstimator.ContrastRatio(black, white), 2);
        Assert.Equal(21.0, TextColorEstimator.ContrastRatio(white, black), 2);
        Assert.Equal(1.0, TextColorEstimator.ContrastRatio(white, white), 6);
        // WCAG reference: #777777 on white is ~4.48.
        Assert.Equal(4.48, TextColorEstimator.ContrastRatio(Bgra32.FromRgb(0x77, 0x77, 0x77), white), 2);
        Assert.Equal(1.0, TextColorEstimator.RelativeLuminance(white), 4);
        Assert.Equal(0.0, TextColorEstimator.RelativeLuminance(black), 6);
    }
}
