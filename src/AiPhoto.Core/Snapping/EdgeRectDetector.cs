using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Snapping;

/// <summary>
/// Pure C# computer-vision detector that finds visual boxes (buttons, inputs, cards, images, table cells)
/// and text blocks/lines around a cursor point directly from the frozen <see cref="Bgra32Image"/> screenshot.
/// Runs in ~1 ms by bounding analysis to a local window around the cursor.
/// </summary>
public static class EdgeRectDetector
{
    private const int MaxSearchRadiusX = 640;
    private const int MaxSearchRadiusY = 420;
    private const int MinBoxWidth = 14;
    private const int MinBoxHeight = 12;

    /// <summary>
    /// Detects snappable visual boxes and text lines/blocks containing <c>(x, y)</c> within <paramref name="clipBounds"/>.
    /// </summary>
    public static IReadOnlyList<SnapCandidate> DetectAroundPoint(Bgra32Image image, int x, int y, PixelRect clipBounds)
    {
        var bounds = clipBounds.IsEmpty ? image.Bounds : clipBounds.Intersect(image.Bounds);
        if (!bounds.Contains(x, y) || bounds.Width < MinBoxWidth || bounds.Height < MinBoxHeight) return [];

        var roi = PixelRect.FromLTRB(
            Math.Max(bounds.X, x - MaxSearchRadiusX),
            Math.Max(bounds.Y, y - MaxSearchRadiusY),
            Math.Min(bounds.Right, x + MaxSearchRadiusX),
            Math.Min(bounds.Bottom, y + MaxSearchRadiusY));

        var results = new List<SnapCandidate>(6);

        DetectTextRegions(image, x, y, roi, results);
        DetectVisualBoxes(image, x, y, roi, results);

        return results;
    }

    // ------------------------------------------------------------------ text line & paragraph detection

