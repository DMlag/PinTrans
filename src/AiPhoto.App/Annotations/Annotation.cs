using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiPhoto.Core.Imaging;

namespace AiPhoto.App.Annotations;

public enum AnnotationTool
{
    None,
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Highlighter,
    Text,
    Mosaic,
    Number,
}

/// <summary>Data available while rendering annotations (e.g. the underlying pixels for mosaic).</summary>
public sealed class AnnotationRenderContext(Bgra32Image baseImage)
{
    /// <summary>Image in the same coordinate space as the annotations.</summary>
    public Bgra32Image BaseImage { get; } = baseImage;
}

/// <summary>
/// Base class of all annotation shapes. Coordinates are in image pixels of the capture canvas,
/// so annotations are independent of monitor DPI and can be rendered at native resolution on export.
/// To add a new tool: derive from this class, add an <see cref="AnnotationTool"/> value and register it in
/// <see cref="AnnotationFactory"/>.
/// </summary>
public abstract class Annotation
{
    protected Annotation(Color color, double strokeWidth)
    {
        Color = color;
        StrokeWidth = strokeWidth;
    }

    public Color Color { get; }
    public double StrokeWidth { get; }

    /// <summary>False while the shape is too small to be meaningful (it is discarded on mouse-up).</summary>
    public abstract bool IsValid { get; }

    /// <summary>Called on mouse move while the user is drawing.</summary>
    public virtual void Update(Point point, bool constrain) { }

    public abstract void Render(DrawingContext dc, AnnotationRenderContext context);

    protected Pen CreatePen(double? width = null, PenLineCap cap = PenLineCap.Round)
    {
        var pen = new Pen(new SolidColorBrush(Color), width ?? StrokeWidth)
        {
            StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    protected static Point Constrain45(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        var len = Math.Sqrt(dx * dx + dy * dy);
        return new Point(start.X + Math.Cos(angle) * len, start.Y + Math.Sin(angle) * len);
    }
}

/// <summary>Shapes defined by a drag from <see cref="Start"/> to <see cref="End"/>.</summary>
public abstract class DragAnnotation : Annotation
{
    protected DragAnnotation(Point start, Color color, double strokeWidth) : base(color, strokeWidth)
    {
        Start = start;
        End = start;
    }

    public Point Start { get; }
    public Point End { get; protected set; }

    public override bool IsValid => Math.Abs(End.X - Start.X) >= 3 || Math.Abs(End.Y - Start.Y) >= 3;

    protected Rect DragRect => new(Start, End);

    public override void Update(Point point, bool constrain)
    {
        if (constrain && this is RectangleAnnotation or EllipseAnnotation or MosaicAnnotation)
        {
            // Square / circle.
            var size = Math.Max(Math.Abs(point.X - Start.X), Math.Abs(point.Y - Start.Y));
            point = new Point(Start.X + Math.Sign(point.X - Start.X) * size, Start.Y + Math.Sign(point.Y - Start.Y) * size);
        }
        else if (constrain)
        {
            point = Constrain45(Start, point);
        }
        End = point;
    }
}

public sealed class RectangleAnnotation(Point start, Color color, double strokeWidth) : DragAnnotation(start, color, strokeWidth)
{
    public override void Render(DrawingContext dc, AnnotationRenderContext context)
        => dc.DrawRectangle(null, CreatePen(cap: PenLineCap.Square), DragRect);
}

public sealed class EllipseAnnotation(Point start, Color color, double strokeWidth) : DragAnnotation(start, color, strokeWidth)
{
    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var r = DragRect;
        dc.DrawEllipse(null, CreatePen(), new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
    }
}

public sealed class LineAnnotation(Point start, Color color, double strokeWidth) : DragAnnotation(start, color, strokeWidth)
{
    public override void Render(DrawingContext dc, AnnotationRenderContext context)
        => dc.DrawLine(CreatePen(), Start, End);
}

public sealed class ArrowAnnotation(Point start, Color color, double strokeWidth) : DragAnnotation(start, color, strokeWidth)
{
    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var dir = End - Start;
        var length = dir.Length;
        if (length < 1) return;
        dir.Normalize();
        var normal = new Vector(-dir.Y, dir.X);

        var headLength = Math.Min(length, Math.Max(12, StrokeWidth * 4.5));
        var headWidth = headLength * 0.62;
        var baseCenter = End - dir * headLength;

        // Shaft stops inside the head so the tip stays sharp.
        dc.DrawLine(CreatePen(), Start, End - dir * (headLength * 0.6));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(End, isFilled: true, isClosed: true);
            ctx.LineTo(baseCenter + normal * headWidth / 2, true, true);
            ctx.LineTo(End - dir * headLength * 0.78, true, true);
            ctx.LineTo(baseCenter - normal * headWidth / 2, true, true);
        }
        geometry.Freeze();
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }
}

/// <summary>Freehand stroke. A highlighter is a wide, semi-transparent pen with flat caps.</summary>
public sealed class PenAnnotation : Annotation
{
    private readonly List<Point> _points = [];
    private readonly bool _highlighter;

    public PenAnnotation(Point start, Color color, double strokeWidth, bool highlighter)
        : base(highlighter ? Color.FromArgb(110, color.R, color.G, color.B) : color, highlighter ? strokeWidth * 4 : strokeWidth)
    {
        _highlighter = highlighter;
        _points.Add(start);
    }

    public override bool IsValid => _points.Count > 1;

