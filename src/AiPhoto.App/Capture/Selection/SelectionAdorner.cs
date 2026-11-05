using System.Windows;
using System.Windows.Media;

namespace AiPhoto.App.Capture.Selection;

public enum ResizeHandle
{
    None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Move,
}

/// <summary>
/// Draws the dimmed backdrop, the selection frame with resize handles and the window-snap highlight.
/// Lives inside the pixel-space canvas, so all coordinates are image pixels.
/// </summary>
public sealed class SelectionAdorner : FrameworkElement
{
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0x7C, 0xF6)));
    private static readonly Brush HandleFill = Brushes.White;

    public SelectionAdorner() => IsHitTestVisible = false;

    /// <summary>Device pixels per image pixel are 1, but UI chrome should keep a constant visual size: pass the DPI scale.</summary>
    public double DpiScale { get; set; } = 1;

    public Size CanvasSize { get; set; }

    /// <summary>Current selection (null while hovering).</summary>
    public Rect? Selection { get; set; }

    /// <summary>Highlighted window/monitor while hovering.</summary>
    public Rect? Hover { get; set; }

    public bool ShowHandles { get; set; }

    public double HandleSize => 7 * DpiScale;

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(CanvasSize);
        var hole = Selection ?? Hover;

        if (hole is { } h && !h.IsEmpty)
        {
            // Dim everything outside the hole (4 rectangles avoid expensive geometry ops on huge canvases).
            dc.DrawRectangle(DimBrush, null, new Rect(0, 0, full.Width, Math.Max(0, h.Top)));
            dc.DrawRectangle(DimBrush, null, new Rect(0, h.Bottom, full.Width, Math.Max(0, full.Height - h.Bottom)));
            dc.DrawRectangle(DimBrush, null, new Rect(0, h.Top, Math.Max(0, h.Left), h.Height));
            dc.DrawRectangle(DimBrush, null, new Rect(h.Right, h.Top, Math.Max(0, full.Width - h.Right), h.Height));

            var thickness = Math.Max(1, Math.Round(DpiScale * (Selection is null ? 2 : 1.5)));
            var pen = new Pen(AccentBrush, thickness);
            pen.Freeze();
            var inset = thickness / 2;
            dc.DrawRectangle(null, pen, new Rect(h.X - inset, h.Y - inset, h.Width + thickness, h.Height + thickness));

            if (Selection is not null && ShowHandles)
            {
                var handlePen = new Pen(AccentBrush, Math.Max(1, DpiScale));
                handlePen.Freeze();
                foreach (var (_, center) in HandleCenters(h))
                {
                    var s = HandleSize;
                    dc.DrawRectangle(HandleFill, handlePen, new Rect(center.X - s / 2, center.Y - s / 2, s, s));
                }
            }
        }
        else
        {
            dc.DrawRectangle(DimBrush, null, full);
        }
    }

    public ResizeHandle HitTestHandle(Point p)
    {
        if (Selection is not { } sel) return ResizeHandle.None;
        var tolerance = HandleSize * 1.2;
        foreach (var (handle, center) in HandleCenters(sel))
        {
            if (Math.Abs(p.X - center.X) <= tolerance && Math.Abs(p.Y - center.Y) <= tolerance) return handle;
        }
        // Edges are grabbable along their whole length.
        var edge = tolerance * 0.6;
        if (p.Y >= sel.Top && p.Y <= sel.Bottom)
        {
            if (Math.Abs(p.X - sel.Left) <= edge) return ResizeHandle.Left;
            if (Math.Abs(p.X - sel.Right) <= edge) return ResizeHandle.Right;
        }
        if (p.X >= sel.Left && p.X <= sel.Right)
        {
            if (Math.Abs(p.Y - sel.Top) <= edge) return ResizeHandle.Top;
            if (Math.Abs(p.Y - sel.Bottom) <= edge) return ResizeHandle.Bottom;
        }
        return sel.Contains(p) ? ResizeHandle.Move : ResizeHandle.None;
    }

    private static IEnumerable<(ResizeHandle, Point)> HandleCenters(Rect r)
    {
        var cx = r.Left + r.Width / 2;
        var cy = r.Top + r.Height / 2;
        yield return (ResizeHandle.TopLeft, r.TopLeft);
        yield return (ResizeHandle.Top, new Point(cx, r.Top));
        yield return (ResizeHandle.TopRight, r.TopRight);
        yield return (ResizeHandle.Right, new Point(r.Right, cy));
        yield return (ResizeHandle.BottomRight, r.BottomRight);
        yield return (ResizeHandle.Bottom, new Point(cx, r.Bottom));
        yield return (ResizeHandle.BottomLeft, r.BottomLeft);
        yield return (ResizeHandle.Left, new Point(r.Left, cy));
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
