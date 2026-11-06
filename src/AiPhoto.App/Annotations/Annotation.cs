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
    Blur,
    Number,
}

public enum AnnotationHandle
{
    None,
    Move,
    StartPoint,
    EndPoint,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>Data available while rendering annotations (e.g. the underlying pixels for mosaic/blur).</summary>
public sealed class AnnotationRenderContext(Bgra32Image baseImage)
{
    /// <summary>Image in the same coordinate space as the annotations.</summary>
    public Bgra32Image BaseImage { get; } = baseImage;
}

/// <summary>
/// Base class of all annotation shapes. Coordinates are in image pixels of the capture canvas,
/// so annotations are independent of monitor DPI and can be rendered at native resolution on export.
/// Supports hit-testing, secondary selection, control-handle dragging, style mutation, and cloning for undo/redo.
/// </summary>
public abstract class Annotation
{
    protected Annotation(Color color, double strokeWidth)
    {
        Color = color;
        StrokeWidth = strokeWidth;
    }

    public abstract AnnotationTool Kind { get; }

    public virtual Color Color { get; set; }

    public virtual double StrokeWidth { get; set; }

    public virtual bool SupportsColor => true;

    /// <summary>False while the shape is too small to be meaningful (it is discarded on mouse-up).</summary>
    public abstract bool IsValid { get; }

    /// <summary>Axis-aligned bounding box in canvas pixel coordinates.</summary>
    public abstract Rect Bounds { get; }

    /// <summary>Called on mouse move while the user is initially drawing the shape.</summary>
    public virtual void Update(Point point, bool constrain) { }

    public abstract void Render(DrawingContext dc, AnnotationRenderContext context);

    public abstract Annotation Clone();

    /// <summary>Gets the tool-specific size parameter (stroke width, font size, mosaic block size, or blur radius).</summary>
    public virtual double GetSizeValue() => StrokeWidth;

    /// <summary>Updates the tool-specific size parameter.</summary>
    public virtual void SetSizeValue(double value) => StrokeWidth = Math.Max(1, value);

    /// <summary>Returns true if <paramref name="point"/> hits the selectable body/stroke of this annotation.</summary>
    public abstract bool HitTest(Point point, double tolerance, AnnotationTool activeTool);

    /// <summary>Control handles displayed when this annotation is selected.</summary>
    public virtual IEnumerable<(AnnotationHandle Handle, Point Center)> GetHandles()
        => BoxHandles(Bounds);

    public AnnotationHandle HitTestHandle(Point point, double handleTolerance, double bodyTolerance, AnnotationTool activeTool)
    {
        foreach (var (handle, center) in GetHandles())
        {
            if (Math.Abs(point.X - center.X) <= handleTolerance && Math.Abs(point.Y - center.Y) <= handleTolerance)
            {
                return handle;
            }
        }
        return HitTest(point, bodyTolerance, activeTool) ? AnnotationHandle.Move : AnnotationHandle.None;
    }

    /// <summary>Applies a move or handle resize relative to the pre-drag <paramref name="baseline"/> state.</summary>
    public abstract void ApplyDrag(Annotation baseline, AnnotationHandle handle, Vector delta, bool constrain);

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

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var lenSq = ab.LengthSquared;
        if (lenSq < 0.0001) return (p - a).Length;
        var t = Math.Clamp(((p - a) * ab) / lenSq, 0.0, 1.0);
        var proj = a + t * ab;
        return (p - proj).Length;
    }

    protected static IEnumerable<(AnnotationHandle Handle, Point Center)> BoxHandles(Rect r)
    {
        if (r.IsEmpty) yield break;
        var cx = r.Left + r.Width / 2;
        var cy = r.Top + r.Height / 2;
        yield return (AnnotationHandle.TopLeft, r.TopLeft);
        yield return (AnnotationHandle.Top, new Point(cx, r.Top));
        yield return (AnnotationHandle.TopRight, r.TopRight);
        yield return (AnnotationHandle.Right, new Point(r.Right, cy));
        yield return (AnnotationHandle.BottomRight, r.BottomRight);
        yield return (AnnotationHandle.Bottom, new Point(cx, r.Bottom));
        yield return (AnnotationHandle.BottomLeft, r.BottomLeft);
        yield return (AnnotationHandle.Left, new Point(r.Left, cy));
    }

