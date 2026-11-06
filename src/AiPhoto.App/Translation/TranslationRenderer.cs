using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AiPhoto.App.Imaging;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Ocr;
using AiPhoto.Core.Translation;

namespace AiPhoto.App.Translation;

/// <summary>A translated block laid out for drawing on top of the source image (image pixel coordinates).</summary>
public sealed class RenderedBlock
{
    public required TranslationBlock Block { get; init; }
    public required Rect Area { get; init; }
    public required Brush Background { get; init; }
    public required FormattedText Text { get; init; }
    public required Point TextOrigin { get; init; }
}

/// <summary>
/// Paints translations over the original text: the block is filled with the surrounding background colour
/// and the translation is drawn with the largest font size that fits the original area.
/// </summary>
public static class TranslationRenderer
{
    private const double MinFontSize = 9;
    private const double MaxFontSize = 120;

    public static RenderedBlock? Layout(Bgra32Image image, TranslationBlock block, string fontFamily)
    {
        if (block.Bounds is not { } bounds || string.IsNullOrWhiteSpace(block.Translation)) return null;

        var colors = TextColorEstimator.Estimate(image, bounds);
        var area = bounds.Inflate(2).Intersect(image.Bounds).ToRect();
        var background = new SolidColorBrush(colors.Background.ToColor());
        background.Freeze();
        var foreground = new SolidColorBrush(colors.Foreground.ToColor());
        foreground.Freeze();
        if (string.IsNullOrWhiteSpace(fontFamily)) fontFamily = "Microsoft YaHei UI";
        var typeface = new Typeface(new FontFamily(fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var pad = Math.Clamp(area.Height * 0.06, 1, 4);
        var maxWidth = Math.Max(4, area.Width - pad * 2);
        var maxHeight = Math.Max(4, area.Height - pad * 2);
        var text = block.Translation.Trim();

        FormattedText Make(double size)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, foreground, 1.0)
            {
                MaxTextWidth = maxWidth,
                Trimming = TextTrimming.None,
            };
            return ft;
        }

        bool Fits(FormattedText ft) => ft.Height <= maxHeight + 0.5 && ft.WidthIncludingTrailingWhitespace <= maxWidth + 0.5;

        // Binary search for the largest font size that fits.
        double lo = MinFontSize, hi = Math.Clamp(maxHeight, MinFontSize, MaxFontSize);
        var best = Make(lo);
        if (Fits(best))
        {
            var top = Make(hi);
            if (Fits(top))
            {
                best = top;
            }
            else
            {
                for (var i = 0; i < 10 && hi - lo > 0.25; i++)
                {
                    var mid = (lo + hi) / 2;
                    var ft = Make(mid);
                    if (Fits(ft)) { lo = mid; best = ft; } else hi = mid;
                }
            }
        }
        else
        {
            // Even the minimum size overflows: grow the painted area downwards.
            area = new Rect(area.X, area.Y, area.Width, Math.Min(image.Height - area.Y, best.Height + pad * 2));
        }

        var origin = new Point(area.X + pad, area.Y + Math.Max(pad, (area.Height - best.Height) / 2));
        return new RenderedBlock { Block = block, Area = area, Background = background, Text = best, TextOrigin = origin };
    }

    public static void Render(DrawingContext dc, IEnumerable<RenderedBlock> blocks)
    {
        foreach (var b in blocks)
        {
            dc.DrawRectangle(b.Background, null, b.Area);
            dc.DrawText(b.Text, b.TextOrigin);
        }
    }

    /// <summary>Burns the translation into a copy of the image (for "copy/save translated image").</summary>
    public static Bgra32Image RenderToImage(Bgra32Image image, IEnumerable<RenderedBlock> blocks)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image.ToBitmapSource(), new Rect(0, 0, image.Width, image.Height));
            Render(dc, blocks);
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb.ToBgra32Image();
    }
}

