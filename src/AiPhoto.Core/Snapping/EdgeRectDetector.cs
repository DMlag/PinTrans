using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Snapping;

/// <summary>
/// Pure C# computer-vision detector that finds visual boxes (buttons, inputs, cards, images, panels)
/// and full text lines / paragraphs around a cursor point directly from the frozen <see cref="Bgra32Image"/> screenshot.
/// </summary>
public static class EdgeRectDetector
{
    private const int MaxBoxSearchRadiusX = 720;
    private const int MaxBoxSearchRadiusY = 480;
    private const int MaxTextSearchRadiusX = 1600;
    private const int MaxTextSearchRadiusY = 360;

    // Minimum dimensions for visual boxes so single characters, tiny icons, or 2-char fragments are never snapped.
    private const int MinBoxWidth = 56;
    private const int MinBoxHeight = 22;
    private const int MinBoxArea = 1800;

    // Minimum width for a standalone snapped text line (~4+ Chinese characters or a real phrase/sentence).
    private const int MinTextLineWidth = 68;

    /// <summary>
    /// Detects snappable text lines/paragraphs and visual boxes containing <c>(x, y)</c> within <paramref name="clipBounds"/>.
    /// </summary>
    public static IReadOnlyList<SnapCandidate> DetectAroundPoint(Bgra32Image image, int x, int y, PixelRect clipBounds)
    {
        var bounds = clipBounds.IsEmpty ? image.Bounds : clipBounds.Intersect(image.Bounds);
        if (!bounds.Contains(x, y) || bounds.Width < MinBoxWidth || bounds.Height < MinBoxHeight) return [];

        var results = new List<SnapCandidate>(6);

        var textRoi = PixelRect.FromLTRB(
            Math.Max(bounds.X, x - MaxTextSearchRadiusX),
            Math.Max(bounds.Y, y - MaxTextSearchRadiusY),
            Math.Min(bounds.Right, x + MaxTextSearchRadiusX),
            Math.Min(bounds.Bottom, y + MaxTextSearchRadiusY));

        var detectedTextRect = DetectTextRegions(image, x, y, textRoi, results);

        var boxRoi = PixelRect.FromLTRB(
            Math.Max(bounds.X, x - MaxBoxSearchRadiusX),
            Math.Max(bounds.Y, y - MaxBoxSearchRadiusY),
            Math.Min(bounds.Right, x + MaxBoxSearchRadiusX),
            Math.Min(bounds.Bottom, y + MaxBoxSearchRadiusY));

        DetectVisualBoxes(image, x, y, boxRoi, detectedTextRect, results);

        return results;
    }

    // ------------------------------------------------------------------ text line & paragraph detection