    public override void Update(Point point, bool constrain)
    {
        if (constrain && _points.Count >= 1)
        {
            // Straight stroke from the starting point.
            var start = _points[0];
            _points.Clear();
            _points.Add(start);
            _points.Add(new Point(point.X, _highlighter ? start.Y : point.Y));
            return;
        }
        var last = _points[^1];
        if ((point - last).LengthSquared >= 1) _points.Add(point);
    }

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        if (_points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(_points[0], false, false);
            ctx.PolyLineTo(_points.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, CreatePen(cap: _highlighter ? PenLineCap.Square : PenLineCap.Round), geometry);
    }
}

public sealed class TextAnnotation : Annotation
{
    public TextAnnotation(Point position, string text, Color color, double fontSize, string fontFamily) : base(color, 1)
    {
        Position = position;
        Text = text;
        FontSize = fontSize;
        FontFamily = fontFamily;
    }

    public Point Position { get; }
    public string Text { get; }
    public double FontSize { get; }
    public string FontFamily { get; }

    public override bool IsValid => !string.IsNullOrWhiteSpace(Text);

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            FontSize, brush, 1.0);
        dc.DrawText(ft, Position);
    }
}

/// <summary>Numbered marker (1, 2, 3...) for step-by-step instructions.</summary>
public sealed class NumberAnnotation : Annotation
{
    public NumberAnnotation(Point center, int number, Color color, double size) : base(color, 1)
    {
        Center = center;
        Number = number;
        Size = size;
    }

    public Point Center { get; private set; }
    public int Number { get; }
    public double Size { get; }

    public override bool IsValid => true;

    public override void Update(Point point, bool constrain) => Center = point;

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var radius = Size * 0.75;
        var fill = new SolidColorBrush(Color);
        fill.Freeze();
        var outline = new Pen(Brushes.White, Math.Max(1.5, radius * 0.12));
        outline.Freeze();
        dc.DrawEllipse(fill, outline, Center, radius, radius);

        var luminance = (0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B) / 255;
        var ft = new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            radius * 1.1, luminance > 0.6 ? Brushes.Black : Brushes.White, 1.0);
        dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
    }
}

/// <summary>Pixelates the underlying image inside the rectangle.</summary>
public sealed class MosaicAnnotation(Point start, int blockSize) : DragAnnotation(start, Colors.Transparent, 0)
{
    private BitmapSource? _cache;
    private PixelRect _cacheRect;

    public int BlockSize { get; } = Math.Max(2, blockSize);

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var r = DragRect;
        var rect = PixelRect.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom))
            .Intersect(context.BaseImage.Bounds);
        if (rect.IsEmpty) return;

        if (_cache is null || _cacheRect != rect)
        {
            _cache = Pixelate(context.BaseImage, rect, BlockSize);
            _cacheRect = rect;
        }
        dc.DrawImage(_cache, new Rect(rect.X, rect.Y, rect.Width, rect.Height));
    }

    private static BitmapSource Pixelate(Bgra32Image image, PixelRect rect, int block)
    {
        var result = new Bgra32Image(rect.Width, rect.Height);
        for (var by = 0; by < rect.Height; by += block)
        for (var bx = 0; bx < rect.Width; bx += block)
        {
            // Blocks start at the rectangle's top-left corner; the last row/column may be partial.
            var bw = Math.Min(block, rect.Width - bx);
            var bh = Math.Min(block, rect.Height - by);
            long r = 0, g = 0, b = 0;
            for (var y = 0; y < bh; y++)
            for (var x = 0; x < bw; x++)
            {
                var c = image.GetPixel(rect.X + bx + x, rect.Y + by + y);
                r += c.R; g += c.G; b += c.B;
            }
            var n = bw * bh;
            var avg = new Bgra32((byte)(b / n), (byte)(g / n), (byte)(r / n));
            for (var y = 0; y < bh; y++)
            for (var x = 0; x < bw; x++)
                result.SetPixel(bx + x, by + y, avg);
        }
        var bmp = BitmapSource.Create(result.Width, result.Height, 96, 96, PixelFormats.Bgra32, null, result.Pixels, result.Stride);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>Creates the annotation for a tool at the point where the user pressed the mouse.</summary>
public static class AnnotationFactory
{
    public static Annotation? Create(AnnotationTool tool, Point start, AnnotationStyle style, int nextNumber) => tool switch
    {
        AnnotationTool.Rectangle => new RectangleAnnotation(start, style.Color, style.StrokeWidth),
        AnnotationTool.Ellipse => new EllipseAnnotation(start, style.Color, style.StrokeWidth),
        AnnotationTool.Arrow => new ArrowAnnotation(start, style.Color, style.StrokeWidth),
        AnnotationTool.Line => new LineAnnotation(start, style.Color, style.StrokeWidth),
        AnnotationTool.Pen => new PenAnnotation(start, style.Color, style.StrokeWidth, highlighter: false),
        AnnotationTool.Highlighter => new PenAnnotation(start, style.Color, style.StrokeWidth, highlighter: true),
        AnnotationTool.Mosaic => new MosaicAnnotation(start, style.MosaicBlockSize),
        AnnotationTool.Number => new NumberAnnotation(start, nextNumber, style.Color, style.FontSize),
        _ => null, // Text is created by the inline editor.
    };
}

public sealed class AnnotationStyle
{
    public Color Color { get; set; } = Color.FromRgb(0xE5, 0x39, 0x35);
    public double StrokeWidth { get; set; } = 3;
    public double FontSize { get; set; } = 18;
    public int MosaicBlockSize { get; set; } = 10;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
}
