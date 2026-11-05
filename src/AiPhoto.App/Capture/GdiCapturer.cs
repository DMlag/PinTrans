using System.Runtime.InteropServices;
using AiPhoto.Core.Imaging;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.Capture;

/// <summary>
/// GDI BitBlt fallback used when desktop duplication is unavailable (remote desktop sessions, some VMs,
/// very old drivers). SDR only.
/// </summary>
internal static class GdiCapturer
{
    public static ScreenSnapshot CaptureVirtualScreen()
    {
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        var image = Capture(new PixelRect(x, y, w, h));
        return new ScreenSnapshot([new MonitorCapture { Bounds = new PixelRect(x, y, w, h), DeviceName = "GDI", Sdr = image }]);
    }

    public static Bgra32Image Capture(PixelRect rect)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = rect.Width,
            biHeight = -rect.Height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var bitmap = CreateDIBSection(screenDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memDc, bitmap);
        try
        {
            BitBlt(memDc, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y, SRCCOPY | CAPTUREBLT);
            var image = new Bgra32Image(rect.Width, rect.Height);
            Marshal.Copy(bits, image.Pixels, 0, image.Pixels.Length);
            for (var i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
            return image;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
