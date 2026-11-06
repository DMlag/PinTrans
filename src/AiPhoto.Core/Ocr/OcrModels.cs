using System.Runtime.CompilerServices;
using System.Text;
using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Ocr;

public readonly record struct OcrWordBox(string Text, PixelRect Bounds);

public sealed record OcrLineResult(string Text, PixelRect Bounds, IReadOnlyList<OcrWordBox> Words);

public sealed record QrCodeResult(string Text, string Format, PixelRect Bounds)
{
    public bool IsHttpUrl => QrCodeScanner.TryGetHttpUri(Text, out _);
}

public sealed record OcrRecognitionResult(
    string FullText,
    IReadOnlyList<OcrLineResult> Lines,
    IReadOnlyList<QrCodeResult> Barcodes)
{
    public bool HasAnyContent => !string.IsNullOrWhiteSpace(FullText) || Lines.Count > 0 || Barcodes.Count > 0;
}

/// <summary>
/// Pre- and post-processing utilities for local offline OCR:
/// <list type="bullet">
/// <item>CJK spacing normalization (<see cref="JoinWords"/>)</item>
/// <item>Dark-mode auto-inversion, ClearType subpixel de-fringing, contrast normalization, and Catmull-Rom bicubic sharp upscaling (<see cref="PrepareImageForOcr"/>)</item>
/// </list>
/// </summary>
public static class OcrTextFormatter
{
    /// <summary>
    /// Joins words recognized on a single line. Windows.Media.Ocr splits every CJK character into a separate
    /// word and joins them with spaces ("你 好 世 界"); this method strips artificial spaces between CJK characters
    /// while preserving spaces between Latin/ASCII words or across wide column gaps.
    /// </summary>
    public static string JoinWords(IReadOnlyList<OcrWordBox> words)
    {
        if (words.Count == 0) return "";
        if (words.Count == 1) return words[0].Text.Trim();

        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var token = words[i].Text.Trim();
            if (token.Length == 0) continue;

            if (sb.Length > 0 && i > 0)
            {
                var prev = words[i - 1];
                var cur = words[i];
                var prevLast = sb[^1];
                var curFirst = token[0];

                var avgHeight = Math.Max(8, (prev.Bounds.Height + cur.Bounds.Height) / 2);
                var horizontalGap = cur.Bounds.X - prev.Bounds.Right;
                var isWideColumnGap = horizontalGap > avgHeight * 1.4;

                if (isWideColumnGap)
                {
                    sb.Append("  ");
                }
                else if (!IsCjkOrFullwidthPunctuation(prevLast) && !IsCjkOrFullwidthPunctuation(curFirst))
                {
                    sb.Append(' ');
                }
            }

            sb.Append(token);
        }

