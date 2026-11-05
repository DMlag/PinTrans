using System.Windows;
using System.Windows.Media;

namespace AiPhoto.App.Annotations;

/// <summary>Ordered list of annotations with undo/redo.</summary>
public sealed class AnnotationDocument
{
    private readonly List<Annotation> _items = [];
    private readonly Stack<Annotation> _redo = new();

    public IReadOnlyList<Annotation> Items => _items;

    public bool CanUndo => _items.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsEmpty => _items.Count == 0;

    public event EventHandler? Changed;

    public int NextNumber => _items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;

    public void Add(Annotation annotation)
    {
        _items.Add(annotation);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_items.Count == 0) return;
        _redo.Push(_items[^1]);
        _items.RemoveAt(_items.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _items.Add(_redo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _items.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RenderAll(DrawingContext dc, AnnotationRenderContext context)
    {
        foreach (var a in _items) a.Render(dc, context);
    }
}

/// <summary>Renders the annotation document plus the shape currently being drawn, clipped to the selection.</summary>
public sealed class AnnotationLayer : FrameworkElement
{
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
        if (ClipRect is not null) dc.Pop();
    }
}
