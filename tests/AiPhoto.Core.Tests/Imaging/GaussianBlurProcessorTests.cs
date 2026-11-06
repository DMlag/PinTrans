using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Tests.Imaging;

public sealed class GaussianBlurProcessorTests
{
    [Fact]
    public void BlurRegion_UniformImage_PreservesExactColor()
    {
        var img = new Bgra32Image(80, 60);
        var color = new Bgra32(45, 130, 220);
        for (var y = 0; y < img.Height; y++)
        for (var x = 0; x < img.Width; x++)
            img.SetPixel(x, y, color);

        var blurred = GaussianBlurProcessor.BlurRegion(img, new PixelRect(10, 10, 50, 40), radius: 12);

        Assert.Equal(50, blurred.Width);
        Assert.Equal(40, blurred.Height);
        for (var y = 0; y < blurred.Height; y++)
        for (var x = 0; x < blurred.Width; x++)
        {
            Assert.Equal(color, blurred.GetPixel(x, y));
        }
    }

    [Fact]
    public void BlurRegion_SharpStepEdge_CreatesSmoothGradientTransition()
    {
        var img = new Bgra32Image(100, 40);
        for (var y = 0; y < img.Height; y++)
        for (var x = 0; x < img.Width; x++)
        {
            var v = x < 50 ? (byte)0 : (byte)255;
            img.SetPixel(x, y, new Bgra32(v, v, v));
        }

        var blurred = GaussianBlurProcessor.BlurRegion(img, img.Bounds, radius: 8);

        // Far left stays near 0, center is ~127, far right stays near 255, and values increase monotonically.
        Assert.Equal(0, blurred.GetPixel(5, 20).R);
        Assert.InRange(blurred.GetPixel(50, 20).R, 110, 145);
        Assert.Equal(255, blurred.GetPixel(95, 20).R);

        var prev = 0;
        for (var x = 0; x < blurred.Width; x++)
        {
            var cur = blurred.GetPixel(x, 20).R;
            Assert.True(cur >= prev, $"Expected monotonic increase at x={x}, got {cur} < {prev}");
            prev = cur;
        }
    }
}
