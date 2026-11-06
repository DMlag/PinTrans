using System.Runtime.InteropServices;
using System.Text;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Snapping;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.Capture.Selection;

internal sealed record WindowSnapTarget(
    IntPtr Hwnd,
    PixelRect ScreenBounds,
    string Title,
    string ClassName,
    List<SnapCandidate> ChildHwnds);

/// <summary>
/// Collects visible top-level windows (front to back) and optionally their Win32 child control HWNDs
/// so the capture overlay can snap to windows and controls under the cursor.
/// </summary>
internal static class WindowFinder
{
    private const int MaxChildHwndsPerWindow = 500;

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd_Overlay", "Windows.UI.Core.CoreWindow",
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    /// <summary>Returns window rectangles in screen (virtual desktop) pixels, topmost first.</summary>
    public static List<PixelRect> GetWindowRects()
        => GetWindows(includeChildHwnds: false).Select(w => w.ScreenBounds).ToList();

    /// <summary>
    /// Enumerates visible top-level windows in front-to-back Z-order, optionally including visible Win32 child HWNDs.
    /// All returned coordinates are in virtual-desktop physical pixels.
    /// </summary>
    public static List<WindowSnapTarget> GetWindows(bool includeChildHwnds)
    {
        var result = new List<WindowSnapTarget>();
        var classBuf = new StringBuilder(256);
        var titleBuf = new StringBuilder(256);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            // Click-through overlays (e.g. GPU/game overlays, our own long-capture frame) are often full-screen and
            // invisible; as the topmost hit they would swallow every snap. They cannot be clicked anyway.
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0) return true;

            classBuf.Clear();
            GetClassName(hwnd, classBuf, classBuf.Capacity);
            var className = classBuf.ToString();
            if (IgnoredClasses.Contains(className)) return true;

            // Extended frame bounds exclude the invisible resize borders of Windows 10/11 windows.
            RECT r;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0)
            {
                if (!GetWindowRect(hwnd, out r)) return true;
            }
            if (r.Width < 8 || r.Height < 8) return true;

            var bounds = PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            titleBuf.Clear();
            GetWindowText(hwnd, titleBuf, titleBuf.Capacity);
            var title = titleBuf.ToString().Trim();

            var children = includeChildHwnds ? EnumVisibleChildHwnds(hwnd, bounds, classBuf, titleBuf) : [];
            result.Add(new WindowSnapTarget(hwnd, bounds, title, className, children));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static List<SnapCandidate> EnumVisibleChildHwnds(
        IntPtr parentHwnd,
        PixelRect parentBounds,
        StringBuilder classBuf,
        StringBuilder titleBuf)
    {
        var children = new List<SnapCandidate>();
        EnumChildWindows(parentHwnd, (child, _) =>
        {
            if (children.Count >= MaxChildHwndsPerWindow) return false;
            if (!IsWindowVisible(child)) return true;
            if (!GetWindowRect(child, out var r)) return true;

            var rect = PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Intersect(parentBounds);
            if (rect.Width < SnapIndex.MinCandidateSize || rect.Height < SnapIndex.MinCandidateSize) return true;
            if (SnapIndex.AreNearlyEqual(rect, parentBounds)) return true;

            classBuf.Clear();
            GetClassName(child, classBuf, classBuf.Capacity);
            var cls = classBuf.ToString();

            titleBuf.Clear();
            GetWindowText(child, titleBuf, titleBuf.Capacity);
            var text = titleBuf.ToString().Trim();

            var (kind, typeLabel) = ClassifyWin32Class(cls);
            var label = BuildControlLabel(typeLabel, text);
            children.Add(new SnapCandidate(rect, kind, SnapSource.ChildHwnd, Depth: 1, Label: label));
            return true;
        }, IntPtr.Zero);
        return children;
    }

    private static (SnapKind Kind, string Label) ClassifyWin32Class(string className)
    {
        if (string.IsNullOrEmpty(className)) return (SnapKind.Control, "控件");
        if (className.Contains("Button", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Control, "按钮");
        if (className.Contains("Edit", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Control, "输入框");
        if (className.Contains("ComboBox", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Control, "下拉框");
        if (className.Contains("ListBox", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("ListView", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Panel, "列表");
        if (className.Contains("TreeView", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Panel, "树视图");
        if (className.Contains("TabControl", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Panel, "标签栏");
        if (className.Contains("Toolbar", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Panel, "工具栏");
        if (className.Contains("StatusBar", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("statusbar", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Panel, "状态栏");
        if (className.Contains("ScrollBar", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Control, "滚动条");
        if (className.Contains("Header", StringComparison.OrdinalIgnoreCase)) return (SnapKind.Control, "表头");
        if (className.Equals("Static", StringComparison.OrdinalIgnoreCase)) return (SnapKind.TextBlock, "文本");
        return (SnapKind.Panel, "面板");
    }

    internal static string BuildControlLabel(string typeLabel, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return typeLabel;
        var clean = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0) return typeLabel;
        if (clean.Length > 24) clean = clean[..24] + "…";
        return $"{typeLabel} · {clean}";
    }
}
