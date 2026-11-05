using AiPhoto.Core.Imaging;
using AiPhoto.Core.Translation;

namespace AiPhoto.Core.Tests.Translation;

public class BoundingBoxTests
{
    [Fact]
    public void Normal()
    {
        // [ymin, xmin, ymax, xmax] in 0..1000.
        var r = BoundingBox.FromNormalized([100, 200, 500, 750], 2000, 1000);
        Assert.Equal(PixelRect.FromLTRB(400, 100, 1500, 500), r);
    }

    [Fact]
    public void RoundsOutwards()
    {
        // 333/1000 * 100 = 33.3 -> floor 33 ; 667/1000 * 100 = 66.7 -> ceil 67.
        var r = BoundingBox.FromNormalized([333, 333, 667, 667], 100, 100);
        Assert.Equal(PixelRect.FromLTRB(33, 33, 67, 67), r);
    }

    [Fact]
    public void FullImage()
    {
        Assert.Equal(new PixelRect(0, 0, 640, 480), BoundingBox.FromNormalized([0, 0, 1000, 1000], 640, 480));
    }

    [Fact]
    public void SwappedCoordinates_AreNormalized()
    {
        var expected = BoundingBox.FromNormalized([100, 200, 500, 750], 1000, 1000);
        Assert.Equal(expected, BoundingBox.FromNormalized([500, 750, 100, 200], 1000, 1000));
        Assert.Equal(expected, BoundingBox.FromNormalized([500, 200, 100, 750], 1000, 1000));
    }

    [Fact]
    public void OutOfRange_IsClamped()
    {
        Assert.Equal(PixelRect.FromLTRB(0, 0, 500, 1000), BoundingBox.FromNormalized([-50, -10, 1200, 500], 1000, 1000));
    }

    [Fact]
    public void CustomScale()
    {
        Assert.Equal(PixelRect.FromLTRB(50, 25, 100, 50), BoundingBox.FromNormalized([1, 2, 2, 4], 100, 100, scale: 4));
    }

    [Theory]
    [MemberData(nameof(MalformedBoxes))]
    public void Malformed_ReturnsNull(int[]? box, int w, int h)
    {
        Assert.Null(BoundingBox.FromNormalized(box, w, h));
    }

    public static TheoryData<int[]?, int, int> MalformedBoxes() => new()
    {
        { null, 100, 100 },
        { [], 100, 100 },
        { [1, 2, 3], 100, 100 },
        { [1, 2, 3, 4, 5], 100, 100 },
        { [100, 100, 200, 200], 0, 100 },
        { [100, 100, 200, 200], 100, -1 },
        { [100, 100, 100, 500], 100, 100 },        // zero height
        { [100, 500, 600, 500], 100, 100 },        // zero width
        { [1100, 0, 1200, 1000], 100, 100 },       // completely below the image -> collapses after clamping
        { [500, 500, 505, 505], 100, 100 },        // smaller than 2px
    };
}
