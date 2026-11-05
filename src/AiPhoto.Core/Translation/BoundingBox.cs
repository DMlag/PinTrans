using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Translation;

public static class BoundingBox
{
    /// <summary>
    /// Converts a Gemini <c>box_2d</c> (<c>[ymin, xmin, ymax, xmax]</c>, normalised 0..1000) to image pixels.
    /// Returns <c>null</c> for malformed boxes.
    /// </summary>
    public static PixelRect? FromNormalized(IReadOnlyList<int>? box, int imageWidth, int imageHeight, int scale = 1000)
    {
        if (box is null || box.Count != 4 || imageWidth <= 0 || imageHeight <= 0) return null;

        double y0 = box[0], x0 = box[1], y1 = box[2], x1 = box[3];
        if (y0 > y1) (y0, y1) = (y1, y0);
        if (x0 > x1) (x0, x1) = (x1, x0);

        y0 = Math.Clamp(y0, 0, scale); y1 = Math.Clamp(y1, 0, scale);
        x0 = Math.Clamp(x0, 0, scale); x1 = Math.Clamp(x1, 0, scale);

        var left = (int)Math.Floor(x0 / scale * imageWidth);
        var top = (int)Math.Floor(y0 / scale * imageHeight);
        var right = (int)Math.Ceiling(x1 / scale * imageWidth);
        var bottom = (int)Math.Ceiling(y1 / scale * imageHeight);

        if (right - left < 2 || bottom - top < 2) return null;
        return PixelRect.FromLTRB(left, top, right, bottom);
    }
}