    private static void DetectTextRegions(Bgra32Image image, int x, int y, PixelRect roi, List<SnapCandidate> results)
    {
        // Check a vertical band around y for text-stroke characteristics (frequent horizontal contrast transitions).
        const int textProbeRadiusY = 160;
        const int textProbeRadiusX = 480;
        var rx0 = Math.Max(roi.X, x - textProbeRadiusX);
        var rx1 = Math.Min(roi.Right, x + textProbeRadiusX);
        var ry0 = Math.Max(roi.Y, y - textProbeRadiusY);
        var ry1 = Math.Min(roi.Bottom, y + textProbeRadiusY);
        var w = rx1 - rx0;
        var h = ry1 - ry0;
        if (w < 20 || h < 10) return;

        // First check if there are text-like horizontal edges near (x, y) within +-10px vertically and +-24px horizontally.
        var nearTransitions = 0;
        var ny0 = Math.Max(ry0, y - 10);
        var ny1 = Math.Min(ry1 - 1, y + 10);
        var nx0 = Math.Max(rx0 + 1, x - 24);
        var nx1 = Math.Min(rx1 - 1, x + 24);
        for (var py = ny0; py <= ny1; py++)
        {
            var rowOffset = py * image.Stride;
            for (var px = nx0; px < nx1; px++)
            {
                if (ColorDistSq(image.Pixels, rowOffset + px * 4, rowOffset + (px - 1) * 4) >= 900)
                {
                    nearTransitions++;
                }
            }
        }
        // A solid border has at most ~20 transitions in a 20x48 patch; actual glyphs produce many more.
        if (nearTransitions < 24) return;

        // Find the vertical span [lineTop, lineBottom] of the text line containing y.
        // We measure transitions in a horizontal window [x - 80, x + 80] per row.
        var bandX0 = Math.Max(rx0 + 1, x - 90);
        var bandX1 = Math.Min(rx1 - 1, x + 90);
        Span<bool> rowActive = stackalloc bool[h];
        for (var ry = 0; ry < h; ry++)
        {
            var py = ry0 + ry;
            var rowOffset = py * image.Stride;
            var count = 0;
            for (var px = bandX0; px < bandX1; px++)
            {
                if (ColorDistSq(image.Pixels, rowOffset + px * 4, rowOffset + (px - 1) * 4) >= 900) count++;
            }
            // Require at least 4 stroke edges (2 vertical strokes) so a single vertical line does not look like text.
            rowActive[ry] = count >= 4;
        }

        var cy = y - ry0;
        // Bridge up to 3px internal gaps (e.g. between baseline and dot of 'i' or Chinese radicals).
        var lineTop = cy;
        var lineBottom = cy;
        while (lineTop > 0 && HasActiveWithin(rowActive, lineTop - 1, -1, maxGap: 3)) lineTop--;
        while (lineBottom < h - 1 && HasActiveWithin(rowActive, lineBottom + 1, +1, maxGap: 3)) lineBottom++;

        var lineHeight = lineBottom - lineTop + 1;
        if (lineHeight < 7 || lineHeight > 72) return;

        // Measure horizontal span [lineLeft, lineRight] of this text line by checking column activity across [lineTop..lineBottom].
        if (!FindLineHorizontalSpan(image, rx0, rx1, ry0 + lineTop, ry0 + lineBottom, x, lineHeight, out var lineLeft, out var lineRight))
            return;

        var lineWidth = lineRight - lineLeft + 1;
        if (lineWidth < 16 || lineWidth < lineHeight) return;

        var lineRect = PixelRect.FromLTRB(
            Math.Max(roi.X, lineLeft - 3),
            Math.Max(roi.Y, ry0 + lineTop - 2),
            Math.Min(roi.Right, lineRight + 4),
            Math.Min(roi.Bottom, ry0 + lineBottom + 3));

        results.Add(new SnapCandidate(lineRect, SnapKind.TextBlock, SnapSource.ImageEdge, Depth: 3, Label: "文本行"));

        // Expand vertically to adjacent text lines that belong to the same paragraph/block.
        var maxLineGap = Math.Clamp((int)(lineHeight * 0.85), 4, 24);
        var blockTop = lineTop;
        var blockBottom = lineBottom;
        var blockLeft = lineLeft;
        var blockRight = lineRight;
        var lineCount = 1;

        // Scan upwards for adjacent lines
        var probeY = blockTop - 1;
        while (probeY > 0)
        {
            var gap = 0;
            while (probeY >= 0 && !rowActive[probeY] && gap <= maxLineGap) { probeY--; gap++; }
            if (probeY < 0 || gap > maxLineGap || !rowActive[probeY]) break;
            var prevBottom = probeY;
            var prevTop = prevBottom;
            while (prevTop > 0 && HasActiveWithin(rowActive, prevTop - 1, -1, maxGap: 3)) prevTop--;
            var prevH = prevBottom - prevTop + 1;
            if (prevH < 6 || prevH > lineHeight * 2) break;
            if (FindLineHorizontalSpan(image, rx0, rx1, ry0 + prevTop, ry0 + prevBottom, Math.Clamp(x, blockLeft, blockRight), prevH, out var l, out var r))
            {
                blockTop = prevTop;
                blockLeft = Math.Min(blockLeft, l);
                blockRight = Math.Max(blockRight, r);
                lineCount++;
                probeY = prevTop - 1;
            }
            else break;
        }

        // Scan downwards for adjacent lines
        probeY = blockBottom + 1;
        while (probeY < h)
        {
            var gap = 0;
            while (probeY < h && !rowActive[probeY] && gap <= maxLineGap) { probeY++; gap++; }
            if (probeY >= h || gap > maxLineGap || !rowActive[probeY]) break;
            var nextTop = probeY;
            var nextBottom = nextTop;
            while (nextBottom < h - 1 && HasActiveWithin(rowActive, nextBottom + 1, +1, maxGap: 3)) nextBottom++;
            var nextH = nextBottom - nextTop + 1;
            if (nextH < 6 || nextH > lineHeight * 2) break;
            if (FindLineHorizontalSpan(image, rx0, rx1, ry0 + nextTop, ry0 + nextBottom, Math.Clamp(x, blockLeft, blockRight), nextH, out var l, out var r))
            {
                blockBottom = nextBottom;
                blockLeft = Math.Min(blockLeft, l);
                blockRight = Math.Max(blockRight, r);
                lineCount++;
                probeY = nextBottom + 1;
            }
            else break;
        }

        if (lineCount >= 2)
        {
            var blockRect = PixelRect.FromLTRB(
                Math.Max(roi.X, blockLeft - 4),
                Math.Max(roi.Y, ry0 + blockTop - 3),
                Math.Min(roi.Right, blockRight + 5),
                Math.Min(roi.Bottom, ry0 + blockBottom + 4));
            if (!SnapIndex.AreNearlyEqual(blockRect, lineRect))
            {
                results.Add(new SnapCandidate(blockRect, SnapKind.TextBlock, SnapSource.ImageEdge, Depth: 2, Label: "文本块"));
            }
        }
    }

