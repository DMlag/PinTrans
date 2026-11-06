using AiPhoto.Core.Imaging;
using AiPhoto.Core.Snapping;

namespace AiPhoto.Core.Tests.Snapping;

public sealed class EdgeRectDetectorTests
{
    [Fact]
    public void DetectAroundPoint_FindsSolidFilledButtonAndOuterCard()
    {
        var img = new Bgra32Image(600, 400);
        FillRect(img, new PixelRect(0, 0, 600, 400), new Bgra32(240, 240, 240));
        // Outer card: (80, 60, 400, 260) white with gray border
        var cardRect = new PixelRect(80, 60, 400, 260);
        FillRect(img, cardRect, new Bgra32(255, 255, 255));
        DrawBorder(img, cardRect, new Bgra32(180, 180, 180));

        // Inner blue button: (140, 120, 120, 40)
        var buttonRect = new PixelRect(140, 120, 120, 40);
        FillRect(img, buttonRect, new Bgra32(246, 124, 47));

        var candidates = EdgeRectDetector.DetectAroundPoint(img, 180, 140, img.Bounds);

        Assert.Contains(candidates, c => c.Kind == SnapKind.VisualBox && SnapIndex.AreNearlyEqual(c.Rect, buttonRect, tolerance: 4));
        Assert.Contains(candidates, c => c.Kind == SnapKind.VisualBox && SnapIndex.AreNearlyEqual(c.Rect, cardRect, tolerance: 4));
    }

    [Fact]
    public void DetectAroundPoint_FindsSingleTextLineAndMultiLineTextBlock()
    {
        var img = new Bgra32Image(500, 300);
        FillRect(img, img.Bounds, new Bgra32(255, 255, 255));

        // Draw 3 simulated text lines at y=80..94, y=104..118, y=128..142 from x=100..320
        DrawSimulatedTextLine(img, x0: 100, x1: 320, y0: 80, height: 14, new Bgra32(30, 30, 30));
        DrawSimulatedTextLine(img, x0: 100, x1: 300, y0: 104, height: 14, new Bgra32(30, 30, 30));
        DrawSimulatedTextLine(img, x0: 100, x1: 260, y0: 128, height: 14, new Bgra32(30, 30, 30));

        var candidates = EdgeRectDetector.DetectAroundPoint(img, 180, 110, img.Bounds);

        var line = candidates.FirstOrDefault(c => c.Label == "文本行");
        var block = candidates.FirstOrDefault(c => c.Label == "文本块");

        Assert.False(line.Rect.IsEmpty);
        Assert.InRange(line.Rect.Y, 98, 106);
        Assert.InRange(line.Rect.Height, 12, 24);
        Assert.InRange(line.Rect.X, 92, 104);
        Assert.InRange(line.Rect.Right, 296, 310);

        Assert.False(block.Rect.IsEmpty);
        Assert.InRange(block.Rect.Y, 74, 82);
        Assert.InRange(block.Rect.Bottom, 140, 150);
    }

    [Fact]
    public void DetectAroundPoint_UniformImage_ReturnsEmpty()
    {
        var img = new Bgra32Image(300, 200);
        FillRect(img, img.Bounds, new Bgra32(128, 128, 128));

        var candidates = EdgeRectDetector.DetectAroundPoint(img, 150, 100, img.Bounds);
        Assert.Empty(candidates);
    }

    private static void FillRect(Bgra32Image img, PixelRect rect, Bgra32 color)
    {
        var r = rect.Intersect(img.Bounds);
        for (var y = r.Y; y < r.Bottom; y++)
        for (var x = r.X; x < r.Right; x++)
            img.SetPixel(x, y, color);
    }

    private static void DrawBorder(Bgra32Image img, PixelRect rect, Bgra32 color)
    {
        for (var x = rect.X; x < rect.Right; x++)
        {
            img.SetPixel(x, rect.Y, color);
            img.SetPixel(x, rect.Bottom - 1, color);
        }
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            img.SetPixel(rect.X, y, color);
            img.SetPixel(rect.Right - 1, y, color);
        }
    }

    private static void DrawSimulatedTextLine(Bgra32Image img, int x0, int x1, int y0, int height, Bgra32 ink)
    {
        // Alternating vertical/horizontal glyph strokes everyfew pixels to mimic real text characters
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var charIndex = (x - x0) / 10;
                var inCharX = (x - x0) % 10;
                if (inCharX is >= 1 and <= 7 && ((inCharX + y + charIndex) % 2 == 0))
                {
                    img.SetPixel(x, y, ink);
                }
            }
        }
    }
}
