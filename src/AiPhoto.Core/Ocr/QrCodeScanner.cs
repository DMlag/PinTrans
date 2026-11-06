using AiPhoto.Core.Imaging;
using ZXing;
using ZXing.Common;

namespace AiPhoto.Core.Ocr;

/// <summary>
/// Offline QR code and barcode detector/decoder backed by ZXing.Net.
/// Works directly on <see cref="Bgra32Image"/> buffers, supporting multiple codes and inverted (dark-mode) QR codes.
/// </summary>
public static class QrCodeScanner
{
    private static readonly BarcodeFormat[] SupportedFormats =
    [
        BarcodeFormat.QR_CODE,
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.AZTEC,
        BarcodeFormat.PDF_417,
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.EAN_13,
        BarcodeFormat.EAN_8,
        BarcodeFormat.UPC_A,
    ];

    public static IReadOnlyList<QrCodeResult> Scan(Bgra32Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 12 || image.Height < 12) return [];

        byte[] packedPixels;
        if (image.Stride == image.Width * Bgra32Image.BytesPerPixel)
        {
            packedPixels = image.Pixels;
        }
        else
        {
            var tight = image.Crop(image.Bounds);
            packedPixels = tight.Pixels;
        }

        var luminance = new RGBLuminanceSource(packedPixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGR32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                CharacterSet = "UTF-8",
                PossibleFormats = SupportedFormats,
            },
        };

        var results = new List<QrCodeResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Collect(Result[]? decoded)
        {
            if (decoded is null) return;
            foreach (var r in decoded)
            {
                if (string.IsNullOrWhiteSpace(r.Text)) continue;
                var bounds = ComputeBounds(r.ResultPoints, image.Bounds);
                var key = $"{r.BarcodeFormat}:{r.Text}";
                if (seen.Add(key))
                {
                    results.Add(new QrCodeResult(r.Text.Trim(), FormatDisplayName(r.BarcodeFormat), bounds));
                }
            }
        }

        try
        {
            Collect(reader.DecodeMultiple(luminance));
            if (results.Count == 0)
            {
                var single = reader.Decode(luminance);
                if (single is not null) Collect([single]);
            }
        }
        catch
        {
            // Ignore malformed barcode bitmaps.
        }

        return results;
    }

    public static bool TryGetHttpUri(string? text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return true;
    }

    private static PixelRect ComputeBounds(ResultPoint[]? points, PixelRect imageBounds)
    {
        if (points is null || points.Length == 0) return imageBounds;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var valid = 0;

        foreach (var p in points)
        {
            if (p is null) continue;
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
            valid++;
        }

        if (valid == 0) return imageBounds;

        // QR finder patterns sit slightly inside the outer quiet zone; inflate by ~12% so the highlight box covers the full code.
        var span = Math.Max(16.0, Math.Max(maxX - minX, maxY - minY));
        var pad = Math.Max(6, (int)Math.Round(span * 0.14));
        var rect = PixelRect.FromLTRB(
            (int)Math.Floor(minX) - pad,
            (int)Math.Floor(minY) - pad,
            (int)Math.Ceiling(maxX) + pad,
            (int)Math.Ceiling(maxY) + pad).Intersect(imageBounds);

        return rect.IsEmpty ? imageBounds : rect;
    }

    private static string FormatDisplayName(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "二维码",
        BarcodeFormat.DATA_MATRIX => "DataMatrix",
        BarcodeFormat.AZTEC => "Aztec",
        BarcodeFormat.PDF_417 => "PDF417",
        _ => "条形码",
    };
}