    private static bool HasActiveWithin(ReadOnlySpan<bool> flags, int start, int step, int maxGap)
    {
        for (var i = 0; i <= maxGap; i++)
        {
            var idx = start + i * step;
            if (idx < 0 || idx >= flags.Length) return false;
            if (flags[idx]) return true;
        }
        return false;
    }

    private static bool FindLineHorizontalSpan(Bgra32Image image, int rx0, int rx1, int yTop, int yBottom, int seedX, int lineHeight, out int left, out int right)
    {
        var w = rx1 - rx0;
        var colActive = new bool[w];
        for (var py = yTop; py <= yBottom; py++)
        {
            var rowOffset = py * image.Stride;
            for (var px = rx0 + 1; px < rx1 - 1; px++)
            {
                if (ColorDistSq(image.Pixels, rowOffset + px * 4, rowOffset + (px - 1) * 4) >= 900)
                {
                    colActive[px - rx0] = true;
                }
            }
        }

        var cx = Math.Clamp(seedX - rx0, 0, w - 1);
        var maxWordGap = Math.Clamp((int)(lineHeight * 1.4), 10, 42);
        if (!colActive[cx] && !HasActiveWithin(colActive, cx, -1, maxWordGap) && !HasActiveWithin(colActive, cx, +1, maxWordGap))
        {
            left = right = 0;
            return false;
        }

        var l = cx;
        var r = cx;
        while (l > 0 && HasActiveWithin(colActive, l - 1, -1, maxWordGap)) l--;
        while (r < w - 1 && HasActiveWithin(colActive, r + 1, +1, maxWordGap)) r++;

        left = rx0 + l;
        right = rx0 + r;
        return right > left;
    }

    // ------------------------------------------------------------------ visual box / container detection

    private static void DetectVisualBoxes(Bgra32Image image, int x, int y, PixelRect roi, List<SnapCandidate> results)
    {
        // Collect candidate horizontal edges (top/bottom) and vertical edges (left/right) around (x, y)
        // using both color departure from the seed fill and sharp gradient steps along multiple probe lines.
        var seed = SampleMedianColor(image, x, y, roi);

        var leftEdges = FindCandidateEdgesHorizontal(image, x, y, roi, direction: -1, seed);
        var rightEdges = FindCandidateEdgesHorizontal(image, x, y, roi, direction: +1, seed);
        var topEdges = FindCandidateEdgesVertical(image, x, y, roi, direction: -1, seed);
        var bottomEdges = FindCandidateEdgesVertical(image, x, y, roi, direction: +1, seed);

        if (leftEdges.Count == 0 || rightEdges.Count == 0 || topEdges.Count == 0 || bottomEdges.Count == 0)
            return;

        var candidates = new List<(PixelRect Rect, double Score)>();

        foreach (var l in leftEdges)
        foreach (var r in rightEdges)
        {
            var width = r - l;
            if (width < MinBoxWidth) continue;
            foreach (var t in topEdges)
            foreach (var b in bottomEdges)
            {
                var height = b - t;
                if (height < MinBoxHeight) continue;
                var rect = new PixelRect(l, t, width, height);
                if (!rect.Contains(x, y)) continue;
                if (SnapIndex.AreNearlyEqual(rect, roi)) continue;

                var score = ScoreRectangleEdges(image, rect);
                if (score >= 0.62)
                {
                    candidates.Add((rect, score));
                }
            }
        }

        if (candidates.Count == 0) return;

        // Sort by area ascending, keeping well-scored distinct boxes (up to 3 nested scales).
        candidates.Sort((a, b) =>
        {
            var areaA = (long)a.Rect.Width * a.Rect.Height;
            var areaB = (long)b.Rect.Width * b.Rect.Height;
            return areaA.CompareTo(areaB);
        });

        var kept = new List<PixelRect>(3);
        foreach (var (rect, _) in candidates)
        {
            if (kept.Any(k => SnapIndex.AreNearlyEqual(k, rect, tolerance: 6))) continue;
            kept.Add(rect);
            var depth = kept.Count == 1 ? 2 : 1;
            results.Add(new SnapCandidate(rect, SnapKind.VisualBox, SnapSource.ImageEdge, Depth: depth, Label: "区域"));
            if (kept.Count >= 3) break;
        }
    }

