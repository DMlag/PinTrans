using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AiPhoto.App.Diagnostics;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Snapping;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.Capture.Selection;

/// <summary>
/// Background MTA worker that queries Windows UI Automation (<c>UIAutomationCore.dll</c> / <c>CUIAutomation8</c>)
/// using batch <c>CacheRequest</c> calls so modern UI controls (WPF, WinUI, Qt, Electron, Chrome/Edge DOM, WinForms)
/// can be snapped without ever blocking the UI thread.
/// </summary>
internal sealed unsafe class UiaSnapProvider : IDisposable
{
    private const int MaxElementsPerWindow = 2500;
    private const int MaxTreeDepth = 36;
    private const int BackgroundPrefetchWindows = 6;

    // UIA property IDs (UIAutomationClient.h)
    private const int UIA_BoundingRectanglePropertyId = 30001;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_NamePropertyId = 30005;
    private const int UIA_IsOffscreenPropertyId = 30022;

    // TreeScope flags
    private const int TreeScope_Element = 0x1;
    private const int TreeScope_Children = 0x2;
    private const int TreeScope_Descendants = 0x4;
    private const int TreeScope_Subtree = TreeScope_Element | TreeScope_Children | TreeScope_Descendants;

    // AutomationElementMode
    private const int AutomationElementMode_None = 0;

    // COM CLSIDs & IIDs
    private static readonly Guid ClsidCUIAutomation8 = new("E22AD333-B25F-460C-83D0-0581107395C9");
    private static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    private static readonly Guid IidIUIAutomation2 = new("34723AFF-0C9D-49D0-9896-7AB52DF8CD8A");

    private const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    private readonly IReadOnlyList<(int WindowIndex, IntPtr Hwnd, PixelRect CanvasBounds)> _windows;
    private readonly int _virtualOriginX;
    private readonly int _virtualOriginY;
    private readonly SnapIndex _index;
    private readonly Action<int>? _onWindowPopulated;
    private readonly ConcurrentDictionary<int, byte> _processed = new();
    private readonly AutoResetEvent _wakeEvent = new(false);
    private volatile int _priorityWindowIndex = -1;
    private volatile bool _disposed;

