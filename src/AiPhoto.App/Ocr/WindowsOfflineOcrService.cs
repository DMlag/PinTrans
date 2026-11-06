using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using AiPhoto.App.Diagnostics;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AiPhoto.App.Ocr;

/// <summary>
/// Local, zero-cloud-latency OCR + QR/Barcode recognition service backed by the Windows 10/11 built-in
/// <see cref="OcrEngine"/> and <see cref="QrCodeScanner"/>.
/// </summary>
public static class WindowsOfflineOcrService
{
    public static async Task<OcrRecognitionResult> RecognizeAsync(Bgra32Image image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 1. Scan QR codes / barcodes on a background thread.
        var barcodeTask = Task.Run(() => QrCodeScanner.Scan(image), cancellationToken);

        // 2. Run Windows.Media.Ocr.OcrEngine on the prepared (upscaled/padded if small) image.
        var lines = new List<OcrLineResult>();
        try
        {
            var engine = CreateBestOcrEngine();
            if (engine is not null)
            {
                var prepared = OcrTextFormatter.PrepareImageForOcr(image, out var scale, out var padding);
                var tight = prepared.Stride == prepared.Width * Bgra32Image.BytesPerPixel
                    ? prepared
                    : prepared.Crop(prepared.Bounds);

                // Ensure alpha channel is 255 for SoftwareBitmap Bgra8.
                var buffer = tight.Pixels.AsBuffer();
                using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    buffer,
                    BitmapPixelFormat.Bgra8,
                    tight.Width,
                    tight.Height,
                    BitmapAlphaMode.Ignore);

                var ocrResult = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
                if (ocrResult?.Lines is not null)
                {
                    foreach (var line in ocrResult.Lines)
                    {
                        if (line.Words is null || line.Words.Count == 0) continue;

                        var wordBoxes = new List<OcrWordBox>(line.Words.Count);
                        var lineBounds = default(PixelRect);

                        foreach (var w in line.Words)
                        {
                            if (string.IsNullOrWhiteSpace(w.Text)) continue;
                            var rawRect = new PixelRect(
                                (int)Math.Floor(w.BoundingRect.X),
                                (int)Math.Floor(w.BoundingRect.Y),
                                (int)Math.Ceiling(w.BoundingRect.Width),
                                (int)Math.Ceiling(w.BoundingRect.Height));
                            var mapped = OcrTextFormatter.MapRectToSource(rawRect, scale, padding, image.Bounds);
                            if (mapped.IsEmpty) continue;
                            wordBoxes.Add(new OcrWordBox(w.Text, mapped));
                            lineBounds = lineBounds.IsEmpty ? mapped : lineBounds.Union(mapped);
                        }

                        if (wordBoxes.Count == 0 || lineBounds.IsEmpty) continue;
                        var normalizedText = OcrTextFormatter.JoinWords(wordBoxes);
                        if (!string.IsNullOrWhiteSpace(normalizedText))
                        {
                            lines.Add(new OcrLineResult(normalizedText, lineBounds, wordBoxes));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Windows Offline OCR failed: {ex.Message}");
        }

        var barcodes = await barcodeTask.ConfigureAwait(false);

        // Sort lines in natural reading order (top-to-bottom, then left-to-right for same visual row).
        lines.Sort( static (a, b) =>
        {
            var avgH = Math.Max(8, (a.Bounds.Height + b.Bounds.Height) / 2);
            var dy = a.Bounds.Y - b.Bounds.Y;
            if (Math.Abs(dy) > avgH / 2) return dy.CompareTo(0);
            return a.Bounds.X.CompareTo(b.Bounds.X);
        });

        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(l.Text);
        }

        return new OcrRecognitionResult(sb.ToString(), lines, barcodes);
    }

    private static OcrEngine? CreateBestOcrEngine()
    {
        try
        {
            // Prefer a CJK-capable recognizer if installed (since Windows Chinese OCR also recognizes English/Latin text),
            // otherwise fall back to user profile languages or the first available recognizer.
            var available = OcrEngine.AvailableRecognizerLanguages;
            if (available is not null && available.Count > 0)
            {
                var preferredTags = new[] { "zh-Hans-CN", "zh-CN", "zh-Hans", "zh-Hant", "ja", "en-US", "en" };
                foreach (var pref in preferredTags)
                {
                    var match = available.FirstOrDefault(l =>
                        l.LanguageTag.Equals(pref, StringComparison.OrdinalIgnoreCase) ||
                        l.LanguageTag.StartsWith(pref + "-", StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        var eng = OcrEngine.TryCreateFromLanguage(match);
                        if (eng is not null) return eng;
                    }
                }
            }

            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (available is { Count: > 0 } ? OcrEngine.TryCreateFromLanguage(available[0]) : null);
        }
        catch (Exception ex)
        {
            Log.Warn($"CreateBestOcrEngine failed: {ex.Message}");
            return null;
        }
    }
}