        return sb.ToString();
    }

    public static bool IsCjkOrFullwidthPunctuation(char c) =>
        c is (>= '\u2E80' and <= '\u9FFF')       // CJK Radicals, Ideographs, Kana, Symbols
          or (>= '\uAC00' and <= '\uD7AF')       // Hangul Syllables
          or (>= '\uF900' and <= '\uFAFF')       // CJK Compatibility Ideographs
          or (>= '\uFE30' and <= '\uFE4F')       // CJK Compatibility Forms
          or (>= '\uFF00' and <= '\uFFEF');      // Halfwidth and Fullwidth Forms

    /// <summary>
    /// Prepares an image crop for <c>Windows.Media.Ocr</c>:
    /// <list type="number">
    /// <item>Detects dark-mode backgrounds (white/light text on dark background) and auto-inverts to dark text on white background.</item>
    /// <item>Eliminates ClearType RGB subpixel fringes and stretches low-contrast dynamic range.</item>
    /// <item>Upscales small crops 2x using Catmull-Rom bicubic sharp interpolation and adds a clean background border.</item>
    /// </list>
    /// </summary>
    public static Bgra32Image PrepareImageForOcr(Bgra32Image source, out int scale, out int padding)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0)
        {
            scale = 1;
            padding = 0;
            return source;
        }

        scale = (source.Height < 64 || source.Width < 160) ? 2 : 1;
        padding = 12 * scale;

        var normalized = NormalizeForOcr(source, out var bgLum);
        var borderColor = new Bgra32(bgLum, bgLum, bgLum, 255);

        var scaledW = normalized.Width * scale;
        var scaledH = normalized.Height * scale;
        var outW = scaledW + padding * 2;
        var outH = scaledH + padding * 2;

        var result = new Bgra32Image(outW, outH);
        result.Pixels.AsSpan().Fill(bgLum);
        for (var i = 3; i < result.Pixels.Length; i += 4)
        {
            result.Pixels[i] = 255;
        }

        if (scale == 1)
        {
            result.Blit(normalized, padding, padding);
            return result;
        }

        // Catmull-Rom Bicubic 2x sharp upscale into [padding .. padding + scaledW, padding .. padding + scaledH]
        for (var dy = 0; dy < scaledH; dy++)
        {
            var fy = (dy + 0.5) / scale - 0.5;
            var iy = (int)Math.Floor(fy);
            var ty = fy - iy;

            var wy0 = CubicWeight(-1.0 - ty);
            var wy1 = CubicWeight(-ty);
            var wy2 = CubicWeight(1.0 - ty);
            var wy3 = CubicWeight(2.0 - ty);

            var y0 = Math.Clamp(iy - 1, 0, normalized.Height - 1);
            var y1 = Math.Clamp(iy, 0, normalized.Height - 1);
            var y2 = Math.Clamp(iy + 1, 0, normalized.Height - 1);
            var y3 = Math.Clamp(iy + 2, 0, normalized.Height - 1);

            for (var dx = 0; dx < scaledW; dx++)
            {
                var fx = (dx + 0.5) / scale - 0.5;
                var ix = (int)Math.Floor(fx);
                var tx = fx - ix;

                var wx0 = CubicWeight(-1.0 - tx);
                var wx1 = CubicWeight(-tx);
                var wx2 = CubicWeight(1.0 - tx);
                var wx3 = CubicWeight(2.0 - tx);

                var x0 = Math.Clamp(ix - 1, 0, normalized.Width - 1);
                var x1 = Math.Clamp(ix, 0, normalized.Width - 1);
                var x2 = Math.Clamp(ix + 1, 0, normalized.Width - 1);
                var x3 = Math.Clamp(ix + 2, 0, normalized.Width - 1);

                var r0 = SampleRowCubic(normalized, y0, x0, x1, x2, x3, wx0, wx1, wx2, wx3);
                var r1 = SampleRowCubic(normalized, y1, x0, x1, x2, x3, wx0, wx1, wx2, wx3);
                var r2 = SampleRowCubic(normalized, y2, x0, x1, x2, x3, wx0, wx1, wx2, wx3);
                var r3 = SampleRowCubic(normalized, y3, x0, x1, x2, x3, wx0, wx1, wx2, wx3);

                var lum = (byte)Math.Clamp((int)Math.Round(r0 * wy0 + r1 * wy1 + r2 * wy2 + r3 * wy3), 0, 255);
                result.SetPixel(padding + dx, padding + dy, new Bgra32(lum, lum, lum, 255));
            }
        }

        return result;
    }

    /// <summary>
    /// Converts to de-fringed grayscale, auto-inverts dark backgrounds (so text is always dark on light background),
    /// and stretches low-contrast crops.
    /// </summary>
    internal static Bgra32Image NormalizeForOcr(Bgra32Image source, out byte backgroundLuminance)
    {
        var w = source.Width;
        var h = source.Height;
        var lumBuffer = new byte[w * h];
        Span<int> hist = stackalloc int[256];

        long borderSum = 0;
        var borderCount = 0;

        for (var y = 0; y < h; y++)
        {
            var rowOffset = y * source.Stride;
            var lRow = y * w;
            var isBorderY = y == 0 || y == h - 1;
            for (var x = 0; x < w; x++)
            {
                var idx = rowOffset + x * 4;
                var b = source.Pixels[idx];
                var g = source.Pixels[idx + 1];
                var r = source.Pixels[idx + 2];
                var lum = (byte)((77 * r + 150 * g + 29 * b + 128) >> 8);
                lumBuffer[lRow + x] = lum;
                hist[lum]++;

                if (isBorderY || x == 0 || x == w - 1)
                {
                    borderSum += lum;
                    borderCount++;
                }
            }
        }

        // Determine dominant background luminance from median and perimeter average.
        var total = w * h;
        var medianLum = FindPercentile(hist, total, 0.50);
        var borderAvg = borderCount > 0 ? (int)(borderSum / borderCount) : medianLum;
        var estimatedBg = (medianLum + borderAvg) / 2;

        // If the background is dark (< 112), invert so dark-mode screenshots become dark text on light background.
        var invert = estimatedBg < 112;
        if (invert)
        {
            hist.Clear();
            for (var i = 0; i < lumBuffer.Length; i++)
            {
                var inv = (byte)(255 - lumBuffer[i]);
                lumBuffer[i] = inv;
                hist[inv]++;
            }
        }

        var low = FindPercentile(hist, total, 0.02);
        var high = FindPercentile(hist, total, 0.90);
        var range = high - low;

        var output = new Bgra32Image(w, h);
        // Stretch contrast if there is meaningful foreground/background separation (range >= 18) but less than full dynamic range.
        var stretch = range >= 18 && (high < 242 || low > 28);
        for (var y = 0; y < h; y++)
        {
            var lRow = y * w;
            var outRow = y * output.Stride;
            for (var x = 0; x < w; x++)
            {
                var v = (int)lumBuffer[lRow + x];
                if (stretch)
                {
                    v = Math.Clamp((v - low) * 255 / range, 0, 255);
                }
                var bv = (byte)v;
                var o = outRow + x * 4;
                output.Pixels[o] = bv;
                output.Pixels[o + 1] = bv;
                output.Pixels[o + 2] = bv;
                output.Pixels[o + 3] = 255;
            }
        }

        backgroundLuminance = stretch ? (byte)255 : (byte)Math.Clamp(invert ? 255 - borderAvg : borderAvg, 200, 255);
        return output;
    }

    private static int FindPercentile(ReadOnlySpan<int> hist, int total, double percentile)
    {
        var target = (int)Math.Round(total * percentile);
        var acc = 0;
        for (var i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target) return i;
        }
        return 255;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CubicWeight(double x)
    {
        // Catmull-Rom spline kernel (a = -0.5) for sharp bicubic interpolation.
        const double a = -0.5;
        x = Math.Abs(x);
        if (x <= 1.0) return (a + 2.0) * x * x * x - (a + 3.0) * x * x + 1.0;
        if (x < 2.0) return a * x * x * x - 5.0 * a * x * x + 8.0 * a * x - 4.0 * a;
        return 0.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SampleRowCubic(
        Bgra32Image img,
        int y,
        int x0, int x1, int x2, int x3,
        double w0, double w1, double w2, double w3)
    {
        var row = y * img.Stride;
        return img.Pixels[row + x0 * 4] * w0 +
               img.Pixels[row + x1 * 4] * w1 +
               img.Pixels[row + x2 * 4] * w2 +
               img.Pixels[row + x3 * 4] * w3;
    }

    public static PixelRect MapRectToSource(PixelRect preparedRect, int scale, int padding, PixelRect sourceBounds)
    {
        scale = Math.Max(1, scale);
        var l = (int)Math.Floor((double)(preparedRect.X - padding) / scale);
        var t = (int)Math.Floor((double)(preparedRect.Y - padding) / scale);
        var r = (int)Math.Ceiling((double)(preparedRect.Right - padding) / scale);
        var b = (int)Math.Ceiling((double)(preparedRect.Bottom - padding) / scale);
        return PixelRect.FromLTRB(l, t, r, b).Intersect(sourceBounds);
    }
}

