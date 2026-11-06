using System.Windows;
using System.Windows.Media;

namespace AiPhoto.App.Annotations;

/// <summary>
/// Ordered list of vector annotations with secondary selection, hit-testing, and full state undo/redo
/// (covering creation, move/resize, style changes, text edits, and deletion).
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<Annotation> _items = [];
    private readonly Stack<List<Annotation>> _undo = new();
    private readonly Stack<List<Annotation>> _redo = new();
    private Annotation? _selected;

    public IReadOnlyList<Annotation> Items => _items;

    public Annotation? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value is not null && _items.Contains(value) ? value : null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsEmpty => _items.Count == 0;

    public event EventHandler? Changed;

    public int NextNumber => _items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Captures a clone of the current document state before an in-place mutation.</summary>
    public List<Annotation> CreateSnapshot() => _items.Select(a => a.Clone()).ToList();

    public void RecordSnapshot(List<Annotation> beforeSnapshot)
    {
        _undo.Push(beforeSnapshot);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(Annotation annotation, bool select = false)
    {
        _undo.Push(CreateSnapshot());
        _redo.Clear();
        _items.Add(annotation);
        _selected = select ? annotation : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveSelected()
    {
        if (_selected is null) return false;
        var idx = _items.IndexOf(_selected);
        if (idx < 0)
        {
            _selected = null;
            return false;
        }
        _undo.Push(CreateSnapshot());
        _redo.Clear();
        _items.RemoveAt(idx);
        _selected = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void MutateSelected(Action<Annotation> mutator)
    {
        if (_selected is null) return;
        _undo.Push(CreateSnapshot());
        _redo.Clear();
        mutator(_selected);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public (Annotation? Target, AnnotationHandle Handle) HitTest(
        Point point,
        double handleTolerance,
        double bodyTolerance,
        AnnotationTool activeTool)
    {
        // 1. Check handles and body of the currently selected annotation first.
        if (_selected is not null && _items.Contains(_selected))
        {
            var h = _selected.HitTestHandle(point, handleTolerance, bodyTolerance, AnnotationTool.None);
            if (h != AnnotationHandle.None) return (_selected, h);
        }

        // 2. Check all annotations from frontmost (last drawn) to backmost.
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.HitTest(point, bodyTolerance, activeTool))
            {
                return (item, AnnotationHandle.Move);
            }
        }

        return (null, AnnotationHandle.None);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(CreateSnapshot());
        Restore(_undo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(CreateSnapshot());
        Restore(_redo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _items.Clear();
        _undo.Clear();
        _redo.Clear();
        _selected = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RenderAll(DrawingContext dc, AnnotationRenderContext context)
    {
        foreach (var a in _items) a.Render(dc, context);
    }

    private void Restore(List<Annotation> state)
    {
        _items.Clear();
        foreach (var a in state) _items.Add(a.Clone());
        _selected = null;
    }
}

/// <summary>
/// Renders the annotation document, the shape currently being drawn, and the interactive selection handles
/// of the currently selected annotation, clipped to the selection rectangle.
/// </summary>
public sealed class AnnotationLayer : FrameworkElement
{
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0x7C, 0xF6)));
    private static readonly Brush HandleFill = Brushes.White;

    private AnnotationDocument? _document;

    public AnnotationDocument? Document
    {
        get => _document;
        set
        {
            if (_document is not null) _document.Changed -= OnChanged;
            _document = value;
            if (_document is not null) _document.Changed += OnChanged;
            InvalidateVisual();
        }
    }

    public AnnotationRenderContext? RenderContext { get; set; }

    /// <summary>Shape being drawn right now (not yet part of the document).</summary>
    public Annotation? Preview { get; set; }

    public Rect? ClipRect { get; set; }

    public double DpiScale { get; set; } = 1.0;

    public double HandleSize => 6.5 * DpiScale;

    public AnnotationLayer()
    {
        IsHitTestVisible = false;
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (RenderContext is null) return;
        if (ClipRect is { } clip) dc.PushClip(new RectangleGeometry(clip));

        _document?.RenderAll(dc, RenderContext);
        Preview?.Render(dc, RenderContext);

        if (_document?.Selected is { } sel && Preview is null)
        {
            RenderSelectionGizmo(dc, sel);
        }

        if (ClipRect is not null) dc.Pop();
    }

    private void RenderSelectionGizmo(DrawingContext dc, Annotation sel)
    {
        var thickness = Math.Max(1, DpiScale * 1.2);
        if (sel is not (LineAnnotation or ArrowAnnotation))
        {
            var bounds = sel.Bounds;
            if (!bounds.IsEmpty)
            {
                bounds.Inflate(2 * DpiScale, 2 * DpiScale);
                var dashPen = new Pen(AccentBrush, thickness)
                {
                    DashStyle = new DashStyle([4, 3], 0),
                };
                dashPen.Freeze();
                dc.DrawRectangle(null, dashPen, bounds);
            }
        }

        var handlePen = new Pen(AccentBrush, Math.Max(1, DpiScale * 1.2));
        handlePen.Freeze();
        var hs = HandleSize;
        foreach (var (_, center) in sel.GetHandles())
        {
            dc.DrawRectangle(HandleFill, handlePen, new Rect(center.X - hs / 2, center.Y - hs / 2, hs, hs));
        }
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
