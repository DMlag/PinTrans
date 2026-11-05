namespace AiPhoto.Core.Imaging;

/// <summary>Integer rectangle in pixel coordinates (X/Y = top-left, exclusive right/bottom).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromLTRB(int left, int top, int right, int bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>Normalizes two arbitrary corner points into a rectangle.</summary>
    public static PixelRect FromPoints(int x1, int y1, int x2, int y2)
        => FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public PixelRect Intersect(PixelRect other)
    {
        var l = Math.Max(X, other.X);
        var t = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : FromLTRB(l, t, r, b);
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLTRB(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public PixelRect Inflate(int amount) => FromLTRB(X - amount, Y - amount, Right + amount, Bottom + amount);

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