/// <summary>
/// Recursive XY-Cut layout analyzer that sorts recognized OCR lines into natural human reading order
/// (handling multi-column layouts, sidebars, and same-row table cells) with a strictly transitive order.
/// </summary>
public static class OcrLayoutAnalyzer
{
    public static IReadOnlyList<OcrLineResult> SortLinesInReadingOrder(IReadOnlyList<OcrLineResult> lines)
    {
        if (lines.Count <= 1) return lines;
        var ordered = new List<OcrLineResult>(lines.Count);
        RecursiveXYCut(lines.ToList(), ordered);
        return ordered;
    }

    public static string BuildFullText(IReadOnlyList<OcrLineResult> sortedLines)
    {
        if (sortedLines.Count == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < sortedLines.Count; i++)
        {
            var cur = sortedLines[i];
            if (i > 0)
            {
                var prev = sortedLines[i - 1];
                // If two lines are on the same visual row (e.g. key-value cells on one row) and cur is to the right of prev:
                if (AreOnSameVisualRow(prev.Bounds, cur.Bounds) && cur.Bounds.X >= prev.Bounds.Right - 8)
                {
                    sb.Append("  ");
                }
                else
                {
                    sb.AppendLine();
                }
            }
            sb.Append(cur.Text);
        }
        return sb.ToString();
    }

