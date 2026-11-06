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

    [Fact]
    public void PrepareImageForOcr_DarkBackground_AutoInvertsToLightBackground()
    {
        // Create a dark-mode image (dark gray 24,24,24 background with bright white 240,240,240 stroke pixels)
        var dark = new Bgra32Image(200, 80);
        for (var y = 0; y < dark.Height; y++)
        for (var x = 0; x < dark.Width; x++)
            dark.SetPixel(x, y, new Bgra32(24, 24, 24));

        for (var y = 30; y < 46; y++)
        for (var x = 40; x < 160; x += 3)
            dark.SetPixel(x, y, new Bgra32(240, 240, 240));

        var prepared = OcrTextFormatter.PrepareImageForOcr(dark, out var scale, out var padding);

        Assert.Equal(1, scale);
        // After dark-mode auto-inversion, the background at the top-left corner must be bright (> 200),
        // and the stroke pixel must be dark (< 50).
        var corner = prepared.GetPixel(0, 0);
        var stroke = prepared.GetPixel(padding + 40, padding + 32);
        Assert.True(corner.G >= 220, $"Expected bright background after inversion, got {corner.G}");
        Assert.True(stroke.G <= 40, $"Expected dark foreground stroke after inversion, got {stroke.G}");
    }

    [Fact]
    public void OcrLayoutAnalyzer_TwoColumnLayout_OrdersLeftColumnBeforeRightColumn()
    {
        var lines = new[]
        {
            new OcrLineResult("左栏第一行", new PixelRect(20, 20, 120, 20), []),
            new OcrLineResult("右栏第一行", new PixelRect(240, 22, 120, 20), []),
            new OcrLineResult("左栏第二行", new PixelRect(20, 50, 120, 20), []),
            new OcrLineResult("右栏第二行", new PixelRect(240, 52, 120, 20), []),
        };

        var sorted = OcrLayoutAnalyzer.SortLinesInReadingOrder(lines);
        Assert.Equal(["左栏第一行", "左栏第二行", "右栏第一行", "右栏第二行"], sorted.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void PaddleOcrOnnxEngine_LoadsModelsAndRunsEndToEndInference()
    {
        const string detPath = "/tmp/ch_PP-OCRv4_det_infer.onnx";
        const string recPath = "/tmp/ch_PP-OCRv4_rec_infer.onnx";
        if (!PaddleOcrModelManager.IsValidModelFile(detPath) || !PaddleOcrModelManager.IsValidModelFile(recPath))
        {
            return;
        }

        using var engine = new PaddleOcrOnnxEngine(detPath, recPath);

        // Render a crisp bitmap with "123" using a 5x7 pixel font scaled 4x (20x28 per digit)
        var img = RenderSimpleDigitsImage("2026", scale: 4);
        var lines = engine.RecognizeLines(img);

        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Text.Contains("2026") || l.Text.Contains("20"));
    }

    private static Bgra32Image RenderSimpleDigitsImage(string digits, int scale)
    {
        var glyphs = new Dictionary<char, string[]>
        {
            ['0'] =
            [
                " ### ",
                "#   #",
                "#   #",
                "#   #",
                "#   #",
                "#   #",
                " ### ",
            ],
            ['2'] =
            [
                " ### ",
                "#   #",
                "    #",
                "  ## ",
                " #   ",
                "#    ",
                "#####",
            ],
            ['6'] =
            [
                " ### ",
                "#    ",
                "#    ",
                "#### ",
                "#   #",
                "#   #",
                " ### ",
            ],
        };

        var w = 48 + digits.Length * 7 * scale;
        var h = 48 + 7 * scale;
        var img = new Bgra32Image(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            img.SetPixel(x, y, new Bgra32(255, 255, 255));

        var ox = 24;
        var oy = 24;
        foreach (var ch in digits)
        {
            if (glyphs.TryGetValue(ch, out var rows))
            {
                for (var gy = 0; gy < rows.Length; gy++)
                {
                    for (var gx = 0; gx < rows[gy].Length; gx++)
                    {
                        if (rows[gy][gx] != '#') continue;
                        for (var dy = 0; dy < scale; dy++)
                        for (var dx = 0; dx < scale; dx++)
                            img.SetPixel(ox + gx * scale + dx, oy + gy * scale + dy, new Bgra32(15, 15, 15));
                    }
                }
            }
            ox += 7 * scale;
        }
        return img;
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