    public UiaSnapProvider(
        IReadOnlyList<(int WindowIndex, IntPtr Hwnd, PixelRect CanvasBounds)> windows,
        int virtualOriginX,
        int virtualOriginY,
        SnapIndex index,
        Action<int>? onWindowPopulated = null)
    {
        _windows = windows;
        _virtualOriginX = virtualOriginX;
        _virtualOriginY = virtualOriginY;
        _index = index;
        _onWindowPopulated = onWindowPopulated;

        if (_windows.Count > 0)
        {
            var thread = new Thread(WorkerLoop)
            {
                Name = "AiPhoto.UiaSnapWorker",
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
    }

    /// <summary>Requests that the window with <paramref name="windowIndex"/> be inspected next by the background thread.</summary>
    public void PrioritizeWindow(int windowIndex)
    {
        if (_disposed || windowIndex < 0) return;
        if (_processed.ContainsKey(windowIndex)) return;
        _priorityWindowIndex = windowIndex;
        try { _wakeEvent.Set(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _wakeEvent.Set(); } catch (ObjectDisposedException) { }
    }

    private void WorkerLoop()
    {
        IntPtr uia = IntPtr.Zero;
        IntPtr controlCondition = IntPtr.Zero;
        IntPtr subtreeCacheReq = IntPtr.Zero;
        IntPtr flatCacheReq = IntPtr.Zero;

        try
        {
            var hr = CoCreateInstance(in ClsidCUIAutomation8, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IidIUIAutomation, out uia);
            if (hr < 0 || uia == IntPtr.Zero)
            {
                hr = CoCreateInstance(in ClsidCUIAutomation, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IidIUIAutomation, out uia);
                if (hr < 0 || uia == IntPtr.Zero) return;
            }

            // Configure short timeouts on IUIAutomation2 so an unresponsive window cannot stall the worker thread.
            if (Marshal.QueryInterface(uia, in IidIUIAutomation2, out var uia2) >= 0 && uia2 != IntPtr.Zero)
            {
                try
                {
                    VtblPutUInt(uia2, slot: 61, 150); // put_ConnectionTimeout (150 ms)
                    VtblPutUInt(uia2, slot: 63, 250); // put_TransactionTimeout (250 ms)
                }
                finally
                {
                    Marshal.Release(uia2);
                }
            }

            // Slot 18 of IUIAutomation: get_ControlViewCondition (falls back to Slot 21: CreateTrueCondition)
            if (VtblGetPtr(uia, slot: 18, out controlCondition) < 0 || controlCondition == IntPtr.Zero)
            {
                if (VtblGetPtr(uia, slot: 21, out controlCondition) < 0 || controlCondition == IntPtr.Zero) return;
            }

            subtreeCacheReq = CreateConfiguredCacheRequest(uia, controlCondition, TreeScope_Subtree);
            flatCacheReq = CreateConfiguredCacheRequest(uia, controlCondition, TreeScope_Element);
            if (subtreeCacheReq == IntPtr.Zero || flatCacheReq == IntPtr.Zero) return;

            while (!_disposed)
            {
                if (!TryPickNextWindow(out var target))
                {
                    _wakeEvent.WaitOne(200);
                    continue;
                }

                if (!_processed.TryAdd(target.WindowIndex, 1)) continue;
                if (_disposed) break;

                try
                {
                    var candidates = InspectWindow(uia, target.Hwnd, target.CanvasBounds, controlCondition, subtreeCacheReq, flatCacheReq);
                    if (_disposed) break;
                    _index.SetWindowUiaCandidates(target.WindowIndex, candidates);
                    _onWindowPopulated?.Invoke(target.WindowIndex);
                }
                catch (Exception ex)
                {
                    Log.Warn($"UIA inspect window 0x{target.Hwnd:X} failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"UIA snap worker exited: {ex.Message}");
        }
        finally
        {
            ReleaseCom(ref flatCacheReq);
            ReleaseCom(ref subtreeCacheReq);
            ReleaseCom(ref controlCondition);
            ReleaseCom(ref uia);
            try { _wakeEvent.Dispose(); } catch { }
        }
    }

    private bool TryPickNextWindow(out (int WindowIndex, IntPtr Hwnd, PixelRect CanvasBounds) target)
    {
        var prio = _priorityWindowIndex;
        if (prio >= 0 && !_processed.ContainsKey(prio))
        {
            for (var i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].WindowIndex == prio)
                {
                    target = _windows[i];
                    return true;
                }
            }
        }

        var limit = Math.Min(_windows.Count, BackgroundPrefetchWindows);
        for (var i = 0; i < limit; i++)
        {
            if (!_processed.ContainsKey(_windows[i].WindowIndex))
            {
                target = _windows[i];
                return true;
            }
        }
        target = default;
        return false;
    }

    private static IntPtr CreateConfiguredCacheRequest(IntPtr uia, IntPtr filterCondition, int treeScope)
    {
        // Slot 20 of IUIAutomation: CreateCacheRequest
        if (VtblGetPtr(uia, slot: 20, out var req) < 0 || req == IntPtr.Zero) return IntPtr.Zero;

        // IUIAutomationCacheRequest slots:
        // 3 = AddProperty, 7 = put_TreeScope, 9 = put_TreeFilter, 11 = put_AutomationElementMode
        VtblCallInt(req, slot: 3, UIA_BoundingRectanglePropertyId);
        VtblCallInt(req, slot: 3, UIA_ControlTypePropertyId);
        VtblCallInt(req, slot: 3, UIA_NamePropertyId);
        VtblCallInt(req, slot: 3, UIA_IsOffscreenPropertyId);
        VtblCallInt(req, slot: 7, treeScope);
        VtblCallPtr(req, slot: 9, filterCondition);
        VtblCallInt(req, slot: 11, AutomationElementMode_None);
        return req;
    }

    private List<SnapCandidate> InspectWindow(
        IntPtr uia,
        IntPtr hwnd,
        PixelRect windowCanvasBounds,
        IntPtr controlCondition,
        IntPtr subtreeCacheReq,
        IntPtr flatCacheReq)
    {
        var list = new List<SnapCandidate>(128);

        // First try single-IPC subtree cache: Slot 10 of IUIAutomation = ElementFromHandleBuildCache(hwnd, cacheRequest, out root)
        if (VtblElementFromHandleBuildCache(uia, hwnd, subtreeCacheReq, out var cachedRoot) >= 0 && cachedRoot != IntPtr.Zero)
        {
            try
            {
                WalkCachedSubtree(cachedRoot, windowCanvasBounds, list);
            }
            finally
            {
                Marshal.Release(cachedRoot);
            }
        }

        // Fallback if the provider did not populate recursive CachedChildren on ElementFromHandleBuildCache:
        // Slot 6 of IUIAutomation = ElementFromHandle(hwnd, out liveRoot)
        // Slot 8 of IUIAutomationElement = FindAllBuildCache(scope, condition, cacheRequest, out array)
        if (list.Count == 0 && !_disposed && VtblElementFromHandle(uia, hwnd, out var liveRoot) >= 0 && liveRoot != IntPtr.Zero)
        {
            try
            {
                if (VtblFindAllBuildCache(liveRoot, TreeScope_Descendants, controlCondition, flatCacheReq, out var arr) >= 0 && arr != IntPtr.Zero)
                {
                    try
                    {
                        ReadElementArray(arr, depth: 2, windowCanvasBounds, list, childStack: null);
                    }
                    finally
                    {
                        Marshal.Release(arr);
                    }
                }
            }
            finally
            {
                Marshal.Release(liveRoot);
            }
        }

        return list;
    }

    private void WalkCachedSubtree(IntPtr rootElement, PixelRect windowCanvasBounds, List<SnapCandidate> list)
    {
        var stack = new Stack<(IntPtr Element, int Depth)>();
        // Slot 19 of IUIAutomationElement: GetCachedChildren(out IUIAutomationElementArray)
        if (VtblGetPtr(rootElement, slot: 19, out var rootChildren) >= 0 && rootChildren != IntPtr.Zero)
        {
            try
            {
                ReadElementArray(rootChildren, depth: 1, windowCanvasBounds, list, stack);
            }
            finally
            {
                Marshal.Release(rootChildren);
            }
        }

        while (stack.Count > 0)
        {
            var (el, depth) = stack.Pop();
            try
            {
                if (_disposed || list.Count >= MaxElementsPerWindow || depth >= MaxTreeDepth) continue;
                if (VtblGetPtr(el, slot: 19, out var children) >= 0 && children != IntPtr.Zero)
                {
                    try
                    {
                        ReadElementArray(children, depth + 1, windowCanvasBounds, list, stack);
                    }
                    finally
                    {
                        Marshal.Release(children);
                    }
                }
            }
            finally
            {
                Marshal.Release(el);
            }
        }
    }

    private void ReadElementArray(
        IntPtr elementArray,
        int depth,
        PixelRect windowCanvasBounds,
        List<SnapCandidate> list,
        Stack<(IntPtr Element, int Depth)>? childStack)
    {
        // Slot 3 of IUIAutomationElementArray: get_Length(out int length)
        if (VtblGetInt(elementArray, slot: 3, out var length) < 0 || length <= 0) return;

        var count = Math.Min(length, MaxElementsPerWindow - list.Count);
        for (var i = 0; i < count; i++)
        {
            if (_disposed) break;
            // Slot 4 of IUIAutomationElementArray: GetElement(int index, out IUIAutomationElement)
            if (VtblGetArrayElement(elementArray, i, out var el) < 0 || el == IntPtr.Zero) continue;

            var pushedToStack = false;
            try
            {
                if (TryExtractCandidate(el, depth, windowCanvasBounds, out var candidate))
                {
                    list.Add(candidate);
                }

                if (childStack is not null && depth < MaxTreeDepth && list.Count < MaxElementsPerWindow)
                {
                    childStack.Push((el, depth));
                    pushedToStack = true;
                }
            }
            finally
            {
                if (!pushedToStack) Marshal.Release(el);
            }
        }
    }

    private bool TryExtractCandidate(IntPtr el, int depth, PixelRect windowCanvasBounds, out SnapCandidate candidate)
    {
        candidate = default;

        // Slot 70 of IUIAutomationElement: get_CachedIsOffscreen(out BOOL)
        if (VtblGetInt(el, slot: 70, out var isOffscreen) >= 0 && isOffscreen != 0)
            return false;

        // Slot 75 of IUIAutomationElement: get_CachedBoundingRectangle(out RECT)
        if (VtblGetRect(el, slot: 75, out var rawRect) < 0)
            return false;

        if (rawRect.Width < SnapIndex.MinCandidateSize || rawRect.Height < SnapIndex.MinCandidateSize)
            return false;

        var canvasRect = PixelRect.FromLTRB(
            rawRect.Left - _virtualOriginX,
            rawRect.Top - _virtualOriginY,
            rawRect.Right - _virtualOriginX,
            rawRect.Bottom - _virtualOriginY).Intersect(windowCanvasBounds);

        if (canvasRect.Width < SnapIndex.MinCandidateSize || canvasRect.Height < SnapIndex.MinCandidateSize)
            return false;
        if (SnapIndex.AreNearlyEqual(canvasRect, windowCanvasBounds))
            return false;

        // Slot 53 of IUIAutomationElement: get_CachedControlType(out int)
        VtblGetInt(el, slot: 53, out var controlType);

        // Slot 55 of IUIAutomationElement: get_CachedName(out BSTR)
        string? name = null;
        if (VtblGetPtr(el, slot: 55, out var bstrName) >= 0 && bstrName != IntPtr.Zero)
        {
            try { name = Marshal.PtrToStringBSTR(bstrName); }
            finally { Marshal.FreeBSTR(bstrName); }
        }

        var (kind, typeLabel) = ClassifyUiaControlType(controlType);
        var label = WindowFinder.BuildControlLabel(typeLabel, name);
        candidate = new SnapCandidate(canvasRect, kind, SnapSource.UiAutomation, Depth: depth, Label: label);
        return true;
    }

    private static (SnapKind Kind, string Label) ClassifyUiaControlType(int controlTypeId) => controlTypeId switch
    {
        50000 => (SnapKind.Control, "按钮"),
        50001 => (SnapKind.Control, "日历"),
        50002 => (SnapKind.Control, "复选框"),
        50003 => (SnapKind.Control, "下拉框"),
        50004 => (SnapKind.Control, "输入框"),
        50005 => (SnapKind.Control, "链接"),
        50006 => (SnapKind.Control, "图片"),
        50007 => (SnapKind.Control, "列表项"),
        50008 => (SnapKind.Panel, "列表"),
        50009 => (SnapKind.Panel, "菜单"),
        50010 => (SnapKind.Panel, "菜单栏"),
        50011 => (SnapKind.Control, "菜单项"),
        50012 => (SnapKind.Control, "进度条"),
        50013 => (SnapKind.Control, "单选框"),
        50014 => (SnapKind.Control, "滚动条"),
        50015 => (SnapKind.Control, "滑块"),
        50016 => (SnapKind.Control, "微调框"),
        50017 => (SnapKind.Panel, "状态栏"),
        50018 => (SnapKind.Panel, "标签栏"),
        50019 => (SnapKind.Control, "标签页"),
        50020 => (SnapKind.TextBlock, "文本"),
        50021 => (SnapKind.Panel, "工具栏"),
        50022 => (SnapKind.Panel, "提示框"),
        50023 => (SnapKind.Panel, "树视图"),
        50024 => (SnapKind.Control, "树节点"),
        50025 => (SnapKind.Control, "控件"),
        50026 => (SnapKind.Panel, "分组"),
        50027 => (SnapKind.Control, "滑块"),
        50028 => (SnapKind.Panel, "数据表"),
        50029 => (SnapKind.Control, "数据行"),
        50030 => (SnapKind.Panel, "文档"),
        50031 => (SnapKind.Control, "按钮"),
        50032 => (SnapKind.Panel, "子窗口"),
        50033 => (SnapKind.Panel, "面板"),
        50034 => (SnapKind.Panel, "表头"),
        50035 => (SnapKind.Control, "表头项"),
        50036 => (SnapKind.Panel, "表格"),
        50037 => (SnapKind.Panel, "标题栏"),
        50038 => (SnapKind.Control, "分隔线"),
        _ => (SnapKind.Control, "控件"),
    };

    // ------------------------------------------------------------------ low-level COM vtable helpers

    private static void ReleaseCom(ref IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.Release(ptr);
            ptr = IntPtr.Zero;
        }
    }

    private static int VtblCallInt(IntPtr comObj, int slot, int arg)
    {
        var vtbl = *(IntPtr**)comObj;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[slot];
        return fn(comObj, arg);
    }

    private static int VtblPutUInt(IntPtr comObj, int slot, uint arg)
    {
        var vtbl = *(IntPtr**)comObj;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtbl[slot];
        return fn(comObj, arg);
    }

    private static int VtblCallPtr(IntPtr comObj, int slot, IntPtr arg)
    {
        var vtbl = *(IntPtr**)comObj;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[slot];
        return fn(comObj, arg);
    }

    private static int VtblGetPtr(IntPtr comObj, int slot, out IntPtr result)
    {
        IntPtr res = IntPtr.Zero;
        var vtbl = *(IntPtr**)comObj;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[slot];
        var hr = fn(comObj, &res);
        result = res;
        return hr;
    }

    private static int VtblGetInt(IntPtr comObj, int slot, out int result)
    {
        int res = 0;
        var vtbl = *(IntPtr**)comObj;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtbl[slot];
        var hr = fn(comObj, &res);
        result = res;
        return hr;
    }

    private static int VtblGetRect(IntPtr comObj, int slot, out RECT result)
    {
        RECT res = default;
        var vtbl = *(IntPtr**)comObj;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, RECT*, int>)vtbl[slot];
        var hr = fn(comObj, &res);
        result = res;
        return hr;
    }

    private static int VtblElementFromHandle(IntPtr uia, IntPtr hwnd, out IntPtr element)
    {
        IntPtr res = IntPtr.Zero;
        var vtbl = *(IntPtr**)uia;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[6];
        var hr = fn(uia, hwnd, &res);
        element = res;
        return hr;
    }

    private static int VtblElementFromHandleBuildCache(IntPtr uia, IntPtr hwnd, IntPtr cacheReq, out IntPtr element)
    {
        IntPtr res = IntPtr.Zero;
        var vtbl = *(IntPtr**)uia;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)vtbl[10];
        var hr = fn(uia, hwnd, cacheReq, &res);
        element = res;
        return hr;
    }

    private static int VtblFindAllBuildCache(IntPtr element, int scope, IntPtr condition, IntPtr cacheReq, out IntPtr array)
    {
        IntPtr res = IntPtr.Zero;
        var vtbl = *(IntPtr**)element;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, IntPtr*, int>)vtbl[8];
        var hr = fn(element, scope, condition, cacheReq, &res);
        array = res;
        return hr;
    }

    private static int VtblGetArrayElement(IntPtr elementArray, int index, out IntPtr element)
    {
        IntPtr res = IntPtr.Zero;
        var vtbl = *(IntPtr**)elementArray;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)vtbl[4];
        var hr = fn(elementArray, index, &res);
        element = res;
        return hr;
    }
}