    protected static Rect ResizeBox(Rect r, AnnotationHandle handle, Vector d, bool constrain)
    {
        double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        switch (handle)
        {
            case AnnotationHandle.TopLeft: l += d.X; t += d.Y; break;
            case AnnotationHandle.Top: t += d.Y; break;
            case AnnotationHandle.TopRight: rt += d.X; t += d.Y; break;
            case AnnotationHandle.Right: rt += d.X; break;
            case AnnotationHandle.BottomRight: rt += d.X; b += d.Y; break;
            case AnnotationHandle.Bottom: b += d.Y; break;
            case AnnotationHandle.BottomLeft: l += d.X; b += d.Y; break;
            case AnnotationHandle.Left: l += d.X; break;
        }
        var res = new Rect(new Point(Math.Min(l, rt), Math.Min(t, b)), new Point(Math.Max(l, rt), Math.Max(t, b)));
        if (constrain && handle is AnnotationHandle.TopLeft or AnnotationHandle.TopRight or AnnotationHandle.BottomLeft or AnnotationHandle.BottomRight)
        {
            var size = Math.Max(res.Width, res.Height);
            res = new Rect(res.X, res.Y, size, size);
        }
        return res;
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

    public Point Start { get; protected set; }
    public Point End { get; protected set; }

    public override bool IsValid => Math.Abs(End.X - Start.X) >= 3 || Math.Abs(End.Y - Start.Y) >= 3;

    public Rect DragRect => new(Start, End);

    public override Rect Bounds => DragRect;

    public override void Update(Point point, bool constrain)
    {
        if (constrain && this is RectangleAnnotation or EllipseAnnotation or MosaicAnnotation or BlurAnnotation)
        {
            var size = Math.Max(Math.Abs(point.X - Start.X), Math.Abs(point.Y - Start.Y));
            point = new Point(Start.X + Math.Sign(point.X - Start.X) * size, Start.Y + Math.Sign(point.Y - Start.Y) * size);
        }
        else if (constrain)
        {
            point = Constrain45(Start, point);
        }
        End = point;
    }

    public override void ApplyDrag(Annotation baseline, AnnotationHandle handle, Vector delta, bool constrain)
    {
        var b = (DragAnnotation)baseline;
        if (handle == AnnotationHandle.Move)
        {
            Start = b.Start + delta;
            End = b.End + delta;
            OnGeometryChanged();
            return;
        }

        if (this is LineAnnotation or ArrowAnnotation)
        {
            if (handle == AnnotationHandle.StartPoint)
            {
                End = b.End;
                Start = constrain ? Constrain45(b.End, b.Start + delta) : b.Start + delta;
            }
            else if (handle == AnnotationHandle.EndPoint)
            {
                Start = b.Start;
                End = constrain ? Constrain45(b.Start, b.End + delta) : b.End + delta;
            }
            OnGeometryChanged();
            return;
        }

        var resized = ResizeBox(b.DragRect, handle, delta, constrain);
        Start = resized.TopLeft;
        End = resized.BottomRight;
        OnGeometryChanged();
    }

    protected virtual void OnGeometryChanged() { }
}

public sealed class RectangleAnnotation(Point start, Color color, double strokeWidth) : DragAnnotation(start, color, strokeWidth)
{
    public override AnnotationTool Kind => AnnotationTool.Rectangle;

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
        => dc.DrawRectangle(null, CreatePen(cap: PenLineCap.Square), DragRect);

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
    {
        var r = DragRect;
        var margin = StrokeWidth / 2 + tolerance;
        var outer = r;
        outer.Inflate(margin, margin);
        if (!outer.Contains(point)) return false;

        if (r.Width <= margin * 2 || r.Height <= margin * 2) return true;
        var inner = r;
        inner.Inflate(-margin, -margin);
        return !inner.Contains(point);
    }

    public override Annotation Clone() => new RectangleAnnotation(Start, Color, StrokeWidth) { End = End };
}

public sealed class EllipseAnnotation(Point start, Color color, double strokeWidth) : DragAnnotation(start, color, strokeWidth)
{
    public override AnnotationTool Kind => AnnotationTool.Ellipse;

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var r = DragRect;
        dc.DrawEllipse(null, CreatePen(), new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
    }

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
    {
        var r = DragRect;
        var margin = StrokeWidth / 2 + tolerance;
        var rx = r.Width / 2;
        var ry = r.Height / 2;
        if (rx < 1 || ry < 1) return DistanceToSegment(point, r.TopLeft, r.BottomRight) <= margin;

        var cx = r.X + rx;
        var cy = r.Y + ry;
        var dx = point.X - cx;
        var dy = point.Y - cy;

        var outerRx = rx + margin;
        var outerRy = ry + margin;
        if ((dx * dx) / (outerRx * outerRx) + (dy * dy) / (outerRy * outerRy) > 1.0) return false;

        var innerRx = Math.Max(0, rx - margin);
        var innerRy = Math.Max(0, ry - margin);
        if (innerRx <= 1 || innerRy <= 1) return true;
        return (dx * dx) / (innerRx * innerRx) + (dy * dy) / (innerRy * innerRy) >= 1.0;
    }

