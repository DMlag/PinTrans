using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Snapping;

/// <summary>
/// Thread-safe hierarchy index of snappable regions (monitors, top-level windows in Z-order, child HWNDs,
/// UI Automation subtrees, and on-demand image-detected visual boxes).
/// </summary>
public sealed class SnapIndex
{
    public const int MinCandidateSize = 6;
    public const int DedupeTolerancePixels = 3;

    private readonly object _gate = new();
    private readonly List<SnapCandidate> _monitors = [];
    private readonly List<SnapCandidate> _windows = [];
    private readonly Dictionary<int, List<SnapCandidate>> _childHwndsByWindow = [];
    private readonly Dictionary<int, List<SnapCandidate>> _uiaByWindow = [];
    private int _version;

    public SnapIndex(PixelRect canvasBounds)
    {
        CanvasBounds = canvasBounds;
    }

    public PixelRect CanvasBounds { get; }

    /// <summary>Incremented whenever asynchronous UI Automation candidates are added.</summary>
    public int Version => Volatile.Read(ref _version);

    public void AddMonitor(PixelRect rect, string? label = null)
    {
        var clipped = rect.Intersect(CanvasBounds);
        if (clipped.Width < MinCandidateSize || clipped.Height < MinCandidateSize) return;
        lock (_gate)
        {
            _monitors.Add(new SnapCandidate(clipped, SnapKind.Monitor, SnapSource.Monitor, Depth: 0, WindowIndex: -1, Label: label ?? "全屏"));
        }
    }

    /// <summary>
    /// Registers a top-level window in front-to-back Z-order (first added = topmost) along with its synchronous Win32 child HWNDs.
    /// Returns the assigned window index.
    /// </summary>
    public int AddWindow(PixelRect rect, string? title = null, IEnumerable<SnapCandidate>? childHwnds = null)
    {
        var clipped = rect.Intersect(CanvasBounds);
        lock (_gate)
        {
            var index = _windows.Count;
            var label = string.IsNullOrWhiteSpace(title) ? "窗口" : $"窗口 · {Truncate(title!, 28)}";
            _windows.Add(new SnapCandidate(clipped, SnapKind.Window, SnapSource.Window, Depth: 0, WindowIndex: index, Label: label));

            if (childHwnds is not null && !clipped.IsEmpty)
            {
                var list = new List<SnapCandidate>();
                foreach (var child in childHwnds)
                {
                    var cr = child.Rect.Intersect(clipped);
                    if (cr.Width < MinCandidateSize || cr.Height < MinCandidateSize) continue;
                    if (AreNearlyEqual(cr, clipped)) continue;
                    list.Add(child with { Rect = cr, WindowIndex = index });
                }
                if (list.Count > 0) _childHwndsByWindow[index] = list;
            }
            return index;
        }
    }

    /// <summary>Replaces the UI Automation candidate list for a window (called from the background UIA worker).</summary>
    public void SetWindowUiaCandidates(int windowIndex, IReadOnlyList<SnapCandidate> candidates)
    {
        lock (_gate)
        {
            if (windowIndex < 0 || windowIndex >= _windows.Count) return;
            var winRect = _windows[windowIndex].Rect;
            if (winRect.IsEmpty) return;

            var list = new List<SnapCandidate>(candidates.Count);
            foreach (var c in candidates)
            {
                var cr = c.Rect.Intersect(winRect);
                if (cr.Width < MinCandidateSize || cr.Height < MinCandidateSize) continue;
                if (AreNearlyEqual(cr, winRect)) continue;
                list.Add(c with { Rect = cr, WindowIndex = windowIndex, Source = SnapSource.UiAutomation });
            }
            _uiaByWindow[windowIndex] = list;
            Interlocked.Increment(ref _version);
        }
    }

    public bool HasUiaCandidates(int windowIndex)
    {
        lock (_gate) return _uiaByWindow.ContainsKey(windowIndex);
    }

