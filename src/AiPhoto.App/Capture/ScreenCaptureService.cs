using AiPhoto.App.Diagnostics;
using AiPhoto.Core.Settings;

namespace AiPhoto.App.Capture;

/// <summary>Entry point for taking full-desktop snapshots (DXGI first, GDI as fallback).</summary>
public sealed class ScreenCaptureService
{
    private readonly Func<AppSettings> _settings;

    public ScreenCaptureService(Func<AppSettings> settings) => _settings = settings;

    public CaptureToneMapping CurrentToneMapping()
    {
        var c = _settings().Capture;
        return new CaptureToneMapping(c.HdrToneMapping, c.HdrSdrWhiteNitsOverride);
    }

    public ScreenSnapshot CaptureDesktop()
    {
        try
        {
            using var capturer = new DesktopDuplicationCapturer(CurrentToneMapping);
            return capturer.CaptureAll();
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop duplication failed, using GDI fallback: {ex}");
            return GdiCapturer.CaptureVirtualScreen();
        }
    }

    /// <summary>Creates a long-lived capturer for repeated region captures (long screenshots).</summary>
    public IRegionCapturer CreateRegionCapturer()
    {
        try
        {
            return new DxgiRegionCapturer(new DesktopDuplicationCapturer(CurrentToneMapping));
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop duplication failed for region capture, using GDI: {ex.Message}");
            return new GdiRegionCapturer();
        }
    }

    private sealed class DxgiRegionCapturer(DesktopDuplicationCapturer inner) : IRegionCapturer
    {
        public Core.Imaging.Bgra32Image? Capture(Core.Imaging.PixelRect screenRect) => inner.CaptureRegion(screenRect);
        public void Dispose() => inner.Dispose();
    }

    private sealed class GdiRegionCapturer : IRegionCapturer
    {
        public Core.Imaging.Bgra32Image? Capture(Core.Imaging.PixelRect screenRect) => GdiCapturer.Capture(screenRect);
        public void Dispose() { }
    }
}

public interface IRegionCapturer : IDisposable
{
    Core.Imaging.Bgra32Image? Capture(Core.Imaging.PixelRect screenRect);
}
