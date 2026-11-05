using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Tests.Imaging;

public class PixelRectTests
{
    [Fact]
    public void Properties()
    {
        var r = new PixelRect(2, 3, 10, 20);
        Assert.Equal(12, r.Right);
        Assert.Equal(23, r.Bottom);
        Assert.False(r.IsEmpty);
        Assert.True(new PixelRect(0, 0, 0, 5).IsEmpty);
        Assert.True(new PixelRect(0, 0, 5, -1).IsEmpty);
        Assert.True(default(PixelRect).IsEmpty);
    }

    [Fact]
    public void FromLTRB_ClampsNegativeSize()
    {
        Assert.Equal(new PixelRect(1, 2, 3, 4), PixelRect.FromLTRB(1, 2, 4, 6));
        Assert.Equal(new PixelRect(5, 5, 0, 0), PixelRect.FromLTRB(5, 5, 1, 1));
    }

    [Theory]
    [InlineData(10, 20, 30, 50)]
    [InlineData(30, 50, 10, 20)]
    [InlineData(30, 20, 10, 50)]
    [InlineData(10, 50, 30, 20)]
    public void FromPoints_NormalizesAnyCornerOrder(int x1, int y1, int x2, int y2)
    {
        Assert.Equal(new PixelRect(10, 20, 20, 30), PixelRect.FromPoints(x1, y1, x2, y2));
    }

    [Fact]
    public void FromPoints_SamePoint_IsEmpty()
    {
        Assert.True(PixelRect.FromPoints(4, 4, 4, 4).IsEmpty);
    }

    [Fact]
    public void Intersect_Overlapping()
    {
        var a = new PixelRect(0, 0, 10, 10);
        var b = new PixelRect(5, -5, 10, 10);
        Assert.Equal(new PixelRect(5, 0, 5, 5), a.Intersect(b));
        Assert.Equal(a.Intersect(b), b.Intersect(a));
    }

    [Fact]
    public void Intersect_Contained()
    {
        var outer = new PixelRect(0, 0, 100, 100);
        var inner = new PixelRect(10, 20, 30, 40);
        Assert.Equal(inner, outer.Intersect(inner));
    }

    [Theory]
    [InlineData(10, 0)]   // touching edge
    [InlineData(20, 20)]  // far away
    [InlineData(-10, 0)]  // touching left edge
    public void Intersect_Disjoint_IsEmptyDefault(int x, int y)
    {
        var r = new PixelRect(0, 0, 10, 10).Intersect(new PixelRect(x, y, 10, 10));
        Assert.True(r.IsEmpty);
        Assert.Equal(default, r);
    }

    [Fact]
    public void Union_CoversBoth()
    {
        var a = new PixelRect(0, 0, 10, 10);
        var b = new PixelRect(20, 5, 5, 20);
        Assert.Equal(new PixelRect(0, 0, 25, 25), a.Union(b));
        Assert.Equal(a.Union(b), b.Union(a));
    }

    [Fact]
    public void Union_WithEmpty_ReturnsOther()
    {
        var a = new PixelRect(3, 4, 5, 6);
        Assert.Equal(a, a.Union(default));
        Assert.Equal(a, default(PixelRect).Union(a));
        Assert.Equal(a, new PixelRect(100, 100, 0, 0).Union(a));
    }

    [Fact]
    public void OffsetInflateContains()
    {
        var r = new PixelRect(1, 2, 3, 4);
        Assert.Equal(new PixelRect(11, 22, 3, 4), r.Offset(10, 20));
        Assert.Equal(new PixelRect(-1, 0, 7, 8), r.Inflate(2));
        Assert.True(r.Contains(1, 2));
        Assert.True(r.Contains(3, 5));
        Assert.False(r.Contains(4, 2));
        Assert.False(r.Contains(1, 6));
        Assert.False(r.Contains(0, 3));
    }
}
