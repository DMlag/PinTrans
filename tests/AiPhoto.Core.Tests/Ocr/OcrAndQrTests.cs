using AiPhoto.Core.Imaging;
using AiPhoto.Core.Ocr;
using ZXing;
using ZXing.Common;

namespace AiPhoto.Core.Tests.Ocr;

public sealed class OcrAndQrTests
{
    [Fact]
    public void JoinWords_StripsArtificialSpacesBetweenCjk_PreservesLatinSpaces()
    {
        var words = new[]
        {
            new OcrWordBox("智", new PixelRect(10, 10, 16, 18)),
            new OcrWordBox("能", new PixelRect(28, 10, 16, 18)),
            new OcrWordBox("截", new PixelRect(46, 10, 16, 18)),
            new OcrWordBox("图", new PixelRect(64, 10, 16, 18)),
            new OcrWordBox("PixPin", new PixelRect(84, 10, 48, 18)),
            new OcrWordBox("Pro", new PixelRect(138, 10, 28, 18)),
            new OcrWordBox("版", new PixelRect(170, 10, 16, 18)),
        };

        var joined = OcrTextFormatter.JoinWords(words);
        Assert.Equal("智能截图PixPin Pro版", joined);
    }

    [Fact]
    public void PrepareImageForOcr_SmallCrop_UpscalesAndMapsRectBack()
    {
        var small = new Bgra32Image(120, 28);
        var prepared = OcrTextFormatter.PrepareImageForOcr(small, out var scale, out var padding);

        Assert.Equal(2, scale);
        Assert.Equal(24, padding);
        Assert.Equal(120 * 2 + 48, prepared.Width);
        Assert.Equal(28 * 2 + 48, prepared.Height);

        var recognizedInPrepared = new PixelRect(padding + 10 * 2, padding + 4 * 2, 80 * 2, 16 * 2);
        var mapped = OcrTextFormatter.MapRectToSource(recognizedInPrepared, scale, padding, small.Bounds);

        Assert.Equal(new PixelRect(10, 4, 80, 16), mapped);
    }

    [Fact]
    public void QrCodeScanner_DecodesNormalAndDarkModeQrCodesWithBounds()
    {
        const string payload = "https://github.com/DMlag/aiphoto";
        var writer = new BarcodeWriterGeneric
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = 140, Height = 140, Margin = 2 },
        };
        var matrix = writer.Encode(payload);

        // Place normal QR code at (40, 30) and inverted (white-on-dark) QR code on a second image.
        var normalImg = RenderBitMatrix(matrix, canvasW: 300, canvasH: 220, offsetX: 40, offsetY: 30, invert: false);
        var normalResults = QrCodeScanner.Scan(normalImg);

        Assert.Single(normalResults);
        Assert.Equal(payload, normalResults[0].Text);
        Assert.Equal("二维码", normalResults[0].Format);
        Assert.True(normalResults[0].IsHttpUrl);
        Assert.True(normalResults[0].Bounds.Contains(110, 100));

        var invertedImg = RenderBitMatrix(matrix, canvasW: 300, canvasH: 220, offsetX: 60, offsetY: 40, invert: true);
        var invertedResults = QrCodeScanner.Scan(invertedImg);

        Assert.Single(invertedResults);
        Assert.Equal(payload, invertedResults[0].Text);
    }

    private static Bgra32Image RenderBitMatrix(BitMatrix matrix, int canvasW, int canvasH, int offsetX, int offsetY, bool invert)
    {
        var bg = invert ? new Bgra32(24, 24, 24) : new Bgra32(250, 250, 250);
        var fg = invert ? new Bgra32(245, 245, 245) : new Bgra32(15, 15, 15);
        var img = new Bgra32Image(canvasW, canvasH);

        for (var y = 0; y < canvasH; y++)
        for (var x = 0; x < canvasW; x++)
            img.SetPixel(x, y, bg);

        for (var y = 0; y < matrix.Height; y++)
        for (var x = 0; x < matrix.Width; x++)
        {
            if (matrix[x, y])
            {
                img.SetPixel(offsetX + x, offsetY + y, fg);
            }
        }

        return img;
    }
}
