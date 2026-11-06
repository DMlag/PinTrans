using System.Runtime.InteropServices.WindowsRuntime;
using AiPhoto.App.Diagnostics;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Ocr;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AiPhoto.App.Ocr;

/// <summary>
/// Local, zero-cloud-latency OCR + QR/Barcode recognition service.
/// <para>
/// Automatically uses the neural <b>PP-OCRv4 ONNX</b> engine (<see cref="PaddleOcrOnnxEngine"/>) when models are
/// available (bundled in <c>models/ppocrv4</c> or downloaded to <c>%AppData%\PinTrans\models\ppocrv4</c>), and
/// falls back to the enhanced <see cref="OcrEngine"/> (<c>Windows.Media.Ocr</c> with dark-mode auto-inversion,
/// ClearType de-fringing, Catmull-Rom bicubic upscaling, and XY-Cut layout sorting) otherwise.
/// </para>
/// </summary>
public static class WindowsOfflineOcrService
{
    private static readonly object EngineGate = new();
    private static PaddleOcrOnnxEngine? _paddleEngine;
    private static bool _paddleProbed;

    public static bool IsPaddleOcrAvailable
        => PaddleOcrModelManager.TryResolveModelPaths(AppPaths.ModelsDirectory, out _, out _);

    public static void ResetPaddleEngineCache()
    {
        lock (EngineGate)
        {
            _paddleEngine?.Dispose();
            _paddleEngine = null;
            _paddleProbed = false;
        }
    }

    private static PaddleOcrOnnxEngine? GetOrCreatePaddleEngine()
    {
        lock (EngineGate)
        {
            if (_paddleEngine is not null) return _paddleEngine;
            if (_paddleProbed) return null;
            _paddleProbed = true;

            if (!PaddleOcrModelManager.TryResolveModelPaths(AppPaths.ModelsDirectory, out var detPath, out var recPath))
            {
                return null;
            }

            try
            {
                _paddleEngine = new PaddleOcrOnnxEngine(detPath, recPath);
                Log.Info($"Loaded PP-OCRv4 ONNX engine from {Path.GetDirectoryName(detPath)}");
                return _paddleEngine;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to initialize PP-OCRv4 ONNX engine, falling back to Windows.Media.Ocr: {ex.Message}");
                return null;
            }
        }
    }

    public static async Task<OcrRecognitionResult> RecognizeAsync(Bgra32Image image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 1. Scan QR codes / barcodes on a background thread in parallel.
        var barcodeTask = Task.Run(() => QrCodeScanner.Scan(image), cancellationToken);

        // 2. Prefer PP-OCRv4 ONNX neural engine if models are ready; otherwise fall back to enhanced Windows.Media.Ocr.
        IReadOnlyList<OcrLineResult> lines = [];
        var usedPaddle = false;

        var paddle = GetOrCreatePaddleEngine();
        if (paddle is not null)
        {
            try
            {
                lines = await Task.Run(() => paddle.RecognizeLines(image, cancellationToken), cancellationToken).ConfigureAwait(false);
                usedPaddle = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"PP-OCRv4 ONNX inference failed, falling back to Windows.Media.Ocr: {ex.Message}");
            }
        }

        if (!usedPaddle)
        {
            lines = await RecognizeWithWindowsOcrAsync(image, cancellationToken).ConfigureAwait(false);
        }

        var barcodes = await barcodeTask.ConfigureAwait(false);
        var sortedLines = OcrLayoutAnalyzer.SortLinesInReadingOrder(lines);
        var fullText = OcrLayoutAnalyzer.BuildFullText(sortedLines);

        return new OcrRecognitionResult(fullText, sortedLines, barcodes);
    }

    private static async Task<IReadOnlyList<OcrLineResult>> RecognizeWithWindowsOcrAsync(Bgra32Image image, CancellationToken cancellationToken)
    {
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

        return lines;
    }

    private static OcrEngine? CreateBestOcrEngine()
    {
        try
        {
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