    /// <summary>
    /// Detects a full text line and/or multi-line paragraph at <c>(x, y)</c>.
    /// Uses a two-pass vertical/horizontal stabilization so hovering at the start, middle, or end of a line
    /// yields the exact same full-line bounding box.
    /// </summary>
    private static PixelRect DetectTextRegions(Bgra32Image image, int x, int y, PixelRect roi, List<SnapCandidate> results)
    {
        var rx0 = roi.X;
        var rx1 = roi.Right;
        var ry0 = roi.Y;
        var ry1 = roi.Bottom;
        var w = rx1 - rx0;
        var h = ry1 - ry0;
        if (w < MinTextLineWidth || h < 12) return default;

        // Check if (x, y) is near glyph strokes (within +-10px vertically and +-28px horizontally).
        var nearTransitions = 0;
        var ny0 = Math.Max(ry0, y - 10);
        var ny1 = Math.Min(ry1 - 1, y + 10);
        var nx0 = Math.Max(rx0 + 2, x - 28);
        var nx1 = Math.Min(rx1 - 2, x + 28);
        for (var py = ny0; py <= ny1; py++)
        {
            var rowOffset = py * image.Stride;
            for (var px = nx0; px < nx1; px++)
            {
                if (IsHorizontalStrokeEdge(image.Pixels, rowOffset, px)) nearTransitions++;
            }
        }
        if (nearTransitions < 22) return default;

        // Pass 1: Find initial vertical bounds [lineTop, lineBottom] around (x, y) using a local 280px window.
        var initBandX0 = Math.Max(rx0 + 2, x - 140);
        var initBandX1 = Math.Min(rx1 - 2, x + 140);
        Span<bool> rowActive = stackalloc bool[h];
        ComputeRowActivity(image, ry0, h, initBandX0, initBandX1, rowActive);

        var cy = y - ry0;
        var lineTop = cy;
        var lineBottom = cy;
        while (lineTop > 0 && HasActiveWithin(rowActive, lineTop - 1, -1, maxGap: 3)) lineTop--;
        while (lineBottom < h - 1 && HasActiveWithin(rowActive, lineBottom + 1, +1, maxGap: 3)) lineBottom++;

        var lineHeight = lineBottom - lineTop + 1;
        if (lineHeight < 8 || lineHeight > 72) return default;

        // Pass 1 horizontal scan: use generous minimum word gap (at least 24px) even if the first character at the
        // start of the line was short (e.g. '一', '-', 'a'), so we never stop early before the end of the line.
        if (!FindLineHorizontalSpan(image, rx0, rx1, ry0 + lineTop, ry0 + lineBottom, x, Math.Max(16, lineHeight), out var lineLeft, out var lineRight))
            return default;

        // Pass 2 stabilization: Re-evaluate vertical row activity across the entire detected horizontal span [lineLeft..lineRight]
        // so tall characters anywhere in the line give the true lineTop/lineBottom, then re-scan horizontally.
        var spanBandX0 = Math.Max(rx0 + 2, lineLeft);
        var spanBandX1 = Math.Min(rx1 - 2, lineRight);
        if (spanBandX1 - spanBandX0 >= 24)
        {
            ComputeRowActivity(image, ry0, h, spanBandX0, spanBandX1, rowActive);
            while (lineTop > 0 && HasActiveWithin(rowActive, lineTop - 1, -1, maxGap: 3)) lineTop--;
            while (lineBottom < h - 1 && HasActiveWithin(rowActive, lineBottom + 1, +1, maxGap: 3)) lineBottom++;
            lineHeight = lineBottom - lineTop + 1;
            if (lineHeight is >= 8 and <= 72)
            {
                FindLineHorizontalSpan(image, rx0, rx1, ry0 + lineTop, ry0 + lineBottom, x, lineHeight, out lineLeft, out lineRight);
            }
        }

        var lineWidth = lineRight - lineLeft + 1;
        if (lineWidth < 16) return default;

        var lineRect = PixelRect.FromLTRB(
            Math.Max(roi.X, lineLeft - 4),
            Math.Max(roi.Y, ry0 + lineTop - 2),
            Math.Min(roi.Right, lineRight + 5),
            Math.Min(roi.Bottom, ry0 + lineBottom + 3));

        // Only emit the single-line candidate if it is a real phrase/line (not 1-3 isolated characters or a vertical strip).
        var isFullTextLine = lineRect.Width >= Math.Max(MinTextLineWidth, lineHeight * 3.5) &&
                             lineRect.Width >= lineRect.Height * 2.2;
        if (isFullTextLine)
        {
            results.Add(new SnapCandidate(lineRect, SnapKind.TextBlock, SnapSource.ImageEdge, Depth: 3, Label: "文本行"));
        }

        // Expand vertically to adjacent text lines that belong to the same paragraph/block.
        var maxLineGap = Math.Clamp((int)(lineHeight * 0.95), 5, 28);
        var blockTop = lineTop;
        var blockBottom = lineBottom;
        var blockLeft = lineLeft;
        var blockRight = lineRight;
        var lineCount = 1;

        // Recompute row activity over a wider horizontal window covering the line so paragraph lines align properly.
        var paraBandX0 = Math.Max(rx0 + 2, Math.Min(x - 160, lineLeft));
        var paraBandX1 = Math.Min(rx1 - 2, Math.Max(x + 160, lineRight));
        ComputeRowActivity(image, ry0, h, paraBandX0, paraBandX1, rowActive);

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
            var seedX = Math.Clamp(x, blockLeft, blockRight);
            if (FindLineHorizontalSpan(image, rx0, rx1, ry0 + prevTop, ry0 + prevBottom, seedX, Math.Max(lineHeight, prevH), out var l, out var r))
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
            var seedX = Math.Clamp(x, blockLeft, blockRight);
            if (FindLineHorizontalSpan(image, rx0, rx1, ry0 + nextTop, ry0 + nextBottom, seedX, Math.Max(lineHeight, nextH), out var l, out var r))
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
                Math.Max(roi.X, blockLeft - 5),
                Math.Max(roi.Y, ry0 + blockTop - 3),
                Math.Min(roi.Right, blockRight + 6),
                Math.Min(roi.Bottom, ry0 + blockBottom + 4));