    /// <summary>Returns the frontmost top-level window index containing <c>(x, y)</c>, or -1 if none.</summary>
    public int TopWindowIndexAt(int x, int y)
    {
        lock (_gate)
        {
            for (var i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].Rect.Contains(x, y)) return i;
            }
            return -1;
        }
    }

    public PixelRect GetWindowRect(int windowIndex)
    {
        lock (_gate)
        {
            return windowIndex >= 0 && windowIndex < _windows.Count ? _windows[windowIndex].Rect : default;
        }
    }

    /// <summary>
    /// Returns the hierarchy stack of snappable regions at canvas point <c>(x, y)</c>, ordered from smallest
    /// (innermost child control / text line) to largest (containing window, then monitor).
    /// </summary>
    public IReadOnlyList<SnapCandidate> Query(int x, int y, IReadOnlyList<SnapCandidate>? extraCandidates = null)
    {
        var raw = new List<SnapCandidate>(32);

        lock (_gate)
        {
            var topWin = -1;
            for (var i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].Rect.Contains(x, y))
                {
                    topWin = i;
                    break;
                }
            }

            if (topWin >= 0)
            {
                var win = _windows[topWin];
                raw.Add(win);

                // Only child controls of the frontmost window at (x, y) are visible; controls of background windows
                // are occluded by topWin at this cursor position.
                if (_uiaByWindow.TryGetValue(topWin, out var uiaList))
                {
                    foreach (var c in uiaList)
                    {
                        if (c.Rect.Contains(x, y)) raw.Add(c);
                    }
                }
                if (_childHwndsByWindow.TryGetValue(topWin, out var hwndList))
                {
                    foreach (var c in hwndList)
                    {
                        if (c.Rect.Contains(x, y)) raw.Add(c);
                    }
                }

                if (extraCandidates is not null)
                {
                    foreach (var extra in extraCandidates)
                    {
                        var cr = extra.Rect.Intersect(win.Rect);
                        if (cr.Width >= MinCandidateSize && cr.Height >= MinCandidateSize &&
                            cr.Contains(x, y) && !AreNearlyEqual(cr, win.Rect))
                        {
                            raw.Add(extra with { Rect = cr, WindowIndex = topWin });
                        }
                    }
                }

                // If topWin is a small popup/dialog inside a larger parent window behind it, allow scrolling up to the larger window.
                for (var i = topWin + 1; i < _windows.Count; i++)
                {
                    if (_windows[i].Rect.Contains(x, y) && _windows[i].Area > win.Area)
                    {
                        raw.Add(_windows[i]);
                    }
                }
            }
            else if (extraCandidates is not null)
            {
                foreach (var extra in extraCandidates)
                {
                    var cr = extra.Rect.Intersect(CanvasBounds);
                    if (cr.Width >= MinCandidateSize && cr.Height >= MinCandidateSize && cr.Contains(x, y))
                    {
                        raw.Add(extra with { Rect = cr });
                    }
                }
            }

            foreach (var m in _monitors)
            {
                if (m.Rect.Contains(x, y)) raw.Add(m);
            }
        }

        if (raw.Count <= 1) return raw;

        // Sort from smallest (most specific child) to largest (window -> monitor).
        raw.Sort(CompareHierarchy);

        // Deduplicate candidates with nearly identical bounds so wheel/Tab always visibly changes the selection.
        var result = new List<SnapCandidate>(raw.Count);
        foreach (var candidate in raw)
        {
            var merged = false;
            for (var i = 0; i < result.Count; i++)
            {
                if (AreNearlyEqual(result[i].Rect, candidate.Rect))
                {
                    result[i] = PickPreferred(result[i], candidate);
                    merged = true;
                    break;
                }
            }
            if (!merged) result.Add(candidate);
        }
        return result;
    }

    private static int CompareHierarchy(SnapCandidate a, SnapCandidate b)
    {
        // Monitors always go last, after top-level windows.
        var aIsMonitor = a.Kind == SnapKind.Monitor;
        var bIsMonitor = b.Kind == SnapKind.Monitor;
        if (aIsMonitor != bIsMonitor) return aIsMonitor ? 1 : -1;

        var areaCmp = a.Area.CompareTo(b.Area);
        if (areaCmp != 0) return areaCmp;

        var kindCmp = a.Kind.CompareTo(b.Kind);
        if (kindCmp != 0) return kindCmp;

        // Deeper node in the UI tree comes before its shallower wrapper of the same size.
        var depthCmp = b.Depth.CompareTo(a.Depth);
        if (depthCmp != 0) return depthCmp;

        return b.Source.CompareTo(a.Source);
    }

    private static SnapCandidate PickPreferred(SnapCandidate a, SnapCandidate b)
    {
        // Keep Window/Monitor exact bounds when a child panel covers the entire window/monitor.
        if (a.Kind is SnapKind.Window or SnapKind.Monitor && b.Kind is not (SnapKind.Window or SnapKind.Monitor)) return a;
        if (b.Kind is SnapKind.Window or SnapKind.Monitor && a.Kind is not (SnapKind.Window or SnapKind.Monitor)) return b;

        if (a.Source != b.Source) return a.Source > b.Source ? a : b;
        if (a.Kind != b.Kind) return a.Kind < b.Kind ? a : b;
        if (a.Depth != b.Depth) return a.Depth > b.Depth ? a : b;
        if (!string.IsNullOrWhiteSpace(b.Label) && string.IsNullOrWhiteSpace(a.Label)) return b;
        return a;
    }

    public static bool AreNearlyEqual(PixelRect a, PixelRect b, int tolerance = DedupeTolerancePixels)
        => Math.Abs(a.X - b.X) <= tolerance &&
           Math.Abs(a.Y - b.Y) <= tolerance &&
           Math.Abs(a.Right - b.Right) <= tolerance &&
           Math.Abs(a.Bottom - b.Bottom) <= tolerance;

    private static string Truncate(string text, int maxChars)
    {
        var clean = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= maxChars ? clean : clean[..maxChars] + "…";
    }
}
