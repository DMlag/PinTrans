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
/// Pre- and post-processing utilities for local offline OCR (CJK spacing normalization and small-crop upscaling).
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
    /// Small crops (e.g. a single line of 12-16px text) are upscaled 2x with bilinear interpolation and padded
    /// with a perimeter-colored border so <c>Windows.Media.Ocr</c> achieves high recognition accuracy.
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

        var scaledW = source.Width * scale;
        var scaledH = source.Height * scale;
        var outW = scaledW + padding * 2;
        var outH = scaledH + padding * 2;

        var borderColor = SamplePerimeterColor(source);
        var result = new Bgra32Image(outW, outH);
        for (var y = 0; y < outH; y++)
        for (var x = 0; x < outW; x++)
            result.SetPixel(x, y, borderColor);

        if (scale == 1)
        {
            result.Blit(source, padding, padding);
            return result;
        }

        // Bilinear 2x upscale into [padding .. padding + scaledW, padding .. padding + scaledH]
        for (var dy = 0; dy < scaledH; dy++)
        {
            var fy = (dy + 0.5) / scale - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(fy), 0, source.Height - 1);
            var y1 = Math.Clamp(y0 + 1, 0, source.Height - 1);
            var wy = Math.Clamp(fy - y0, 0.0, 1.0);

            for (var dx = 0; dx < scaledW; dx++)
            {
                var fx = (dx + 0.5) / scale - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(fx), 0, source.Width - 1);
                var x1 = Math.Clamp(x0 + 1, 0, source.Width - 1);
                var wx = Math.Clamp(fx - x0, 0.0, 1.0);

                var c00 = source.GetPixel(x0, y0);
                var c10 = source.GetPixel(x1, y0);
                var c01 = source.GetPixel(x0, y1);
                var c11 = source.GetPixel(x1, y1);

                var b = Bilinear(c00.B, c10.B, c01.B, c11.B, wx, wy);
                var g = Bilinear(c00.G, c10.G, c01.G, c11.G, wx, wy);
                var r = Bilinear(c00.R, c10.R, c01.R, c11.R, wx, wy);
                result.SetPixel(padding + dx, padding + dy, new Bgra32(b, g, r, 255));
            }
        }

        return result;
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

    private static byte Bilinear(byte v00, byte v10, byte v01, byte v11, double wx, double wy)
    {
        var top = v00 * (1 - wx) + v10 * wx;
        var bot = v01 * (1 - wx) + v11 * wx;
        return (byte)Math.Clamp((int)Math.Round(top * (1 - wy) + bot * wy), 0, 255);
    }

    private static Bgra32 SamplePerimeterColor(Bgra32Image img)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var x = 0; x < img.Width; x++)
        {
            var cTop = img.GetPixel(x, 0);
            var cBot = img.GetPixel(x, img.Height - 1);
            r += cTop.R + cBot.R;
            g += cTop.G + cBot.G;
            b += cTop.B + cBot.B;
            n += 2;
        }
        return n == 0 ? new Bgra32(255, 255, 255) : new Bgra32((byte)(b / n), (byte)(g / n), (byte)(r / n));
    }
}