    public override Annotation Clone() => new EllipseAnnotation(Start, Color, StrokeWidth) { End = End };
}

public sealed class LineAnnotation(Point start, Color color, double strokeWidth) : DragAnnotation(start, color, strokeWidth)
{
    public override AnnotationTool Kind => AnnotationTool.Line;

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
        => dc.DrawLine(CreatePen(), Start, End);

    public override IEnumerable<(AnnotationHandle Handle, Point Center)> GetHandles()
    {
        yield return (AnnotationHandle.StartPoint, Start);
        yield return (AnnotationHandle.EndPoint, End);
    }

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
        => DistanceToSegment(point, Start, End) <= StrokeWidth / 2 + tolerance;

    public override Annotation Clone() => new LineAnnotation(Start, Color, StrokeWidth) { End = End };
}

public sealed class ArrowAnnotation(Point start, Color color, double strokeWidth) : DragAnnotation(start, color, strokeWidth)
{
    public override AnnotationTool Kind => AnnotationTool.Arrow;

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

    public override IEnumerable<(AnnotationHandle Handle, Point Center)> GetHandles()
    {
        yield return (AnnotationHandle.StartPoint, Start);
        yield return (AnnotationHandle.EndPoint, End);
    }

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
    {
        var headRadius = Math.Max(10, StrokeWidth * 3.0) + tolerance;
        if ((point - End).Length <= headRadius && DistanceToSegment(point, Start, End) <= headRadius) return true;
        return DistanceToSegment(point, Start, End) <= StrokeWidth / 2 + tolerance;
    }

    public override Annotation Clone() => new ArrowAnnotation(Start, Color, StrokeWidth) { End = End };
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

    private PenAnnotation(IEnumerable<Point> points, Color rawColor, double strokeWidth, bool highlighter)
        : base(rawColor, strokeWidth)
    {
        _highlighter = highlighter;
        _points.AddRange(points);
    }

    public override AnnotationTool Kind => _highlighter ? AnnotationTool.Highlighter : AnnotationTool.Pen;

    public override Color Color
    {
        get => base.Color;
        set => base.Color = _highlighter ? Color.FromArgb(110, value.R, value.G, value.B) : Color.FromArgb(255, value.R, value.G, value.B);
    }

    public override double GetSizeValue() => _highlighter ? StrokeWidth / 4.0 : StrokeWidth;

    public override void SetSizeValue(double value) => StrokeWidth = _highlighter ? Math.Max(1, value) * 4.0 : Math.Max(1, value);

    public override bool IsValid => _points.Count > 1;