            // Reject narrow vertical strips ("上下两个字"): a multi-line block must have real horizontal width.
            if (blockRect.Width >= Math.Max(MinTextLineWidth, lineHeight * 3.5) &&
                blockRect.Area >= 2400 &&
                (!isFullTextLine || !SnapIndex.AreNearlyEqual(blockRect, lineRect)))
            {
                results.Add(new SnapCandidate(blockRect, SnapKind.TextBlock, SnapSource.ImageEdge, Depth: 2, Label: "文本块"));
                return blockRect;
            }
        }

        return isFullTextLine ? lineRect : default;
    }

    private static void ComputeRowActivity(Bgra32Image image, int ry0, int h, int bandX0, int bandX1, Span<bool> rowActive)
    {
        for (var ry = 0; ry < h; ry++)
        {
            var py = ry0 + ry;
            var rowOffset = py * image.Stride;
            var count = 0;
            for (var px = bandX0; px < bandX1; px++)
            {
                if (IsHorizontalStrokeEdge(image.Pixels, rowOffset, px)) count++;
            }
            rowActive[ry] = count >= 4;
        }
    }

    private static bool IsHorizontalStrokeEdge(byte[] pixels, int rowOffset, int px)
    {
        var cur = rowOffset + px * 4;
        // Check 1px and 2px horizontal deltas so ClearType/anti-aliased font strokes are reliably detected.
        return ColorDistSq(pixels, cur, cur - 4) >= 784 ||
               ColorDistSq(pixels, cur + 4, cur - 4) >= 1024;
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

    private static bool FindLineHorizontalSpan(
        Bgra32Image image,
        int rx0,
        int rx1,
        int yTop,
        int yBottom,
        int seedX,
        int lineHeight,
        out int left,
        out int right)
    {
        var w = rx1 - rx0;
        var colActive = new bool[w];
        var bandHeight = Math.Max(1, yBottom - yTop + 1);

        for (var px = rx0 + 2; px < rx1 - 2; px++)
        {
            var strokeRows = 0;
            var verticalEdgeRows = 0;
            for (var py = yTop; py <= yBottom; py++)
            {
                var rowOffset = py * image.Stride;
                if (IsHorizontalStrokeEdge(image.Pixels, rowOffset, px))
                {
                    strokeRows++;
                    // Check if this column is a continuous vertical border line rather than a character glyph:
                    // A straight vertical border has nearly zero vertical color change between py and py-1.
                    if (py > yTop && ColorDistSq(image.Pixels, rowOffset + px * 4, (py - 1) * image.Stride + px * 4) <= 36)
                    {
                        verticalEdgeRows++;
                    }
                }
            }
            // If the column is a solid vertical container border spanning the full line height, do not treat it as text.
            if (bandHeight >= 10 && verticalEdgeRows >= bandHeight - 2) continue;
            if (strokeRows >= 1) colActive[px - rx0] = true;
        }

        // Bridge intra-line spaces, CJK full-width punctuation ('，', '。', '：'), and English word spaces.
        var maxWordGap = Math.Clamp((int)(lineHeight * 2.2), 22, 56);
        var cx = Math.Clamp(seedX - rx0, 0, w - 1);
        if (!colActive[cx])
        {
            // Snap cx to the nearest active glyph column within maxWordGap.
            var found = -1;
            for (var d = 1; d <= maxWordGap; d++)
            {
                if (cx - d >= 0 && colActive[cx - d]) { found = cx - d; break; }
                if (cx + d < w && colActive[cx + d]) { found = cx + d; break; }
            }
            if (found < 0)
            {
                left = right = 0;
                return false;
            }
            cx = found;
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

    private static void DetectVisualBoxes(
        Bgra32Image image,
        int x,
        int y,
        PixelRect roi,
        PixelRect detectedTextRect,
        List<SnapCandidate> results)
    {
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
                if (height < MinBoxHeight || width * height < MinBoxArea) continue;
                var rect = new PixelRect(l, t, width, height);
                if (!rect.Contains(x, y)) continue;
                if (SnapIndex.AreNearlyEqual(rect, roi)) continue;

                // If (x, y) is on a detected text line/block, any visual container box must enclose the text
                // (prevents glyph strokes inside a paragraph from being mistaken for small visual boxes).
                if (!detectedTextRect.IsEmpty &&
                    (rect.X > detectedTextRect.X + 4 || rect.Right < detectedTextRect.Right - 4 ||
                     rect.Y > detectedTextRect.Y + 4 || rect.Bottom < detectedTextRect.Bottom - 4))
                {
                    continue;
                }

                var score = ScoreRectangleEdges(image, rect);
                if (score >= 0.65)
                {
                    candidates.Add((rect, score));
                }
            }
        }

        if (candidates.Count == 0) return;

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
            if (!detectedTextRect.IsEmpty && SnapIndex.AreNearlyEqual(rect, detectedTextRect, tolerance: 6)) continue;
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

                if (!foundSeedDeparture && dy == 0)
                {
                    var curColor = image.GetPixel(px, py);
                    if (curColor.DistanceSquared(seed) >= 324 && grad >= 144)
                    {
                        foundSeedDeparture = true;
                        AddEdgeVote(votes, direction < 0 ? px + 1 : px);
                    }
                }

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
    /// Also checks tangential continuity so lines cutting through text glyphs are rejected.
    /// </summary>
    private static double ScoreRectangleEdges(Bgra32Image image, PixelRect r)
    {
        if (r.X <= 1 || r.Y <= 1 || r.Right >= image.Width - 1 || r.Bottom >= image.Height - 1)
            return 0;

        var topScore = MeasureHorizontalEdge(image, r.X, r.Right, r.Y);
        var bottomScore = MeasureHorizontalEdge(image, r.X, r.Right, r.Bottom);
        var leftScore = MeasureVerticalEdge(image, r.Y, r.Bottom, r.X);
        var rightScore = MeasureVerticalEdge(image, r.Y, r.Bottom, r.Right);

        if (topScore < 0.65 || bottomScore < 0.65 || leftScore < 0.65 || rightScore < 0.65) return 0;

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
        var rowAbove = (yIn - 1) * image.Stride;
        var rowAt = yIn * image.Stride;
        var rowBelow = (yIn + 1) * image.Stride;

        for (var i = 1; i <= samples; i++)
        {
            var px = Math.Clamp(x0 + (int)((long)i * span / (samples + 1)), 1, image.Width - 2);
            var g1 = ColorDistSq(image.Pixels, rowAt + px * 4, rowAbove + px * 4);
            var g2 = ColorDistSq(image.Pixels, rowBelow + px * 4, rowAbove + px * 4);
            // Tangential stability: along a real horizontal edge, either the row above or the row below is locally smooth.
            var tanAbove = ColorDistSq(image.Pixels, rowAbove + (px + 1) * 4, rowAbove + (px - 1) * 4);
            var tanAt = ColorDistSq(image.Pixels, rowAt + (px + 1) * 4, rowAt + (px - 1) * 4);
            var isSmoothAlongEdge = tanAbove <= 400 || tanAt <= 400;

            if ((g1 >= 196 || g2 >= 256) && isSmoothAlongEdge)
            {
                hits++;
                if (i <= quarter) startHits++;
                if (i > samples - quarter) endHits++;
            }
        }
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
            var py = Math.Clamp(y0 + (int)((long)i * span / (samples + 1)), 1, image.Height - 2);
            var row = py * image.Stride;
            var rowPrev = (py - 1) * image.Stride;
            var rowNext = (py + 1) * image.Stride;
            var g1 = ColorDistSq(image.Pixels, row + xIn * 4, row + (xIn - 1) * 4);
            var g2 = ColorDistSq(image.Pixels, row + (xIn + 1) * 4, row + (xIn - 1) * 4);
            var tanLeft = ColorDistSq(image.Pixels, rowNext + (xIn - 1) * 4, rowPrev + (xIn - 1) * 4);
            var tanAt = ColorDistSq(image.Pixels, rowNext + xIn * 4, rowPrev + xIn * 4);
            var isSmoothAlongEdge = tanLeft <= 400 || tanAt <= 400;

            if ((g1 >= 196 || g2 >= 256) && isSmoothAlongEdge)
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
