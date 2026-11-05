using AiPhoto.Core.Imaging;
using AiPhoto.Core.Stitching;

namespace AiPhoto.Core.Tests.Imaging;

public class ImageRotationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-4)]
    public void ZeroTurns_ReturnsSameInstance(int turns)
    {
        var img = Bgra32ImageTests.Coordinates(3, 2);
        Assert.Same(img, ImageRotation.RotateClockwise(img, turns));
    }

    [Fact]
    public void Clockwise90_MovesTopLeftToTopRight()
    {
        // 3x2 source:  A B C      rotated 90° CW (2x3):  D A
        //              D E F                             E B
        //                                                F C
        var src = Bgra32ImageTests.Coordinates(3, 2);
        var dst = ImageRotation.RotateClockwise(src, 1);
        Assert.Equal(2, dst.Width);
        Assert.Equal(3, dst.Height);
        Assert.Equal(src.GetPixel(0, 1), dst.GetPixel(0, 0)); // D
        Assert.Equal(src.GetPixel(0, 0), dst.GetPixel(1, 0)); // A
        Assert.Equal(src.GetPixel(2, 0), dst.GetPixel(1, 2)); // C
        Assert.Equal(src.GetPixel(2, 1), dst.GetPixel(0, 2)); // F
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rotations_Compose(int turns)
    {
        var src = Bgra32ImageTests.Coordinates(7, 4);
        var back = ImageRotation.RotateClockwise(ImageRotation.RotateClockwise(src, turns), 4 - turns);
        Assert.Equal(src.Pixels, back.Pixels);
        Assert.Equal(ImageRotation.RotateClockwise(src, 3).Pixels, ImageRotation.RotateClockwise(src, -1).Pixels);
        var twice = ImageRotation.RotateClockwise(ImageRotation.RotateClockwise(src, 1), 1);
        Assert.Equal(ImageRotation.RotateClockwise(src, 2).Pixels, twice.Pixels);
    }

    [Fact]
    public void Rotation_HandlesPaddedStride()
    {
        var img = new Bgra32Image(2, 2, 12, new byte[24]);
        img.SetPixel(1, 0, Bgra32.FromRgb(1, 2, 3));
        var r = ImageRotation.RotateClockwise(img, 1);
        Assert.Equal(Bgra32.FromRgb(1, 2, 3), r.GetPixel(1, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HalfImage_RotatesLikeBgraImage(int turns)
    {
        var half = new RgbaHalfImage(5, 3);
        for (var i = 0; i < half.Pixels.Length; i++) half.Pixels[i] = (Half)i;
        var r = ImageRotation.RotateClockwise(half, turns);
        var back = ImageRotation.RotateClockwise(r, 4 - turns);
        Assert.Equal(half.Pixels, back.Pixels);
        if (turns == 1)
        {
            Assert.Equal(3, r.Width);
            Assert.Equal(5, r.Height);
            // dst (0,0) = src (0, h-1)
            Assert.Equal(half.Pixels[(2 * 5 + 0) * 4], r.Pixels[0]);
        }
    }
}

public class LongImageSplitterTests
{
    private static Bgra32Image Page(int width, int height, Func<int, bool> isBlankRow)
    {
        var img = new Bgra32Image(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            img.SetPixel(x, y, isBlankRow(y) ? Bgra32.FromRgb(255, 255, 255) : Bgra32.FromRgb((byte)(x * 37), (byte)y, 0));
        return img;
    }

    private static void AssertCoversImage(Bgra32Image img, IReadOnlyList<PixelRect> chunks, int max)
    {
        var y = 0;
        foreach (var c in chunks)
        {
            Assert.Equal(0, c.X);
            Assert.Equal(img.Width, c.Width);
            Assert.Equal(y, c.Y);
            Assert.InRange(c.Height, 1, max);
            y = c.Bottom;
        }
        Assert.Equal(img.Height, y);
    }

    [Fact]
    public void ShortImage_IsSingleChunk()
    {
        var img = Page(50, 500, _ => false);
        Assert.Equal([new PixelRect(0, 0, 50, 500)], LongImageSplitter.Split(img, 500));
    }

    [Fact]
    public void NoBlankRows_CutsAtMaxHeight()
    {
        var img = Page(50, 2500, _ => false);
        var chunks = LongImageSplitter.Split(img, 1000);
        Assert.Equal([1000, 1000, 500], chunks.Select(c => c.Height));
        AssertCoversImage(img, chunks, 1000);
    }

    [Fact]
    public void CutsOnBlankRowsNearIdealPosition()
    {
        // Text lines of 30px separated by 10px gaps; gaps at y % 40 >= 30.
        var img = Page(60, 3000, y => y % 40 >= 30);
        var chunks = LongImageSplitter.Split(img, 1000);
        AssertCoversImage(img, chunks, 1000);
        foreach (var c in chunks.Take(chunks.Count - 1))
            Assert.True(c.Bottom % 40 >= 30, $"cut at {c.Bottom} slices a text line");
        Assert.True(chunks[0].Height > 1000 - 160);
    }

    [Fact]
    public void TooSmallMaxHeight_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LongImageSplitter.Split(new Bgra32Image(10, 10), 15));
    }

    [Theory]
    [InlineData(100, 800)]
    [InlineData(1000, 1500)]
    [InlineData(5000, 3000)]
    public void SuggestChunkHeight_IsClamped(int width, int expected)
    {
        Assert.Equal(expected, LongImageSplitter.SuggestChunkHeight(width));
    }
}