/// <summary>Draws translated blocks, offline OCR line boxes, and QR code highlights over an image in pixel space.</summary>
public sealed class TranslationLayer : FrameworkElement
{
    private static readonly Pen HighlightPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xDD, 0x2F, 0x7C, 0xF6)), 1.6));
    private static readonly Pen OcrIdlePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x2F, 0x7C, 0xF6)), 1.0));
    private static readonly Brush OcrHoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0x2F, 0x7C, 0xF6)));
    private static readonly Pen QrIdlePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0xB9, 0x81)), 1.8));
    private static readonly Brush QrIdleFill = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x10, 0xB9, 0x81)));
    private static readonly Brush QrHoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x44, 0x10, 0xB9, 0x81)));

    private readonly List<RenderedBlock> _blocks = [];
    private readonly List<OcrLineResult> _ocrLines = [];
    private readonly List<QrCodeResult> _barcodes = [];
    private bool _showOriginal;
    private RenderedBlock? _highlight;
    private OcrLineResult? _highlightOcr;
    private QrCodeResult? _highlightBarcode;

    public TranslationLayer()
    {
        IsHitTestVisible = false;
    }

    public IReadOnlyList<RenderedBlock> Blocks => _blocks;
    public IReadOnlyList<OcrLineResult> OcrLines => _ocrLines;
    public IReadOnlyList<QrCodeResult> Barcodes => _barcodes;

    public bool ShowOriginal
    {
        get => _showOriginal;
        set { _showOriginal = value; InvalidateVisual(); }
    }

    public RenderedBlock? Highlight
    {
        get => _highlight;
        set { if (!ReferenceEquals(_highlight, value)) { _highlight = value; InvalidateVisual(); } }
    }

    public OcrLineResult? HighlightOcr
    {
        get => _highlightOcr;
        set { if (!ReferenceEquals(_highlightOcr, value)) { _highlightOcr = value; InvalidateVisual(); } }
    }

    public QrCodeResult? HighlightBarcode
    {
        get => _highlightBarcode;
        set { if (!ReferenceEquals(_highlightBarcode, value)) { _highlightBarcode = value; InvalidateVisual(); } }
    }

    public void Add(RenderedBlock block)
    {
        _blocks.Add(block);
        InvalidateVisual();
    }

    public void SetOcrResults(IReadOnlyList<OcrLineResult> lines, IReadOnlyList<QrCodeResult> barcodes)
    {
        _ocrLines.Clear();
        _ocrLines.AddRange(lines);
        _barcodes.Clear();
        _barcodes.AddRange(barcodes);
        _highlightOcr = null;
        _highlightBarcode = null;
        InvalidateVisual();
    }

    public void Clear()
    {
        _blocks.Clear();
        _ocrLines.Clear();
        _barcodes.Clear();
        _highlight = null;
        _highlightOcr = null;
        _highlightBarcode = null;
        InvalidateVisual();
    }

    /// <summary>Topmost translated block at an image-pixel position.</summary>
    public RenderedBlock? BlockAt(Point p) => _blocks.LastOrDefault(b => b.Area.Contains(p));

    /// <summary>Topmost recognized OCR line at an image-pixel position.</summary>
    public OcrLineResult? OcrLineAt(Point p)
        => _ocrLines.LastOrDefault(l => l.Bounds.Inflate(2).Contains((int)Math.Round(p.X), (int)Math.Round(p.Y)));

    /// <summary>Topmost recognized QR/barcode at an image-pixel position.</summary>
    public QrCodeResult? BarcodeAt(Point p)
        => _barcodes.LastOrDefault(b => b.Bounds.Inflate(2).Contains((int)Math.Round(p.X), (int)Math.Round(p.Y)));

    protected override void OnRender(DrawingContext dc)
    {
        if (!_showOriginal && _blocks.Count > 0)
        {
            TranslationRenderer.Render(dc, _blocks);
        }

        foreach (var line in _ocrLines)
        {
            var r = line.Bounds.Inflate(2).ToRect();
            var isHovered = ReferenceEquals(line, _highlightOcr);
            dc.DrawRoundedRectangle(isHovered ? OcrHoverFill : null, isHovered ? HighlightPen : OcrIdlePen, r, 2, 2);
        }

        foreach (var qr in _barcodes)
        {
            var r = qr.Bounds.ToRect();
            var isHovered = ReferenceEquals(qr, _highlightBarcode);
            dc.DrawRoundedRectangle(isHovered ? QrHoverFill : QrIdleFill, QrIdlePen, r, 3, 3);
        }

        if (_highlight is not null)
        {
            dc.DrawRectangle(null, HighlightPen, _highlight.Area);
        }
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