    private static List<int> FindCandidateEdgesHorizontal(Bgra32Image image, int x, int y, PixelRect roi, int direction, Bgra32 seed)
    {
        ReadOnlySpan<int> offsets = [0, -12, 12, -32, 32];
        var votes = new List<int>(12);
        var limit = direction < 0 ? roi.X + 1 : roi.Right - 2;

        foreach (var dy in offsets)
        {
            var py = y + dy;
            if (py <= roi.Y || py >= roi.Bottom - 1) continue;
            var rowOffset = py * image.Stride;
            var prevGrad = 0;
            var foundSeedDeparture = false;

            for (var px = x + direction; direction < 0 ? px >= limit : px <= limit; px += direction)
            {
                var curIdx = rowOffset + px * 4;
                var prevIdx = rowOffset + (px - direction) * 4;
                var grad = ColorDistSq(image.Pixels, curIdx, prevIdx);

                // First strong departure from the interior seed color
                if (!foundSeedDeparture && dy == 0)
                {
                    var curColor = image.GetPixel(px, py);
                    if (curColor.DistanceSquared(seed) >= 324 && grad >= 144)
                    {
                        foundSeedDeparture = true;
                        AddEdgeVote(votes, direction < 0 ? px + 1 : px);
                    }
                }

                // Local gradient peak (border line or container edge)
                if (grad >= 400 && grad >= prevGrad)
                {
                    AddEdgeVote(votes, direction < 0 ? px + 1 : px);
                    if (votes.Count >= 6) break;
                }
                prevGrad = grad;
            }
        }
        return votes;
    }

    private static List<int> FindCandidateEdgesVertical(Bgra32Image image, int x, int y, PixelRect roi, int direction, Bgra32 seed)
    {
        ReadOnlySpan<int> offsets = [0, -16, 16, -48, 48];
        var votes = new List<int>(12);
        var limit = direction < 0 ? roi.Y + 1 : roi.Bottom - 2;

        foreach (var dx in offsets)
        {
            var px = x + dx;
            if (px <= roi.X || px >= roi.Right - 1) continue;
            var prevGrad = 0;
            var foundSeedDeparture = false;

            for (var py = y + direction; direction < 0 ? py >= limit : py <= limit; py += direction)
            {
                var curIdx = py * image.Stride + px * 4;
                var prevIdx = (py - direction) * image.Stride + px * 4;
                var grad = ColorDistSq(image.Pixels, curIdx, prevIdx);

                if (!foundSeedDeparture && dx == 0)
                {
                    var curColor = image.GetPixel(px, py);
                    if (curColor.DistanceSquared(seed) >= 324 && grad >= 144)
                    {
                        foundSeedDeparture = true;
                        AddEdgeVote(votes, direction < 0 ? py + 1 : py);
                    }
                }

                if (grad >= 400 && grad >= prevGrad)
                {
                    AddEdgeVote(votes, direction < 0 ? py + 1 : py);
                    if (votes.Count >= 6) break;
                }
                prevGrad = grad;
            }
        }
        return votes;
    }

