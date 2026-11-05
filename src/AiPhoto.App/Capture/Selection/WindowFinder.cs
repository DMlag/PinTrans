using System.Runtime.InteropServices;
using System.Text;
using AiPhoto.Core.Imaging;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.Capture.Selection;

/// <summary>
/// Collects the rectangles of visible top-level windows (front to back) so the selection can snap to
/// the window under the cursor with a single click.
/// </summary>
internal static class WindowFinder
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd_Overlay", "Windows.UI.Core.CoreWindow",
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    /// <summary>Returns window rectangles in screen (virtual desktop) pixels, topmost first.</summary>
    public static List<PixelRect> GetWindowRects()
    {
        var result = new List<PixelRect>();
        var className = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            // Click-through overlays (e.g. GPU/game overlays, our own long-capture frame) are often full-screen and
            // invisible; as the topmost hit they would swallow every snap. They cannot be clicked anyway.
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0) return true;

            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (IgnoredClasses.Contains(className.ToString())) return true;

            // Extended frame bounds exclude the invisible resize borders of Windows 10/11 windows.
            RECT r;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0)
            {
                if (!GetWindowRect(hwnd, out r)) return true;
            }
            if (r.Width < 8 || r.Height < 8) return true;
            result.Add(PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
