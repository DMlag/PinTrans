using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Tests.Imaging;

public class Bgra32ImageTests
{
    /// <summary>Image whose pixel (x, y) encodes its own coordinates so copies can be verified exactly.</summary>
    internal static Bgra32Image Coordinates(int w, int h)
    {
        var img = new Bgra32Image(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            img.SetPixel(x, y, new Bgra32((byte)x, (byte)y, (byte)(x ^ y), 255));
        return img;
    }

    private static Bgra32 Coord(int x, int y) => new((byte)x, (byte)y, (byte)(x ^ y), 255);

    [Fact]
    public void Constructor_AllocatesTightlyPackedBuffer()
    {
        var img = new Bgra32Image(7, 3);
        Assert.Equal(28, img.Stride);
        Assert.Equal(84, img.Pixels.Length);
        Assert.Equal(new PixelRect(0, 0, 7, 3), img.Bounds);
    }

    [Fact]
    public void Constructor_RejectsTooSmallStrideOrBuffer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bgra32Image(4, 2, 15, new byte[32]));
        Assert.Throws<ArgumentException>(() => new Bgra32Image(4, 2, 16, new byte[31]));
    }

    [Fact]
    public void SetPixel_GetPixel_RoundTripAndByteOrderIsBgra()
    {
        var img = new Bgra32Image(2, 2);
        img.SetPixel(1, 1, Bgra32.FromRgb(10, 20, 30));
        Assert.Equal(Bgra32.FromRgb(10, 20, 30), img.GetPixel(1, 1));
        var i = 1 * img.Stride + 4;
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, img.Pixels[i..(i + 4)]);
    }

    [Fact]
    public void Crop_CopiesRegionExactly()
    {
        var img = Coordinates(20, 10);
        var crop = img.Crop(new PixelRect(3, 2, 5, 4));
        Assert.Equal(5, crop.Width);
        Assert.Equal(4, crop.Height);
        Assert.Equal(20, crop.Stride);
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 5; x++)
            Assert.Equal(Coord(x + 3, y + 2), crop.GetPixel(x, y));
    }

    [Fact]
    public void Crop_ClampsToBounds()
    {
        var img = Coordinates(10, 10);
        var crop = img.Crop(new PixelRect(-5, 7, 8, 10));
        Assert.Equal(3, crop.Width);
        Assert.Equal(3, crop.Height);
        Assert.Equal(Coord(0, 7), crop.GetPixel(0, 0));
        Assert.Equal(Coord(2, 9), crop.GetPixel(2, 2));
    }

    [Fact]
    public void Crop_OutsideBounds_ReturnsEmptyImage()
    {
        var crop = Coordinates(10, 10).Crop(new PixelRect(20, 20, 5, 5));
        Assert.Equal(0, crop.Width);
        Assert.Equal(0, crop.Height);
    }

    [Fact]
    public void Crop_RespectsSourceStridePadding()
    {
        // 3x2 image with 4 bytes of padding per row.
        var pixels = new byte[2 * 16];
        var img = new Bgra32Image(3, 2, 16, pixels);
        img.SetPixel(2, 1, Bgra32.FromRgb(1, 2, 3));
        var crop = img.Crop(new PixelRect(1, 1, 2, 1));
        Assert.Equal(Bgra32.FromRgb(1, 2, 3), crop.GetPixel(1, 0));
    }

    [Fact]
    public void Blit_CopiesInside()
    {
        var dst = new Bgra32Image(10, 10);
        var src = Coordinates(3, 2);
        dst.Blit(src, 4, 5);
        for (var y = 0; y < 10; y++)
        for (var x = 0; x < 10; x++)
        {
            var inside = x is >= 4 and < 7 && y is >= 5 and < 7;
            Assert.Equal(inside ? Coord(x - 4, y - 5) : default, dst.GetPixel(x, y));
        }
    }

    [Theory]
    [InlineData(-2, -3)]   // clipped top-left
    [InlineData(8, 7)]     // clipped bottom-right
    [InlineData(-2, 8)]    // clipped bottom-left
    [InlineData(7, -4)]    // clipped top-right
    public void Blit_ClipsAtEdges(int dx, int dy)
    {
        var dst = new Bgra32Image(10, 10);
        var src = Coordinates(5, 5);
        dst.Blit(src, dx, dy);
        for (var y = 0; y < 10; y++)
        for (var x = 0; x < 10; x++)
        {
            int sx = x - dx, sy = y - dy;
            var inside = sx is >= 0 and < 5 && sy is >= 0 and < 5;
            Assert.Equal(inside ? Coord(sx, sy) : default, dst.GetPixel(x, y));
        }
    }

    [Fact]
    public void Blit_CompletelyOutside_IsNoOp()
    {
        var dst = new Bgra32Image(4, 4);
        dst.Blit(Coordinates(3, 3), 10, -10);
        dst.Blit(Coordinates(3, 3), -3, 0);
        Assert.All(dst.Pixels, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var img = Coordinates(4, 4);
        var clone = img.Clone();
        clone.SetPixel(0, 0, Bgra32.FromRgb(9, 9, 9));
        Assert.Equal(Coord(0, 0), img.GetPixel(0, 0));
        Assert.Equal(Bgra32.FromRgb(9, 9, 9), clone.GetPixel(0, 0));
    }

    [Fact]
    public void DownscaleToFit_ReturnsSameInstanceWhenSmallEnough()
    {
        var img = new Bgra32Image(100, 50);
        Assert.Same(img, img.DownscaleToFit(100));
        Assert.Same(img, img.DownscaleToFit(1000));
    }

    [Theory]
    [InlineData(4000, 1000, 2048, 2048, 512)]
    [InlineData(1000, 4000, 2048, 512, 2048)]
    [InlineData(301, 300, 100, 100, 100)]
    [InlineData(5000, 3, 100, 100, 1)]
    public void DownscaleToFit_KeepsAspectRatio(int w, int h, int max, int ew, int eh)
    {
        var result = new Bgra32Image(w, h).DownscaleToFit(max);
        Assert.Equal(ew, result.Width);
        Assert.Equal(eh, result.Height);
    }

    [Fact]
    public void DownscaleToFit_BoxFilterAveragesPixels()
    {
        // 4x2 checker of black/white 2x2 blocks -> 2x1: each output pixel averages one block.
        var img = new Bgra32Image(4, 2);
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 4; x++)
            img.SetPixel(x, y, x < 2 ? Bgra32.FromRgb(0, 0, 0) : Bgra32.FromRgb(200, 100, 50));
        var small = img.DownscaleToFit(2);
        Assert.Equal(2, small.Width);
        Assert.Equal(1, small.Height);
        Assert.Equal(Bgra32.FromRgb(0, 0, 0), small.GetPixel(0, 0));
        Assert.Equal(Bgra32.FromRgb(200, 100, 50), small.GetPixel(1, 0));

        // Fine checkerboard averages to mid grey.
        var checker = new Bgra32Image(8, 8);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            checker.SetPixel(x, y, (x + y) % 2 == 0 ? Bgra32.FromRgb(0, 0, 0) : Bgra32.FromRgb(254, 254, 254));
        var grey = checker.DownscaleToFit(4);
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
            Assert.Equal(Bgra32.FromRgb(127, 127, 127), grey.GetPixel(x, y));
    }

    [Fact]
    public void DownscaleToFit_EmptyImage_ReturnsSelf()
    {
        var img = new Bgra32Image(0, 0);
        Assert.Same(img, img.DownscaleToFit(10));
    }

    [Fact]
    public void Bgra32_LumaAndDistance()
    {
        Assert.Equal(1.0, Bgra32.FromRgb(255, 255, 255).Luma, 6);
        Assert.Equal(0.0, Bgra32.FromRgb(0, 0, 0).Luma, 6);
        Assert.Equal(3 * 255 * 255, Bgra32.FromRgb(0, 0, 0).DistanceSquared(Bgra32.FromRgb(255, 255, 255)));
        Assert.Equal("#0A141E", Bgra32.FromRgb(10, 20, 30).ToString());
    }
}
