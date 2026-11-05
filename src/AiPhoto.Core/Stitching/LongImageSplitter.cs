using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Stitching;

/// <summary>
/// Splits a very tall image (long screenshot) into chunks that a vision model can read comfortably.
/// Cuts are placed on visually empty (uniform) rows near the ideal position so text lines are not sliced.
/// </summary>
public static class LongImageSplitter
{
    /// <param name="maxChunkHeight">Upper bound for each chunk height.</param>
    /// <param name="searchWindow">How far above the ideal cut position to look for an empty row.</param>
    public static IReadOnlyList<PixelRect> Split(Bgra32Image image, int maxChunkHeight, int searchWindow = 160)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunkHeight, 16);
        var chunks = new List<PixelRect>();
        var y = 0;
        while (y < image.Height)
        {
            var remaining = image.Height - y;
            if (remaining <= maxChunkHeight)
            {
                chunks.Add(new PixelRect(0, y, image.Width, remaining));
                break;
            }

            var ideal = y + maxChunkHeight;
            var cut = ideal;
            var lowest = Math.Max(y + maxChunkHeight / 2, ideal - searchWindow);
            for (var row = ideal; row >= lowest; row--)
            {
                if (IsUniformRow(image, row)) { cut = row; break; }
            }
            chunks.Add(new PixelRect(0, y, image.Width, cut - y));
            y = cut;
        }
        return chunks;
    }

    /// <summary>The model sees images of roughly this aspect best; long images are cut to at most ~1.5× their width.</summary>
    public static int SuggestChunkHeight(int width) => Math.Clamp((int)(width * 1.5), 800, 3000);

    private static bool IsUniformRow(Bgra32Image image, int y)
    {
        var row = image.GetRowReadOnly(y);
        var b0 = row[0]; var g0 = row[1]; var r0 = row[2];
        for (var i = 4; i < row.Length; i += 4)
        {
            if (Math.Abs(row[i] - b0) > 6 || Math.Abs(row[i + 1] - g0) > 6 || Math.Abs(row[i + 2] - r0) > 6) return false;
        }
        return true;
    }
}
