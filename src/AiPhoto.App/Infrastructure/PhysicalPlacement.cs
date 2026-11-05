using System.Windows;
using System.Windows.Interop;
using AiPhoto.Core.Imaging;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.Infrastructure;

/// <summary>Helpers to place WPF windows at exact physical-pixel rectangles (per-monitor DPI aware).</summary>
public static class PhysicalPlacement
{
    public static void Place(Window window, PixelRect rect, bool topmost = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height,
            SWP_NOACTIVATE | (topmost ? 0 : SWP_NOZORDER));
    }

    public static PixelRect GetRect(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return GetWindowRect(hwnd, out var r) ? PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : default;
    }

    /// <summary>Device pixels per DIP for the monitor the window currently lives on.</summary>
    public static double GetScale(Window window)
        => PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    public static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static void MakeToolWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    /// <summary>Disables Windows 11 rounded corners (they would clip pixel-exact overlays). No-op on Windows 10.</summary>
    public static void DisableRoundedCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var pref = DWMWCP_DONOTROUND;
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Hides the window from screenshots/screen capture (Windows 10 2004+). Best effort.</summary>
    public static void ExcludeFromCapture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
        catch (EntryPointNotFoundException) { }
    }
}