    public override Rect Bounds
    {
        get
        {
            if (_points.Count == 0) return Rect.Empty;
            double minX = _points[0].X, minY = _points[0].Y, maxX = minX, maxY = minY;
            for (var i = 1; i < _points.Count; i++)
            {
                var p = _points[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            var r = new Rect(new Point(minX, minY), new Point(maxX, maxY));
            r.Inflate(StrokeWidth / 2, StrokeWidth / 2);
            return r;
        }
    }

    public override void Update(Point point, bool constrain)
    {
        if (constrain && _points.Count >= 1)
        {
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

    public override IEnumerable<(AnnotationHandle Handle, Point Center)> GetHandles()
    {
        var b = Bounds;
        if (b.IsEmpty) yield break;
        yield return (AnnotationHandle.TopLeft, b.TopLeft);
        yield return (AnnotationHandle.TopRight, b.TopRight);
        yield return (AnnotationHandle.BottomRight, b.BottomRight);
        yield return (AnnotationHandle.BottomLeft, b.BottomLeft);
    }

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
    {
        var threshold = StrokeWidth / 2 + tolerance;
        for (var i = 0; i < _points.Count - 1; i++)
        {
            if (DistanceToSegment(point, _points[i], _points[i + 1]) <= threshold) return true;
        }
        return false;
    }

    public override void ApplyDrag(Annotation baseline, AnnotationHandle handle, Vector delta, bool constrain)
    {
        var b = (PenAnnotation)baseline;
        _points.Clear();
        if (handle == AnnotationHandle.Move)
        {
            foreach (var p in b._points) _points.Add(p + delta);
            return;
        }

        var oldBox = b.Bounds;
        var newBox = ResizeBox(oldBox, handle, delta, constrain);
        var sx = oldBox.Width > 1 ? newBox.Width / oldBox.Width : 1.0;
        var sy = oldBox.Height > 1 ? newBox.Height / oldBox.Height : 1.0;
        foreach (var p in b._points)
        {
            _points.Add(new Point(
                newBox.X + (p.X - oldBox.X) * sx,
                newBox.Y + (p.Y - oldBox.Y) * sy));
        }
    }

    public override Annotation Clone() => new PenAnnotation(_points, base.Color, StrokeWidth, _highlighter);
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

    public override AnnotationTool Kind => AnnotationTool.Text;

    public Point Position { get; set; }
    public string Text { get; set; }
    public double FontSize { get; set; }
    public string FontFamily { get; set; }

    public override double GetSizeValue() => FontSize;

    public override void SetSizeValue(double value) => FontSize = Math.Clamp(value, 10, 96);

    public override bool IsValid => !string.IsNullOrWhiteSpace(Text);

    public override Rect Bounds
    {
        get
        {
            var ft = CreateFormattedText();
            return new Rect(Position.X, Position.Y, Math.Max(16, ft.WidthIncludingTrailingWhitespace), Math.Max(14, ft.Height));
        }
    }

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
        => dc.DrawText(CreateFormattedText(), Position);

    public override IEnumerable<(AnnotationHandle Handle, Point Center)> GetHandles()
    {
        var b = Bounds;
        yield return (AnnotationHandle.TopLeft, b.TopLeft);
        yield return (AnnotationHandle.TopRight, b.TopRight);
        yield return (AnnotationHandle.BottomRight, b.BottomRight);
        yield return (AnnotationHandle.BottomLeft, b.BottomLeft);
    }

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
    {
        var b = Bounds;
        b.Inflate(tolerance, tolerance);
        return b.Contains(point);
    }

    public override void ApplyDrag(Annotation baseline, AnnotationHandle handle, Vector delta, bool constrain)
    {
        var b = (TextAnnotation)baseline;
        if (handle == AnnotationHandle.Move)
        {
            Position = b.Position + delta;
            return;
        }

        var oldBox = b.Bounds;
        var newBox = ResizeBox(oldBox, handle, delta, constrain: true);
        var scale = oldBox.Height > 1 ? newBox.Height / oldBox.Height : 1.0;
        FontSize = Math.Clamp(b.FontSize * scale, 10, 96);
    }

    public override Annotation Clone() => new TextAnnotation(Position, Text, Color, FontSize, FontFamily);

    private FormattedText CreateFormattedText()
    {
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        return new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            FontSize, brush, 1.0);
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

    public override AnnotationTool Kind => AnnotationTool.Number;

    public Point Center { get; private set; }
    public int Number { get; }
    public double Size { get; private set; }

    public double Radius => Size * 0.75;

    public override double GetSizeValue() => Size;

    public override void SetSizeValue(double value) => Size = Math.Clamp(value, 10, 72);

    public override bool IsValid => true;

    public override Rect Bounds
    {
        get
        {
            var r = Radius;
            return new Rect(Center.X - r, Center.Y - r, r * 2, r * 2);
        }
    }

    public override void Update(Point point, bool constrain) => Center = point;

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var radius = Radius;
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

    public override IEnumerable<(AnnotationHandle Handle, Point Center)> GetHandles()
    {
        var b = Bounds;
        yield return (AnnotationHandle.TopLeft, b.TopLeft);
        yield return (AnnotationHandle.TopRight, b.TopRight);
        yield return (AnnotationHandle.BottomRight, b.BottomRight);
        yield return (AnnotationHandle.BottomLeft, b.BottomLeft);
    }

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
        => (point - Center).Length <= Radius + tolerance;

    public override void ApplyDrag(Annotation baseline, AnnotationHandle handle, Vector delta, bool constrain)
    {
        var b = (NumberAnnotation)baseline;
        if (handle == AnnotationHandle.Move)
        {
            Center = b.Center + delta;
            return;
        }

        var oldBox = b.Bounds;
        var newBox = ResizeBox(oldBox, handle, delta, constrain: true);
        var scale = oldBox.Width > 1 ? newBox.Width / oldBox.Width : 1.0;
        Size = Math.Clamp(b.Size * scale, 10, 72);
    }

    public override Annotation Clone() => new NumberAnnotation(Center, Number, Color, Size);
}

/// <summary>Pixelates the underlying image inside the rectangle.</summary>
public sealed class MosaicAnnotation(Point start, int blockSize) : DragAnnotation(start, Colors.Transparent, 0)
{
    private BitmapSource? _cache;
    private PixelRect _cacheRect;
    private int _cacheBlock;

    public override AnnotationTool Kind => AnnotationTool.Mosaic;

    public override bool SupportsColor => false;

    public int BlockSize { get; private set; } = Math.Max(2, blockSize);

    public override double GetSizeValue() => BlockSize;

    public override void SetSizeValue(double value)
    {
        BlockSize = Math.Max(2, (int)Math.Round(value));
        _cache = null;
    }

    protected override void OnGeometryChanged() => _cache = null;

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var r = DragRect;
        var rect = PixelRect.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom))
            .Intersect(context.BaseImage.Bounds);
        if (rect.IsEmpty) return;

        if (_cache is null || _cacheRect != rect || _cacheBlock != BlockSize)
        {
            _cache = Pixelate(context.BaseImage, rect, BlockSize);
            _cacheRect = rect;
            _cacheBlock = BlockSize;
        }
        dc.DrawImage(_cache, new Rect(rect.X, rect.Y, rect.Width, rect.Height));
    }

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
    {
        var r = DragRect;
        var outer = r;
        outer.Inflate(tolerance, tolerance);
        if (!outer.Contains(point)) return false;

        // When no drawing tool is active (or Mosaic/Blur is active), clicking anywhere inside selects the region;
        // when another tool (e.g. Rectangle/Arrow/Text) is active, only hit the border so the user can draw on top of the mosaic!
        if (activeTool is AnnotationTool.None or AnnotationTool.Mosaic or AnnotationTool.Blur) return true;
        var inner = r;
        inner.Inflate(-tolerance, -tolerance);
        return !inner.Contains(point);
    }

    public override Annotation Clone() => new MosaicAnnotation(Start, BlockSize) { End = End };

    private static BitmapSource Pixelate(Bgra32Image image, PixelRect rect, int block)
    {
        var result = new Bgra32Image(rect.Width, rect.Height);
        for (var by = 0; by < rect.Height; by += block)
        for (var bx = 0; bx < rect.Width; bx += block)
        {
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

/// <summary>Applies a smooth Gaussian blur to the underlying image inside the rectangle.</summary>
public sealed class BlurAnnotation(Point start, int blurRadius) : DragAnnotation(start, Colors.Transparent, 0)
{
    private BitmapSource? _cache;
    private PixelRect _cacheRect;
    private int _cacheRadius;

    public override AnnotationTool Kind => AnnotationTool.Blur;

    public override bool SupportsColor => false;

    public int BlurRadius { get; private set; } = Math.Clamp(blurRadius, 2, 64);

    public override double GetSizeValue() => BlurRadius;

    public override void SetSizeValue(double value)
    {
        BlurRadius = Math.Clamp((int)Math.Round(value), 2, 64);
        _cache = null;
    }

    protected override void OnGeometryChanged() => _cache = null;

    public override void Render(DrawingContext dc, AnnotationRenderContext context)
    {
        var r = DragRect;
        var rect = PixelRect.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom))
            .Intersect(context.BaseImage.Bounds);
        if (rect.IsEmpty) return;

        if (_cache is null || _cacheRect != rect || _cacheRadius != BlurRadius)
        {
            var blurred = GaussianBlurProcessor.BlurRegion(context.BaseImage, rect, BlurRadius);
            var bmp = BitmapSource.Create(blurred.Width, blurred.Height, 96, 96, PixelFormats.Bgra32, null, blurred.Pixels, blurred.Stride);
            bmp.Freeze();
            _cache = bmp;
            _cacheRect = rect;
            _cacheRadius = BlurRadius;
        }
        dc.DrawImage(_cache, new Rect(rect.X, rect.Y, rect.Width, rect.Height));
    }

    public override bool HitTest(Point point, double tolerance, AnnotationTool activeTool)
    {
        var r = DragRect;
        var outer = r;
        outer.Inflate(tolerance, tolerance);
        if (!outer.Contains(point)) return false;

        if (activeTool is AnnotationTool.None or AnnotationTool.Mosaic or AnnotationTool.Blur) return true;
        var inner = r;
        inner.Inflate(-tolerance, -tolerance);
        return !inner.Contains(point);
    }

    public override Annotation Clone() => new BlurAnnotation(Start, BlurRadius) { End = End };
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
        AnnotationTool.Blur => new BlurAnnotation(start, style.BlurRadius),
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
    public int BlurRadius { get; set; } = 12;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
}