    private static void AddEdgeVote(List<int> list, int coord)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (Math.Abs(list[i] - coord) <= 2) return;
        }
        if (list.Count < 6) list.Add(coord);
    }

    /// <summary>
    /// Evaluates how well the 4 edges of <paramref name="r"/> align with actual visual boundaries in <paramref name="image"/>.
    /// Returns a score in <c>0..1</c>.
    /// </summary>
    private static double ScoreRectangleEdges(Bgra32Image image, PixelRect r)
    {
        if (r.X <= 1 || r.Y <= 1 || r.Right >= image.Width - 1 || r.Bottom >= image.Height - 1)
            return 0;

        var topScore = MeasureHorizontalEdge(image, r.X, r.Right, r.Y);
        var bottomScore = MeasureHorizontalEdge(image, r.X, r.Right, r.Bottom);
        var leftScore = MeasureVerticalEdge(image, r.Y, r.Bottom, r.X);
        var rightScore = MeasureVerticalEdge(image, r.Y, r.Bottom, r.Right);

        // Require all 4 sides to have valid corner-to-corner edge continuity.
        if (topScore < 0.6 || bottomScore < 0.6 || leftScore < 0.6 || rightScore < 0.6) return 0;

        return (topScore + bottomScore + leftScore + rightScore) * 0.25;
    }

    private static double MeasureHorizontalEdge(Bgra32Image image, int x0, int x1, int y)
    {
        var span = x1 - x0;
        if (span <= 0) return 0;
        var samples = Math.Clamp(span / 4, 8, 48);
        var quarter = Math.Max(2, samples / 4);
        int hits = 0, startHits = 0, endHits = 0;
        var yIn = Math.Clamp(y, 1, image.Height - 2);
        for (var i = 1; i <= samples; i++)
        {
            var px = x0 + (int)((long)i * span / (samples + 1));
            var g1 = ColorDistSq(image.Pixels, yIn * image.Stride + px * 4, (yIn - 1) * image.Stride + px * 4);
            var g2 = ColorDistSq(image.Pixels, (yIn + 1) * image.Stride + px * 4, (yIn - 1) * image.Stride + px * 4);
            if (g1 >= 196 || g2 >= 256)
            {
                hits++;
                if (i <= quarter) startHits++;
                if (i > samples - quarter) endHits++;
            }
        }
        // Reject "chimera" edges where one end of the segment extends past the actual box corner into empty background.
        if (startHits * 2 < quarter || endHits * 2 < quarter) return 0;
        return (double)hits / samples;
    }

    private static double MeasureVerticalEdge(Bgra32Image image, int y0, int y1, int x)
    {
        var span = y1 - y0;
        if (span <= 0) return 0;
        var samples = Math.Clamp(span / 4, 8, 48);
        var quarter = Math.Max(2, samples / 4);
        int hits = 0, startHits = 0, endHits = 0;
        var xIn = Math.Clamp(x, 1, image.Width - 2);
        for (var i = 1; i <= samples; i++)
        {
            var py = y0 + (int)((long)i * span / (samples + 1));
            var row = py * image.Stride;
            var g1 = ColorDistSq(image.Pixels, row + xIn * 4, row + (xIn - 1) * 4);
            var g2 = ColorDistSq(image.Pixels, row + (xIn + 1) * 4, row + (xIn - 1) * 4);
            if (g1 >= 196 || g2 >= 256)
            {
                hits++;
                if (i <= quarter) startHits++;
                if (i > samples - quarter) endHits++;
            }
        }
        if (startHits * 2 < quarter || endHits * 2 < quarter) return 0;
        return (double)hits / samples;
    }

    private static Bgra32 SampleMedianColor(Bgra32Image image, int x, int y, PixelRect roi)
    {
        var c0 = image.GetPixel(x, y);
        var c1 = image.GetPixel(Math.Max(roi.X, x - 2), y);
        var c2 = image.GetPixel(Math.Min(roi.Right - 1, x + 2), y);
        return c0.DistanceSquared(c1) <= c0.DistanceSquared(c2) ? c0 : c1;
    }

    private static int ColorDistSq(byte[] p, int i, int j)
    {
        var db = p[i] - p[j];
        var dg = p[i + 1] - p[j + 1];
        var dr = p[i + 2] - p[j + 2];
        return db * db + dg * dg + dr * dr;
    }
}