    private static void RecursiveXYCut(List<OcrLineResult> group, List<OcrLineResult> output)
    {
        if (group.Count == 0) return;
        if (group.Count == 1)
        {
            output.Add(group[0]);
            return;
        }

        // 1. Try horizontal split (top vs bottom sections separated by a full-width vertical whitespace gap).
        group.Sort(static (a, b) =>
        {
            var cmp = a.Bounds.Y.CompareTo(b.Bounds.Y);
            return cmp != 0 ? cmp : a.Bounds.X.CompareTo(b.Bounds.X);
        });

        var avgH = Math.Max(10, (int)group.Average(l => l.Bounds.Height));
        var maxBottomSoFar = group[0].Bounds.Bottom;
        for (var i = 1; i < group.Count; i++)
        {
            var gapY = group[i].Bounds.Y - maxBottomSoFar;
            if (gapY >= Math.Max(12, (int)(avgH * 0.85)))
            {
                var topGroup = group.GetRange(0, i);
                var bottomGroup = group.GetRange(i, group.Count - i);
                RecursiveXYCut(topGroup, output);
                RecursiveXYCut(bottomGroup, output);
                return;
            }
            maxBottomSoFar = Math.Max(maxBottomSoFar, group[i].Bounds.Bottom);
        }

        // 2. Try vertical column split (left column vs right column separated by a clear vertical gutter).
        // Only treat as multi-column when there are >= 3 lines and at least one side has >= 2 vertically stacked lines.
        if (group.Count >= 3)
        {
            var byX = new List<OcrLineResult>(group);
            byX.Sort(static (a, b) =>
            {
                var cmp = a.Bounds.X.CompareTo(b.Bounds.X);
                return cmp != 0 ? cmp : a.Bounds.Y.CompareTo(b.Bounds.Y);
            });

            var maxRightSoFar = byX[0].Bounds.Right;
            var bestSplitIndex = -1;
            var bestGutter = 0;

            for (var i = 1; i < byX.Count; i++)
            {
                var gutterX = byX[i].Bounds.X - maxRightSoFar;
                if (gutterX >= Math.Max(28, (int)(avgH * 1.8)) && gutterX > bestGutter)
                {
                    var leftCol = byX.GetRange(0, i);
                    var rightCol = byX.GetRange(i, byX.Count - i);
                    if (CountDistinctVerticalRows(leftCol) >= 2 || CountDistinctVerticalRows(rightCol) >= 2)
                    {
                        bestGutter = gutterX;
                        bestSplitIndex = i;
                    }
                }
                maxRightSoFar = Math.Max(maxRightSoFar, byX[i].Bounds.Right);
            }

            if (bestSplitIndex > 0)
            {
                var leftCol = byX.GetRange(0, bestSplitIndex);
                var rightCol = byX.GetRange(bestSplitIndex, byX.Count - bestSplitIndex);
                RecursiveXYCut(leftCol, output);
                RecursiveXYCut(rightCol, output);
                return;
            }
        }

        // 3. Atomic block: cluster into visual rows and sort each row strictly left-to-right.
        var rows = new List<List<OcrLineResult>>();
        foreach (var line in group)
        {
            List<OcrLineResult>? matchedRow = null;
            foreach (var row in rows)
            {
                if (AreOnSameVisualRow(row[0].Bounds, line.Bounds))
                {
                    matchedRow = row;
                    break;
                }
            }
            if (matchedRow is not null) matchedRow.Add(line);
            else rows.Add([line]);
        }

        rows.Sort(static (r1, r2) => r1[0].Bounds.Y.CompareTo(r2[0].Bounds.Y));
        foreach (var row in rows)
        {
            row.Sort(static (a, b) =>
            {
                var cmp = a.Bounds.X.CompareTo(b.Bounds.X);
                return cmp != 0 ? cmp : a.Bounds.Y.CompareTo(b.Bounds.Y);
            });
            output.AddRange(row);
        }
    }

    private static int CountDistinctVerticalRows(List<OcrLineResult> lines)
    {
        if (lines.Count <= 1) return lines.Count;
        var sorted = new List<OcrLineResult>(lines);
        sorted.Sort(static (a, b) => a.Bounds.Y.CompareTo(b.Bounds.Y));
        var rows = 1;
        var currentRow = sorted[0].Bounds;
        for (var i = 1; i < sorted.Count; i++)
        {
            if (!AreOnSameVisualRow(currentRow, sorted[i].Bounds))
            {
                rows++;
                currentRow = sorted[i].Bounds;
            }
        }
        return rows;
    }

    public static bool AreOnSameVisualRow(PixelRect a, PixelRect b)
    {
        var centerA = a.Y + a.Height * 0.5;
        var centerB = b.Y + b.Height * 0.5;
        var minH = Math.Max(8, Math.Min(a.Height, b.Height));
        return Math.Abs(centerA - centerB) <= minH * 0.45;
    }
}
