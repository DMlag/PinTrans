using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using AiPhoto.App.Annotations;
using AiPhoto.App.Capture.Selection;
using AiPhoto.App.Diagnostics;
using AiPhoto.App.Imaging;
using AiPhoto.App.Services;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Ocr;
using AiPhoto.Core.Snapping;
using Microsoft.Win32;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.Capture;

/// <summary>
/// Full-screen frozen-desktop window: region selection (with window snapping and magnifier),
/// annotation editing and the action toolbar (copy, save, HDR, pin, translate, long capture).
/// </summary>
public partial class CaptureWindow : Window
{
    private const int WM_DPICHANGED = 0x02E0;
    private const int MagnifierCells = 15;

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0xFB, 0x8C, 0x00), Color.FromRgb(0xFD, 0xD8, 0x35),
        Color.FromRgb(0x43, 0xA0, 0x47), Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x8E, 0x24, 0xAA),
        Color.FromRgb(0x21, 0x21, 0x21), Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    /// <summary>Last used annotation style, kept for the lifetime of the app.</summary>
    private static AnnotationStyle? _sessionStyle;

    private readonly ScreenSnapshot _snapshot;
    private readonly CaptureIntent _mode;
    private readonly IAppHost _host;
    private readonly SnapMode _snapMode;
    private readonly SnapIndex _snapIndex;
    private readonly UiaSnapProvider? _uiaProvider;
    private readonly AnnotationDocument _document = new();
    private readonly AnnotationStyle _style;
    private readonly WriteableBitmap _magnifierBitmap = new(MagnifierCells, MagnifierCells, 96, 96, PixelFormats.Bgra32, null);
    private readonly Dictionary<AnnotationTool, ToggleButton> _toolButtons;

    private double _scale = 1;
    private Phase _phase = Phase.Hover;
    private Rect? _selection;
    private Rect? _hoverRect;
    private Rect? _displayedHoverRect;
    private bool _hoverAnimating;
    private long _lastAnimTick;
    private IReadOnlyList<SnapCandidate> _hoverCandidates = [];
    private int _hoverLevel;
    private bool _manualLevelOverride;
    private Point _manualLevelAnchor;
    private bool _selectionFromSnap;
    private (int X, int Y) _lastEdgePoint = (-99999, -99999);
    private IReadOnlyList<SnapCandidate>? _cachedEdgeCandidates;
    private Point _dragStart;
    private Rect _dragOrigin;
    private ResizeHandle _resizeHandle;
    private AnnotationTool _tool = AnnotationTool.None;
    private Annotation? _drawing;
    private Annotation? _editingAnnotation;
    private Annotation? _editingBaseline;
    private AnnotationHandle _editingHandle;
    private List<Annotation>? _editingUndoSnapshot;
    private bool _editingMoved;
    private TextAnnotation? _editingTextTarget;
    private Point _textPosition;
    private Point _lastMouse;
    private QrCodeResult? _detectedQr;
    private int _qrScanVersion;
    private bool _closing;

    private enum Phase { Hover, Dragging, Selected, Moving, Resizing, Drawing, EditingAnnotation }

    internal CaptureWindow(ScreenSnapshot snapshot, CaptureIntent mode, IAppHost host, IReadOnlyList<WindowSnapTarget> windows)
    {
        _snapshot = snapshot;
        _mode = mode;
        _host = host;
        InitializeComponent();

        var settings = host.Settings.Annotation;
        _style = _sessionStyle ??= new AnnotationStyle
        {
            Color = WpfImaging.ParseColor(settings.Color, Palette[0]),
            StrokeWidth = settings.StrokeWidth,
            FontSize = settings.FontSize,
            MosaicBlockSize = settings.MosaicBlockSize,
            BlurRadius = settings.BlurRadius,
            FontFamily = host.Settings.Translation.OverlayFontFamily,
        };

        var vb = snapshot.VirtualBounds;
        var canvasBounds = new PixelRect(0, 0, vb.Width, vb.Height);
        _snapMode = host.Settings.Capture.EffectiveSnapMode;
        _snapIndex = new SnapIndex(canvasBounds);

        var uiaTargets = new List<(int WindowIndex, IntPtr Hwnd, PixelRect CanvasBounds)>(windows.Count);
        if (_snapMode != SnapMode.Disabled)
        {
            foreach (var w in windows)
            {
                var winRect = w.ScreenBounds.Offset(-vb.X, -vb.Y).Intersect(canvasBounds);
                if (winRect.IsEmpty) continue;
                var childHwnds = _snapMode == SnapMode.Smart
                    ? w.ChildHwnds.Select(c => c with { Rect = c.Rect.Offset(-vb.X, -vb.Y) })
                    : null;
                var idx = _snapIndex.AddWindow(winRect, w.Title, childHwnds);
                if (w.Hwnd != IntPtr.Zero) uiaTargets.Add((idx, w.Hwnd, winRect));
            }
        }
        foreach (var m in snapshot.Monitors)
        {
            _snapIndex.AddMonitor(m.Bounds.Offset(-vb.X, -vb.Y), "全屏");
        }
        if (_snapMode == SnapMode.Smart)
        {
            _ = Task.Run(() => EdgeRectDetector.Prewarm(snapshot.Composite));
            if (uiaTargets.Count > 0)
            {
                _uiaProvider = new UiaSnapProvider(uiaTargets, vb.X, vb.Y, _snapIndex, OnUiaWindowPopulated);
            }
        }

        Backdrop.Source = snapshot.Composite.ToBitmapSource();
        Backdrop.Width = vb.Width;
        Backdrop.Height = vb.Height;
        PixelCanvas.Width = vb.Width;
        PixelCanvas.Height = vb.Height;
        Adorner.Width = vb.Width;
        Adorner.Height = vb.Height;
        Adorner.CanvasSize = new Size(vb.Width, vb.Height);
        Layer.Width = vb.Width;
        Layer.Height = vb.Height;
        Layer.Document = _document;
        Layer.RenderContext = new AnnotationRenderContext(snapshot.Composite);
        MagnifierImage.Source = _magnifierBitmap;
        Magnifier.Visibility = host.Settings.Capture.ShowMagnifier ? Visibility.Visible : Visibility.Collapsed;
        HdrButton.Visibility = snapshot.HasHdr ? Visibility.Visible : Visibility.Collapsed;

        _toolButtons = new()
        {
            [AnnotationTool.Rectangle] = ToolRectangle, [AnnotationTool.Ellipse] = ToolEllipse,
            [AnnotationTool.Arrow] = ToolArrow, [AnnotationTool.Line] = ToolLine, [AnnotationTool.Pen] = ToolPen,
            [AnnotationTool.Highlighter] = ToolHighlighter, [AnnotationTool.Text] = ToolText,
            [AnnotationTool.Mosaic] = ToolMosaic, [AnnotationTool.Blur] = ToolBlur,
            [AnnotationTool.Number] = ToolNumber,
        };
        _document.Changed += (_, _) =>
        {
            UpdateUndoButtons();
            BuildPropertyBar();
            if (ToolbarHost.IsVisible) ShowToolbar();
        };
        UpdateUndoButtons();

        TextEditor.PreviewKeyDown += OnTextEditorKeyDown;
        TextEditor.LostKeyboardFocus += (_, _) => CommitText();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseMove += OnMouseMoved;
        MouseLeftButtonUp += OnMouseLeftUp;
        MouseRightButtonUp += OnMouseRightUp;
        MouseWheel += OnMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDown;
        Deactivated += (_, _) => { if (!_closing && _phase == Phase.Hover) Activate(); };
        // Alt+F4 / system close bypass CloseCapture(): mark closing so Deactivated does not re-activate us.
        Closing += (_, _) =>
        {
            _closing = true;
            StopHoverAnimation();
            _uiaProvider?.Dispose();
        };
    }

    private void OnUiaWindowPopulated(int windowIndex)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _phase != Phase.Hover) return;
            var px = (int)_lastMouse.X;
            var py = (int)_lastMouse.Y;
            if (_snapIndex.TopWindowIndexAt(px, py) == windowIndex)
            {
                UpdateHover(_lastMouse, preserveLevel: _manualLevelOverride);
            }
        });
    }

    // ------------------------------------------------------------------ window setup

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        Infrastructure.PhysicalPlacement.DisableRoundedCorners(this);
        var vb = _snapshot.VirtualBounds;
        // Cover the entire virtual desktop in physical pixels, regardless of per-monitor DPI.
        SetWindowPos(hwnd, HWND_TOPMOST, vb.X, vb.Y, vb.Width, vb.Height, SWP_SHOWWINDOW);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The window spans monitors with different DPIs. Ignore DPI change notifications so WPF does not
        // resize it; we compensate for the single DPI scale WPF uses with PixelScale instead.
        if (msg == WM_DPICHANGED) handled = true;
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var source = PresentationSource.FromVisual(this);
        _scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        PixelScale.ScaleX = PixelScale.ScaleY = 1.0 / _scale;
        Adorner.DpiScale = _scale;
        Layer.DpiScale = _scale;

        // Re-apply the physical bounds in case WPF adjusted the size during its own initialisation.
        var vb = _snapshot.VirtualBounds;
        SetWindowPos(new WindowInteropHelper(this).Handle, HWND_TOPMOST, vb.X, vb.Y, vb.Width, vb.Height, SWP_SHOWWINDOW);

        Activate();
        Focus();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);

        if (GetCursorPos(out var pt))
        {
            var p = new Point(pt.X - vb.X, pt.Y - vb.Y);
            _lastMouse = p;
            UpdateHover(p);
            UpdateMagnifier(p);
        }
    }

    // ------------------------------------------------------------------ coordinates

    private Point CanvasPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(PixelCanvas);
        return new Point(Math.Clamp(Math.Round(p.X), 0, Backdrop.Width), Math.Clamp(Math.Round(p.Y), 0, Backdrop.Height));
    }

    private Point ToUi(Point canvas) => new(canvas.X / _scale, canvas.Y / _scale);

    /// <summary>
    /// UI-space (DIP) bounds of the monitor that contains a canvas point. The window covers the bounding box
    /// of all monitors, which contains off-screen areas when monitors differ in size or are offset, so floating
    /// UI (toolbar, magnifier, size label) must be kept inside the monitor, not merely inside the window.
    /// </summary>
    private Rect MonitorUiBounds(Point canvas)
    {
        var x = (int)Math.Clamp(canvas.X, 0, Math.Max(0, Backdrop.Width - 1));
        var y = (int)Math.Clamp(canvas.Y, 0, Math.Max(0, Backdrop.Height - 1));
        if (_snapshot.MonitorAt(x, y) is not { } monitor) return new Rect(0, 0, ActualWidth, ActualHeight);
        var vb = _snapshot.VirtualBounds;
        var r = monitor.Bounds.Offset(-vb.X, -vb.Y);
        return new Rect(ToUi(new Point(r.X, r.Y)), ToUi(new Point(r.Right, r.Bottom)));
    }

    private PixelRect SelectionPixels
    {
        get
        {
            var s = _selection ?? Rect.Empty;
            return PixelRect.FromLTRB((int)Math.Round(s.Left), (int)Math.Round(s.Top), (int)Math.Round(s.Right), (int)Math.Round(s.Bottom))
                .Intersect(_snapshot.Composite.Bounds);
        }
    }

    // ------------------------------------------------------------------ mouse

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnToolbar(e)) return;
        if (TextEditor.IsVisible) { CommitText(); return; }
        var p = CanvasPoint(e);

        if (_phase == Phase.Selected && _selection is { } sel && sel.Contains(p))
        {
            var (hitTarget, hitHandle) = _document.HitTest(p, 7 * _scale, 5 * _scale, _tool);
            if (hitTarget is not null)
            {
                if (e.ClickCount == 2 && hitTarget is TextAnnotation existingText)
                {
                    BeginTextEdit(existingText.Position, existingText);
                    return;
                }

                _document.Selected = hitTarget;
                _editingAnnotation = hitTarget;
                _editingBaseline = hitTarget.Clone();
                _editingHandle = hitHandle;
                _editingUndoSnapshot = _document.CreateSnapshot();
                _editingMoved = false;
                _dragStart = p;
                _phase = Phase.EditingAnnotation;
                CaptureMouse();
                return;
            }
        }

        if (_document.Selected is not null)
        {
            _document.Selected = null;
        }

        if (e.ClickCount == 2 && _phase == Phase.Selected && _tool == AnnotationTool.None && _selection!.Value.Contains(p))
        {
            CopyAndClose();
            return;
        }

        switch (_phase)
        {
            case Phase.Hover:
                _dragStart = p;
                _phase = Phase.Dragging;
                CaptureMouse();
                break;

            case Phase.Selected when _tool != AnnotationTool.None:
                if (!_selection!.Value.Contains(p)) break;
                if (_tool == AnnotationTool.Text)
                {
                    BeginTextEdit(p);
                    break;
                }
                _drawing = AnnotationFactory.Create(_tool, p, _style, _document.NextNumber);
                if (_drawing is null) break;
                Layer.Preview = _drawing;
                Layer.InvalidateVisual();
                _phase = Phase.Drawing;
                CaptureMouse();
                break;

            case Phase.Selected:
                var handle = Adorner.HitTestHandle(p);
                if (handle == ResizeHandle.None)
                {
                    if (!_document.IsEmpty) break;
                    // Start a fresh selection.
                    _selection = null;
                    _dragStart = p;
                    _phase = Phase.Dragging;
                    HideToolbar();
                    CaptureMouse();
                    break;
                }
                _dragStart = p;
                _dragOrigin = _selection!.Value;
                _resizeHandle = handle;
                _phase = handle == ResizeHandle.Move ? Phase.Moving : Phase.Resizing;
                HideToolbar();
                CaptureMouse();
                break;
        }
    }

    private void OnMouseMoved(object sender, MouseEventArgs e)
    {
        var p = CanvasPoint(e);
        _lastMouse = p;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (_phase)
        {
            case Phase.Hover:
                UpdateHover(p);
                break;

            case Phase.Dragging:
                if (Math.Abs(p.X - _dragStart.X) >= 3 || Math.Abs(p.Y - _dragStart.Y) >= 3 || _selection is not null)
                {
                    StopHoverAnimation();
                    _selection = new Rect(_dragStart, p);
                    Adorner.Selection = _selection;
                    Adorner.Hover = null;
                    Adorner.InvalidateVisual();
                    UpdateSizeLabel();
                }
                break;

            case Phase.Moving:
            {
                var dx = p.X - _dragStart.X;
                var dy = p.Y - _dragStart.Y;
                var r = _dragOrigin;
                var x = Math.Clamp(r.X + dx, 0, Backdrop.Width - r.Width);
                var y = Math.Clamp(r.Y + dy, 0, Backdrop.Height - r.Height);
                SetSelection(new Rect(x, y, r.Width, r.Height));
                break;
            }

            case Phase.Resizing:
                SetSelection(ResizeRect(_dragOrigin, _resizeHandle, p - _dragStart));
                break;

            case Phase.Drawing:
                _drawing?.Update(p, shift);
                Layer.InvalidateVisual();
                break;

            case Phase.EditingAnnotation:
                if (_editingAnnotation is not null && _editingBaseline is not null)
                {
                    var delta = p - _dragStart;
                    if (!_editingMoved && (Math.Abs(delta.X) >= 1 || Math.Abs(delta.Y) >= 1))
                    {
                        _editingMoved = true;
                    }
                    if (_editingMoved)
                    {
                        _editingAnnotation.ApplyDrag(_editingBaseline, _editingHandle, delta, shift);
                        Layer.InvalidateVisual();
                    }
                }
                break;

            case Phase.Selected:
                UpdateCursor(p);
                break;
        }

        if (_phase is Phase.Hover or Phase.Dragging or Phase.Resizing) UpdateMagnifier(p);
        else Magnifier.Visibility = Visibility.Collapsed;
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        switch (_phase)
        {
            case Phase.Dragging:
                var fromSnap = false;
                if (_selection is null || _selection.Value.Width < 3 || _selection.Value.Height < 3)
                {
                    // A click (no drag) selects the control/window/monitor under the cursor.
                    if (_hoverRect is null) { _phase = Phase.Hover; return; }
                    _selection = _hoverRect;
                    fromSnap = true;
                }
                SetSelection(_selection.Value, fromSnap);
                EnterSelected();
                break;

            case Phase.Moving:
            case Phase.Resizing:
                _phase = Phase.Selected;
                ScanSelectionQrCodeAsync();
                ShowToolbar();
                break;

            case Phase.Drawing:
                if (_drawing is { IsValid: true }) _document.Add(_drawing, select: true);
                _drawing = null;
                Layer.Preview = null;
                Layer.InvalidateVisual();
                _phase = Phase.Selected;
                break;

            case Phase.EditingAnnotation:
                if (_editingMoved && _editingUndoSnapshot is not null)
                {
                    _document.RecordSnapshot(_editingUndoSnapshot);
                }
                _editingAnnotation = null;
                _editingBaseline = null;
                _editingUndoSnapshot = null;
                _editingMoved = false;
                _phase = Phase.Selected;
                UpdateCursor(_lastMouse);
                break;
        }
    }

    private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (IsOnToolbar(e)) return;
        if (TextEditor.IsVisible) { CancelText(); return; }
        switch (_phase)
        {
            case Phase.Drawing:
                _drawing = null;
                Layer.Preview = null;
                Layer.InvalidateVisual();
                _phase = Phase.Selected;
                break;
            case Phase.EditingAnnotation:
                if (_editingAnnotation is not null && _editingBaseline is not null)
                {
                    _editingAnnotation.ApplyDrag(_editingBaseline, AnnotationHandle.Move, new Vector(0, 0), false);
                    Layer.InvalidateVisual();
                }
                _editingAnnotation = null;
                _editingBaseline = null;
                _editingUndoSnapshot = null;
                _editingMoved = false;
                _phase = Phase.Selected;
                break;
            case Phase.Selected when _document.Selected is not null:
                _document.Selected = null;
                break;
            case Phase.Selected when _tool != AnnotationTool.None:
                SelectTool(AnnotationTool.None);
                break;
            case Phase.Selected:
                ResetSelection();
                break;
            default:
                CloseCapture();
                break;
        }
    }

    private void EnterSelected()
    {
        _phase = Phase.Selected;
        Magnifier.Visibility = Visibility.Collapsed;
        switch (_mode)
        {
            case CaptureIntent.Translate:
                Pin(PinMode.Translate);
                return;
            case CaptureIntent.Ocr:
                Pin(PinMode.Ocr);
                return;
            case CaptureIntent.LongCapture:
                StartLongCapture();
                return;
        }
        ScanSelectionQrCodeAsync();
        ShowToolbar();
        UpdateCursor(_lastMouse);
    }

    private void ResetSelection()
    {
        _selection = null;
        _selectionFromSnap = false;
        _detectedQr = null;
        Interlocked.Increment(ref _qrScanVersion);
        QrBanner.Visibility = Visibility.Collapsed;
        _document.Clear();
        SelectTool(AnnotationTool.None);
        Adorner.Selection = null;
        Adorner.ShowHandles = false;
        Layer.ClipRect = null;
        HideToolbar();
        SizeLabel.Visibility = Visibility.Collapsed;
        _phase = Phase.Hover;
        Cursor = Cursors.Cross;
        UpdateHover(_lastMouse);
        if (_host.Settings.Capture.ShowMagnifier) Magnifier.Visibility = Visibility.Visible;
        UpdateMagnifier(_lastMouse);
    }

    private void SetSelection(Rect r, bool fromSnap = false)
    {
        r = Rect.Intersect(r, new Rect(0, 0, Backdrop.Width, Backdrop.Height));
        if (r.IsEmpty) return;
        StopHoverAnimation();
        _selectionFromSnap = fromSnap;
        _selection = new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));
        Adorner.Selection = _selection;
        Adorner.Hover = null;
        Adorner.ShowHandles = true;
        Adorner.InvalidateVisual();
        Layer.ClipRect = _selection;
        Layer.InvalidateVisual();
        UpdateSizeLabel();
        if (_snapshot.HasHdr) HdrButton.IsEnabled = _snapshot.IntersectsHdr(SelectionPixels);
    }

    private static Rect ResizeRect(Rect r, ResizeHandle handle, Vector d)
    {
        double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        switch (handle)
        {
            case ResizeHandle.TopLeft: l += d.X; t += d.Y; break;
            case ResizeHandle.Top: t += d.Y; break;
            case ResizeHandle.TopRight: rt += d.X; t += d.Y; break;
            case ResizeHandle.Right: rt += d.X; break;
            case ResizeHandle.BottomRight: rt += d.X; b += d.Y; break;
            case ResizeHandle.Bottom: b += d.Y; break;
            case ResizeHandle.BottomLeft: l += d.X; b += d.Y; break;
            case ResizeHandle.Left: l += d.X; break;
        }
        // Allow dragging an edge past the opposite one.
        return new Rect(new Point(Math.Min(l, rt), Math.Min(t, b)), new Point(Math.Max(l, rt), Math.Max(t, b)));
    }

    private void UpdateHover(Point p, bool preserveLevel = false)
    {
        var px = Math.Clamp((int)p.X, 0, Math.Max(0, _snapshot.Composite.Width - 1));
        var py = Math.Clamp((int)p.Y, 0, Math.Max(0, _snapshot.Composite.Height - 1));

        // Keep manual hierarchy level (from wheel/Tab) against tiny 1-3px hand jitter right before clicking.
        if (_manualLevelOverride)
        {
            if (Math.Abs(p.X - _manualLevelAnchor.X) < 4 && Math.Abs(p.Y - _manualLevelAnchor.Y) < 4)
            {
                preserveLevel = true;
            }
            else
            {
                _manualLevelOverride = false;
            }
        }

        IReadOnlyList<SnapCandidate>? edgeCandidates = null;
        if (_snapMode == SnapMode.Smart)
        {
            var topWin = _snapIndex.TopWindowIndexAt(px, py);
            if (topWin >= 0) _uiaProvider?.PrioritizeWindow(topWin);

            if (Math.Abs(px - _lastEdgePoint.X) >= 4 || Math.Abs(py - _lastEdgePoint.Y) >= 4 || _cachedEdgeCandidates is null)
            {
                _lastEdgePoint = (px, py);
                var clip = topWin >= 0 ? _snapIndex.GetWindowRect(topWin) : _snapIndex.CanvasBounds;
                _cachedEdgeCandidates = EdgeRectDetector.DetectAroundPoint(_snapshot.Composite, px, py, clip);
            }
            edgeCandidates = _cachedEdgeCandidates;
        }

        var prevRect = _hoverCandidates.Count > 0 && _hoverLevel < _hoverCandidates.Count
            ? _hoverCandidates[_hoverLevel].Rect
            : default;

        _hoverCandidates = _snapIndex.Query(px, py, edgeCandidates);
        Rect? targetHover;
        if (_hoverCandidates.Count == 0)
        {
            _hoverLevel = 0;
            targetHover = null;
        }
        else
        {
            if (preserveLevel && !prevRect.IsEmpty)
            {
                var match = -1;
                for (var i = 0; i < _hoverCandidates.Count; i++)
                {
                    if (SnapIndex.AreNearlyEqual(_hoverCandidates[i].Rect, prevRect)) { match = i; break; }
                }
                _hoverLevel = match >= 0 ? match : Math.Clamp(_hoverLevel, 0, _hoverCandidates.Count - 1);
            }
            else
            {
                _hoverLevel = 0;
            }
            targetHover = _hoverCandidates[_hoverLevel].Rect.ToRect();
        }

        SetHoverTarget(targetHover);
    }

    private void SetHoverTarget(Rect? target)
    {
        _hoverRect = target;
        if (target is null)
        {
            _displayedHoverRect = null;
            StopHoverAnimation();
            Adorner.Hover = null;
            Adorner.InvalidateVisual();
            UpdateSizeLabel();
            return;
        }

        if (_displayedHoverRect is not { } cur)
        {
            _displayedHoverRect = target;
            Adorner.Hover = _displayedHoverRect;
            Adorner.InvalidateVisual();
            UpdateSizeLabel();
            return;
        }

        if (IsRectClose(cur, target.Value))
        {
            _displayedHoverRect = target;
            StopHoverAnimation();
            Adorner.Hover = _displayedHoverRect;
            Adorner.InvalidateVisual();
            UpdateSizeLabel();
            return;
        }

        UpdateSizeLabel();
        if (!_hoverAnimating)
        {
            _hoverAnimating = true;
            _lastAnimTick = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnHoverAnimationFrame;
        }
    }

    private void StopHoverAnimation()
    {
        if (!_hoverAnimating) return;
        _hoverAnimating = false;
        CompositionTarget.Rendering -= OnHoverAnimationFrame;
    }

    private void OnHoverAnimationFrame(object? sender, EventArgs e)
    {
        if (_closing || _phase != Phase.Hover || _hoverRect is not { } target || _displayedHoverRect is not { } cur)
        {
            StopHoverAnimation();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var dt = Math.Clamp((double)(now - _lastAnimTick) / Stopwatch.Frequency, 0.001, 0.05);
        _lastAnimTick = now;

        // Frame-rate-independent exponential smoothing (~28/s decay rate -> smooth 60/120/144/240Hz glide).
        var factor = 1.0 - Math.Exp(-dt * 28.0);
        var l = cur.Left + (target.Left - cur.Left) * factor;
        var t = cur.Top + (target.Top - cur.Top) * factor;
        var r = cur.Right + (target.Right - cur.Right) * factor;
        var b = cur.Bottom + (target.Bottom - cur.Bottom) * factor;
        var next = new Rect(new Point(Math.Min(l, r), Math.Min(t, b)), new Point(Math.Max(l, r), Math.Max(t, b)));

        if (IsRectClose(next, target))
        {
            _displayedHoverRect = target;
            StopHoverAnimation();
        }
        else
        {
            _displayedHoverRect = next;
        }

        Adorner.Hover = _displayedHoverRect;
        Adorner.InvalidateVisual();
        UpdateSizeLabelPosition(_displayedHoverRect.Value);
    }

    private static bool IsRectClose(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) < 0.6 &&
        Math.Abs(a.Top - b.Top) < 0.6 &&
        Math.Abs(a.Right - b.Right) < 0.6 &&
        Math.Abs(a.Bottom - b.Bottom) < 0.6;

    /// <summary>
    /// Steps up (<c>+1</c> = parent/outer region) or down (<c>-1</c> = child/inner region) in the snap hierarchy stack.
    /// Supported via both mouse wheel and <c>Tab</c> / <c>Shift+Tab</c>.
    /// </summary>
    private bool StepHierarchyLevel(int delta)
    {
        if (_hoverCandidates.Count <= 1) return false;

        if (_phase == Phase.Hover)
        {
            _hoverLevel = Math.Clamp(_hoverLevel + delta, 0, _hoverCandidates.Count - 1);
            _manualLevelOverride = true;
            _manualLevelAnchor = _lastMouse;
            SetHoverTarget(_hoverCandidates[_hoverLevel].Rect.ToRect());
            return true;
        }

        if (_phase == Phase.Selected && _selectionFromSnap && _tool == AnnotationTool.None && _document.IsEmpty)
        {
            _hoverLevel = Math.Clamp(_hoverLevel + delta, 0, _hoverCandidates.Count - 1);
            SetSelection(_hoverCandidates[_hoverLevel].Rect.ToRect(), fromSnap: true);
            ScanSelectionQrCodeAsync();
            ShowToolbar();
            return true;
        }

        return false;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TextEditor.IsVisible || e.Delta == 0) return;
        // Wheel up (e.Delta > 0) expands to parent container; wheel down (e.Delta < 0) shrinks to child control.
        if (StepHierarchyLevel(e.Delta > 0 ? +1 : -1))
        {
            e.Handled = true;
        }
    }

    private bool IsOnToolbar(RoutedEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, ToolbarHost)) return true;
        }
        return false;
    }

    private void UpdateCursor(Point p)
    {
        if (_selection?.Contains(p) == true)
        {
            var (hitTarget, hitHandle) = _document.HitTest(p, 7 * _scale, 5 * _scale, _tool);
            if (hitTarget is not null)
            {
                Cursor = hitHandle switch
                {
                    AnnotationHandle.TopLeft or AnnotationHandle.BottomRight => Cursors.SizeNWSE,
                    AnnotationHandle.TopRight or AnnotationHandle.BottomLeft => Cursors.SizeNESW,
                    AnnotationHandle.Top or AnnotationHandle.Bottom => Cursors.SizeNS,
                    AnnotationHandle.Left or AnnotationHandle.Right => Cursors.SizeWE,
                    AnnotationHandle.StartPoint or AnnotationHandle.EndPoint => Cursors.Cross,
                    _ => Cursors.SizeAll,
                };
                return;
            }
        }

        if (_tool != AnnotationTool.None)
        {
            Cursor = _selection?.Contains(p) == true ? (_tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross) : Cursors.Arrow;
            return;
        }
        Cursor = Adorner.HitTestHandle(p) switch
        {
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
            ResizeHandle.Move => Cursors.SizeAll,
            _ => _document.IsEmpty ? Cursors.Cross : Cursors.Arrow,
        };
    }

    // ------------------------------------------------------------------ magnifier & labels

    private void UpdateMagnifier(Point p)
    {
        if (!_host.Settings.Capture.ShowMagnifier) return;
        Magnifier.Visibility = Visibility.Visible;

        var img = _snapshot.Composite;
        var cx = (int)p.X;
        var cy = (int)p.Y;
        var half = MagnifierCells / 2;
        var pixels = new byte[MagnifierCells * MagnifierCells * 4];
        for (var y = 0; y < MagnifierCells; y++)
        for (var x = 0; x < MagnifierCells; x++)
        {
            var sx = cx - half + x;
            var sy = cy - half + y;
            var o = (y * MagnifierCells + x) * 4;
            if (sx < 0 || sy < 0 || sx >= img.Width || sy >= img.Height) { pixels[o + 3] = 255; continue; }
            var c = img.GetPixel(sx, sy);
            pixels[o] = c.B; pixels[o + 1] = c.G; pixels[o + 2] = c.R; pixels[o + 3] = 255;
        }
        _magnifierBitmap.WritePixels(new Int32Rect(0, 0, MagnifierCells, MagnifierCells), pixels, MagnifierCells * 4, 0);

        var color = cx >= 0 && cy >= 0 && cx < img.Width && cy < img.Height ? img.GetPixel(cx, cy) : default;
        MagnifierSwatch.Background = new SolidColorBrush(color.ToColor());
        MagnifierColor.Text = $"{color}  RGB({color.R},{color.G},{color.B})";
        MagnifierPos.Text = _selection is { } s && _phase != Phase.Hover
            ? $"{(int)s.Width} × {(int)s.Height}"
            : $"X {cx + _snapshot.VirtualBounds.X}  Y {cy + _snapshot.VirtualBounds.Y}";

        // Keep the magnifier next to the cursor but inside the cursor's monitor.
        Magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var ui = ToUi(p);
        var size = Magnifier.DesiredSize;
        var monitor = MonitorUiBounds(p);
        var x0 = ui.X + 20;
        var y0 = ui.Y + 20;
        if (x0 + size.Width > monitor.Right) x0 = ui.X - 20 - size.Width;
        if (y0 + size.Height > monitor.Bottom) y0 = ui.Y - 20 - size.Height;
        Canvas.SetLeft(Magnifier, Math.Max(monitor.Left, x0));
        Canvas.SetTop(Magnifier, Math.Max(monitor.Top, y0));
    }

    private void UpdateSizeLabel()
    {
        Rect anchor;
        string text;

        if (_phase == Phase.Hover)
        {
            if (_hoverRect is not { } hr || _hoverCandidates.Count == 0)
            {
                SizeLabel.Visibility = Visibility.Collapsed;
                return;
            }
            anchor = _displayedHoverRect ?? hr;
            var cand = _hoverCandidates[Math.Clamp(_hoverLevel, 0, _hoverCandidates.Count - 1)];
            text = _hoverCandidates.Count > 1
                ? $"{cand.DisplayLabel}  {(int)hr.Width} × {(int)hr.Height}  ({_hoverLevel + 1}/{_hoverCandidates.Count} · 滚轮/Tab 切换)"
                : $"{cand.DisplayLabel}  {(int)hr.Width} × {(int)hr.Height}";
        }
        else if (_selection is { } s)
        {
            anchor = s;
            if (_selectionFromSnap && _hoverCandidates.Count > 0 && _hoverLevel < _hoverCandidates.Count)
            {
                var cand = _hoverCandidates[_hoverLevel];
                text = _hoverCandidates.Count > 1
                    ? $"{cand.DisplayLabel}  {(int)s.Width} × {(int)s.Height}  ({_hoverLevel + 1}/{_hoverCandidates.Count})"
                    : $"{cand.DisplayLabel}  {(int)s.Width} × {(int)s.Height}";
            }
            else
            {
                text = $"{(int)s.Width} × {(int)s.Height}";
            }
        }
        else
        {
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        SizeText.Text = text;
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        UpdateSizeLabelPosition(anchor);
    }

    private void UpdateSizeLabelPosition(Rect anchor)
    {
        if (SizeLabel.Visibility != Visibility.Visible) return;
        var ui = ToUi(anchor.TopLeft);
        var monitor = MonitorUiBounds(anchor.TopLeft);
        var left = Math.Clamp(ui.X, monitor.Left + 4, Math.Max(monitor.Left + 4, monitor.Right - SizeLabel.DesiredSize.Width - 4));
        var top = ui.Y - SizeLabel.DesiredSize.Height - 4;
        Canvas.SetLeft(SizeLabel, left);
        Canvas.SetTop(SizeLabel, top < monitor.Top + 4 ? ui.Y + 4 : top);
    }

    // ------------------------------------------------------------------ toolbar

    private void ShowToolbar()
    {
        if (_selection is not { } s) return;
        ToolbarHost.Visibility = Visibility.Visible;
        ToolbarHost.UpdateLayout();
        ToolbarHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ToolbarHost.DesiredSize;
        var topLeft = ToUi(s.TopLeft);
        var bottomRight = ToUi(s.BottomRight);
        // The toolbar hangs off the selection's bottom-right corner: keep it on that corner's monitor.
        var monitor = MonitorUiBounds(new Point(s.Right - 1, s.Bottom - 1));

        var x = bottomRight.X - size.Width;
        var y = bottomRight.Y + 8;
        if (y + size.Height > monitor.Bottom)
        {
            y = topLeft.Y - size.Height - 8;                      // above the selection
            if (y < monitor.Top) y = bottomRight.Y - size.Height - 8; // inside, bottom
        }
        x = Math.Clamp(x, monitor.Left + 4, Math.Max(monitor.Left + 4, monitor.Right - size.Width - 4));
        Canvas.SetLeft(ToolbarHost, x);
        Canvas.SetTop(ToolbarHost, Math.Max(monitor.Top + 4, y));
    }

    private void HideToolbar() => ToolbarHost.Visibility = Visibility.Collapsed;

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        var tool = Enum.Parse<AnnotationTool>(tag);
        SelectTool(_tool == tool ? AnnotationTool.None : tool);
    }

    private void SelectTool(AnnotationTool tool)
    {
        CommitText();
        _tool = tool;
        if (tool != AnnotationTool.None && _document.Selected is { } sel && sel.Kind != tool)
        {
            _document.Selected = null;
        }
        foreach (var (t, button) in _toolButtons) button.IsChecked = t == tool;
        Adorner.ShowHandles = tool == AnnotationTool.None;
        Adorner.InvalidateVisual();
        BuildPropertyBar();
        if (ToolbarHost.IsVisible) ShowToolbar();
        UpdateCursor(_lastMouse);
    }

    private void BuildPropertyBar()
    {
        SizePanel.Children.Clear();
        ColorPanel.Children.Clear();
        var selected = _document.Selected;
        var activeKind = selected?.Kind ?? _tool;
        if (activeKind == AnnotationTool.None)
        {
            PropertyBar.Visibility = Visibility.Collapsed;
            return;
        }
        PropertyBar.Visibility = Visibility.Visible;

        DeleteSeparator.Visibility = selected is not null ? Visibility.Visible : Visibility.Collapsed;
        DeleteSelectedButton.Visibility = selected is not null ? Visibility.Visible : Visibility.Collapsed;

        // Size options depend on the tool: stroke width, font size, mosaic block size, or blur radius.
        (double Value, double Dot)[] sizes = activeKind switch
        {
            AnnotationTool.Text or AnnotationTool.Number => [(14, 4), (20, 7), (30, 10)],
            AnnotationTool.Mosaic => [(6, 4), (10, 7), (18, 10)],
            AnnotationTool.Blur => [(6, 4), (12, 7), (22, 10)],
            _ => [(2, 4), (4, 7), (8, 10)],
        };
        double current = selected is not null
            ? selected.GetSizeValue()
            : activeKind switch
            {
                AnnotationTool.Text or AnnotationTool.Number => _style.FontSize,
                AnnotationTool.Mosaic => _style.MosaicBlockSize,
                AnnotationTool.Blur => _style.BlurRadius,
                _ => _style.StrokeWidth,
            };
        var nearest = sizes.MinBy(s => Math.Abs(s.Value - current)).Value;
        foreach (var (value, dot) in sizes)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("SizeButton"),
                GroupName = "size",
                IsChecked = value == nearest,
                Content = new Ellipse { Width = dot, Height = dot, Fill = Brushes.White },
                ToolTip = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            rb.Checked += (_, _) =>
            {
                switch (activeKind)
                {
                    case AnnotationTool.Text or AnnotationTool.Number: _style.FontSize = value; break;
                    case AnnotationTool.Mosaic: _style.MosaicBlockSize = (int)value; break;
                    case AnnotationTool.Blur: _style.BlurRadius = (int)value; break;
                    default: _style.StrokeWidth = value; break;
                }
                if (_document.Selected is { } s && s.Kind == activeKind && Math.Abs(s.GetSizeValue() - value) > 0.01)
                {
                    _document.MutateSelected(a => a.SetSizeValue(value));
                }
            };
            SizePanel.Children.Add(rb);
        }

        var colorVisible = activeKind is not (AnnotationTool.Mosaic or AnnotationTool.Blur);
        ColorSeparator.Visibility = colorVisible ? Visibility.Visible : Visibility.Collapsed;
        ColorPanel.Visibility = colorVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!colorVisible) return;

        var activeColor = selected is { SupportsColor: true } sc
            ? Color.FromRgb(sc.Color.R, sc.Color.G, sc.Color.B)
            : _style.Color;
        foreach (var color in Palette)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("SwatchButton"),
                GroupName = "color",
                Background = new SolidColorBrush(color),
                IsChecked = color == activeColor,
            };
            rb.Checked += (_, _) =>
            {
                _style.Color = color;
                if (_document.Selected is { SupportsColor: true } s)
                {
                    var curRgb = Color.FromRgb(s.Color.R, s.Color.G, s.Color.B);
                    if (curRgb != color)
                    {
                        _document.MutateSelected(a => a.Color = color);
                    }
                }
            };
            ColorPanel.Children.Add(rb);
        }
    }

    private void OnDeleteSelectedAnnotation(object sender, RoutedEventArgs e)
    {
        _document.RemoveSelected();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _document.CanUndo;
        RedoButton.IsEnabled = _document.CanRedo;
    }

    private void OnUndo(object sender, RoutedEventArgs e) => _document.Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => _document.Redo();
    private void OnCancel(object sender, RoutedEventArgs e) => CloseCapture();
    private void OnCopy(object sender, RoutedEventArgs e) => CopyAndClose();
    private void OnSave(object sender, RoutedEventArgs e) => SaveAs();
    private void OnSaveHdr(object sender, RoutedEventArgs e) => SaveHdr();
    private void OnPin(object sender, RoutedEventArgs e) => Pin(PinMode.None);
    private void OnTranslate(object sender, RoutedEventArgs e) => Pin(PinMode.Translate);
    private void OnOcr(object sender, RoutedEventArgs e) => Pin(PinMode.Ocr);
    private void OnLongCapture(object sender, RoutedEventArgs e) => StartLongCapture();

    // ------------------------------------------------------------------ QR banner

    private async void ScanSelectionQrCodeAsync()
    {
        var version = Interlocked.Increment(ref _qrScanVersion);
        var rect = SelectionPixels;
        if (rect.Width < 16 || rect.Height < 16)
        {
            _detectedQr = null;
            QrBanner.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var crop = _snapshot.CropSdr(rect);
            var codes = await Task.Run(() => QrCodeScanner.Scan(crop));
            if (_closing || version != _qrScanVersion || _phase == Phase.Hover) return;

            if (codes.Count == 0)
            {
                _detectedQr = null;
                QrBanner.Visibility = Visibility.Collapsed;
            }
            else
            {
                _detectedQr = codes[0];
                QrBannerBadge.Text = _detectedQr.Format;
                QrBannerText.Text = _detectedQr.Text.Replace('\r', ' ').Replace('\n', ' ');
                QrBannerOpenButton.Visibility = _detectedQr.IsHttpUrl ? Visibility.Visible : Visibility.Collapsed;
                QrBanner.Visibility = Visibility.Visible;
            }

            if (ToolbarHost.IsVisible) ShowToolbar();
        }
        catch (Exception ex)
        {
            Log.Warn($"QR scan failed: {ex.Message}");
        }
    }

    private void OnCopyQrBanner(object sender, RoutedEventArgs e)
    {
        if (_detectedQr is null) return;
        WpfImaging.CopyTextToClipboard(_detectedQr.Text);
        _host.Notify($"已复制{_detectedQr.Format}内容", _detectedQr.Text);
        CloseCapture();
    }

    private void OnOpenQrBannerUrl(object sender, RoutedEventArgs e)
    {
        if (_detectedQr is null || !QrCodeScanner.TryGetHttpUri(_detectedQr.Text, out var uri) || uri is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            CloseCapture();
        }
        catch (Exception ex)
        {
            Log.Warn($"Open QR URL failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ text tool

    private void BeginTextEdit(Point p, TextAnnotation? existing = null)
    {
        _editingTextTarget = existing;
        _textPosition = existing?.Position ?? p;
        var ui = ToUi(_textPosition);
        var fontSize = existing?.FontSize ?? _style.FontSize;
        var fontFamily = existing?.FontFamily ?? _style.FontFamily;
        var color = existing?.Color ?? _style.Color;

        TextEditor.Text = existing?.Text ?? "";
        TextEditor.FontSize = fontSize / _scale;
        TextEditor.FontFamily = new FontFamily(fontFamily);
        TextEditor.FontWeight = FontWeights.SemiBold;
        TextEditor.Foreground = new SolidColorBrush(color);
        Canvas.SetLeft(TextEditor, ui.X - 1);
        Canvas.SetTop(TextEditor, ui.Y - 1);
        TextEditor.Visibility = Visibility.Visible;
        TextEditor.Focus();
        TextEditor.SelectAll();
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelText();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CommitText();
            e.Handled = true;
        }
    }

    private void CommitText()
    {
        if (!TextEditor.IsVisible) return;
        var text = TextEditor.Text;
        var editing = _editingTextTarget;
        _editingTextTarget = null;
        TextEditor.Visibility = Visibility.Collapsed;

        if (editing is not null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _document.Selected = editing;
                _document.RemoveSelected();
            }
            else if (editing.Text != text)
            {
                _document.Selected = editing;
                _document.MutateSelected(a => ((TextAnnotation)a).Text = text);
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            _document.Add(new TextAnnotation(_textPosition, text, _style.Color, _style.FontSize, _style.FontFamily), select: true);
        }
        Focus();
    }

    private void CancelText()
    {
        _editingTextTarget = null;
        TextEditor.Visibility = Visibility.Collapsed;
        Focus();
    }

    // ------------------------------------------------------------------ keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TextEditor.IsVisible) return;
        if (e.Key == Key.Tab)
        {
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            // Tab expands to parent container; Shift+Tab shrinks to child control.
            if (StepHierarchyLevel(shift ? -1 : +1))
            {
                e.Handled = true;
            }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (TextEditor.IsVisible) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.Escape: CloseCapture(); break;
            case Key.Delete or Key.Back when _document.Selected is not null:
                _document.RemoveSelected();
                break;
            case Key.Enter when _selection is not null: CopyAndClose(); break;
            case Key.C when ctrl && _selection is not null: CopyAndClose(); break;
            case Key.S when ctrl && _selection is not null: SaveAs(); break;
            case Key.T when ctrl && _selection is not null: Pin(PinMode.Translate); break;
            case Key.O when ctrl && _selection is not null: Pin(PinMode.Ocr); break;
            case Key.P when ctrl && _selection is not null: Pin(PinMode.None); break;
            case Key.Z when ctrl && shift: _document.Redo(); break;
            case Key.Z when ctrl: _document.Undo(); break;
            case Key.Y when ctrl: _document.Redo(); break;
            case Key.C when _phase == Phase.Hover:
                var c = _snapshot.Composite.GetPixel(Math.Clamp((int)_lastMouse.X, 0, _snapshot.Composite.Width - 1),
                    Math.Clamp((int)_lastMouse.Y, 0, _snapshot.Composite.Height - 1));
                WpfImaging.CopyTextToClipboard(c.ToString());
                _host.Notify("已复制颜色", c.ToString());
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                Nudge(e.Key, shift);
                break;
            default:
                if (_phase == Phase.Selected && !ctrl) HandleToolShortcut(e.Key);
                return;
        }
        e.Handled = true;
    }

    private void HandleToolShortcut(Key key)
    {
        AnnotationTool? tool = key switch
        {
            Key.R => AnnotationTool.Rectangle, Key.E => AnnotationTool.Ellipse, Key.A => AnnotationTool.Arrow,
            Key.L => AnnotationTool.Line, Key.P => AnnotationTool.Pen, Key.H => AnnotationTool.Highlighter,
            Key.T => AnnotationTool.Text, Key.M => AnnotationTool.Mosaic, Key.B => AnnotationTool.Blur,
            Key.N => AnnotationTool.Number,
            _ => null,
        };
        if (tool is { } t) SelectTool(_tool == t ? AnnotationTool.None : t);
    }

    /// <summary>Arrow keys: move the cursor by one pixel while hovering; nudge the selected annotation or selection.</summary>
    private void Nudge(Key key, bool shift)
    {
        var (dx, dy) = key switch { Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), _ => (0, 1) };
        if (_phase == Phase.Selected && _document.Selected is { } selAnn)
        {
            var step = shift ? 5 : 1;
            var baseline = selAnn.Clone();
            _document.MutateSelected(a => a.ApplyDrag(baseline, AnnotationHandle.Move, new Vector(dx * step, dy * step), false));
            return;
        }
        if (_phase == Phase.Selected && _selection is { } s)
        {
            SetSelection(shift
                ? new Rect(s.X, s.Y, Math.Max(1, s.Width + dx), Math.Max(1, s.Height + dy))
                : new Rect(Math.Clamp(s.X + dx, 0, Backdrop.Width - s.Width), Math.Clamp(s.Y + dy, 0, Backdrop.Height - s.Height), s.Width, s.Height));
            ShowToolbar();
        }
        else if (GetCursorPos(out var pt))
        {
            SetCursorPos(pt.X + dx, pt.Y + dy);
        }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>The selected region with annotations burned in, at native pixel resolution.</summary>
    private Bgra32Image ComposeResult()
    {
        CommitText();
        var rect = SelectionPixels;
        var crop = _snapshot.CropSdr(rect);
        if (_document.IsEmpty) return crop;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(crop.ToBitmapSource(), new Rect(0, 0, rect.Width, rect.Height));
            dc.PushTransform(new TranslateTransform(-rect.X, -rect.Y));
            _document.RenderAll(dc, Layer.RenderContext!);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(rect.Width, rect.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb.ToBgra32Image();
    }

    private PixelRect SelectionOnScreen => SelectionPixels.Offset(_snapshot.VirtualBounds.X, _snapshot.VirtualBounds.Y);

    private void CopyAndClose()
    {
        if (_selection is null) return;
        var image = ComposeResult();
        WpfImaging.CopyToClipboard(image.ToBitmapSource());
        AutoSaveHdrIfEnabled();
        CloseCapture();
    }

    private void SaveAs()
    {
        if (_selection is null) return;
        var image = ComposeResult();
        var suggested = _host.SuggestFilePath();
        var hdrAvailable = _snapshot.IntersectsHdr(SelectionPixels);
        var dialog = new SaveFileDialog
        {
            InitialDirectory = Path.GetDirectoryName(suggested),
            FileName = Path.GetFileName(suggested),
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp" + (hdrAvailable ? "|HDR JPEG XR (不含标注)|*.jxr" : ""),
            DefaultExt = ".png",
            AddExtension = true,
        };

        Topmost = false;
        var ok = dialog.ShowDialog(this) == true;
        Topmost = true;
        if (!ok) return;

        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".jxr", StringComparison.OrdinalIgnoreCase))
            {
                JxrEncoder.Save(_snapshot.CropHdr(SelectionPixels)!, dialog.FileName);
            }
            else
            {
                WpfImaging.Save(image.ToBitmapSource(), dialog.FileName);
                if (_host.Settings.Capture.CopyAfterSave) WpfImaging.CopyToClipboard(image.ToBitmapSource());
                AutoSaveHdrIfEnabled(Path.ChangeExtension(dialog.FileName, ".jxr"));
            }
            CloseCapture();
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            _host.Notify("保存失败", ex.Message, isError: true);
        }
    }

    private void SaveHdr()
    {
        if (_selection is null) return;
        var hdr = _snapshot.CropHdr(SelectionPixels);
        if (hdr is null)
        {
            _host.Notify("无 HDR 数据", "所选区域不在 HDR 显示器上。");
            return;
        }
        var suggested = _host.SuggestFilePath("_HDR");
        var dialog = new SaveFileDialog
        {
            InitialDirectory = Path.GetDirectoryName(suggested),
            FileName = Path.GetFileName(suggested),
            Filter = "HDR JPEG XR|*.jxr",
            DefaultExt = ".jxr",
            AddExtension = true,
        };
        Topmost = false;
        var ok = dialog.ShowDialog(this) == true;
        Topmost = true;
        if (!ok) return;

        try
        {
            JxrEncoder.Save(hdr, dialog.FileName);
            _host.Notify("已保存 HDR 截图", dialog.FileName);
            CloseCapture();
        }
        catch (Exception ex)
        {
            Log.Error("HDR save failed", ex);
            _host.Notify("HDR 保存失败", ex.Message, isError: true);
        }
    }

    private void AutoSaveHdrIfEnabled(string? path = null)
    {
        if (!_host.Settings.Capture.AutoSaveHdr || !_snapshot.IntersectsHdr(SelectionPixels)) return;
        try
        {
            var hdr = _snapshot.CropHdr(SelectionPixels)!;
            path ??= _host.SuggestFilePath("_HDR") + ".jxr";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            JxrEncoder.Save(hdr, path);
        }
        catch (Exception ex)
        {
            Log.Error("Auto HDR save failed", ex);
        }
    }

    private void Pin(PinMode mode)
    {
        if (_selection is null) return;
        var image = ComposeResult();
        var screenRect = SelectionOnScreen;
        CloseCapture();
        _host.ShowPin(image, screenRect, mode);
    }

    private void StartLongCapture()
    {
        if (_selection is null) return;
        var screenRect = SelectionOnScreen;
        CloseCapture();
        _host.StartLongCapture(screenRect);
    }

    private void CloseCapture()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
