using AiPhoto.Core.Imaging;

namespace AiPhoto.App.Capture;

/// <summary>Pixels captured from one monitor.</summary>
public sealed class MonitorCapture
{
    /// <summary>Monitor rectangle in virtual-desktop physical pixels.</summary>
    public required PixelRect Bounds { get; init; }
    public required string DeviceName { get; init; }

    /// <summary>True when the monitor runs in HDR (scRGB FP16 frames).</summary>
    public bool IsHdr { get; init; }

    /// <summary>Windows "SDR content brightness" for this monitor (only meaningful when <see cref="IsHdr"/>).</summary>
    public float SdrWhiteNits { get; init; } = ToneMapper.DefaultSdrWhiteNits;

    public float PeakNits { get; init; } = 1000f;

    /// <summary>Displayable 8-bit image (tone mapped for HDR monitors).</summary>
    public required Bgra32Image Sdr { get; init; }

    /// <summary>Original HDR pixels (scRGB), or <c>null</c> for SDR monitors.</summary>
    public RgbaHalfImage? Hdr { get; init; }
}

/// <summary>A frozen image of the whole virtual desktop at the moment the capture hotkey was pressed.</summary>
public sealed class ScreenSnapshot
{
    public ScreenSnapshot(IReadOnlyList<MonitorCapture> monitors)
    {
        Monitors = monitors;
        VirtualBounds = monitors.Aggregate(default(PixelRect), (acc, m) => acc.Union(m.Bounds));
        Composite = new Bgra32Image(VirtualBounds.Width, VirtualBounds.Height);
        foreach (var m in monitors)
        {
            Composite.Blit(m.Sdr, m.Bounds.X - VirtualBounds.X, m.Bounds.Y - VirtualBounds.Y);
        }
    }

    public IReadOnlyList<MonitorCapture> Monitors { get; }

    /// <summary>Union of all monitor rectangles (physical pixels, may have a negative origin).</summary>
    public PixelRect VirtualBounds { get; }

    /// <summary>SDR image of the whole virtual desktop; (0,0) = <see cref="VirtualBounds"/> top-left.</summary>
    public Bgra32Image Composite { get; }

    public bool HasHdr => Monitors.Any(m => m.IsHdr);

    /// <summary>Crop of the SDR composite. <paramref name="rect"/> is relative to the composite.</summary>
    public Bgra32Image CropSdr(PixelRect rect) => Composite.Crop(rect);

    /// <summary>True if the (composite-relative) rectangle touches an HDR monitor.</summary>
    public bool IntersectsHdr(PixelRect rect)
    {
        var screen = rect.Offset(VirtualBounds.X, VirtualBounds.Y);
        return Monitors.Any(m => m.IsHdr && !m.Bounds.Intersect(screen).IsEmpty);
    }

    /// <summary>
    /// HDR crop (scRGB) of a composite-relative rectangle. Parts on SDR monitors are converted to scRGB
    /// using the SDR white level of the HDR monitor so they look consistent.
    /// </summary>
    public RgbaHalfImage? CropHdr(PixelRect rect)
    {
        if (!IntersectsHdr(rect)) return null;
        var screen = rect.Offset(VirtualBounds.X, VirtualBounds.Y);
        var result = new RgbaHalfImage(rect.Width, rect.Height);
        var sdrWhite = Monitors.First(m => m.IsHdr).SdrWhiteNits;

        foreach (var m in Monitors)
        {
            var part = m.Bounds.Intersect(screen);
            if (part.IsEmpty) continue;
            var local = part.Offset(-m.Bounds.X, -m.Bounds.Y);
            var tile = m.Hdr is not null ? m.Hdr.Crop(local) : RgbaHalfImage.FromSdr(m.Sdr.Crop(local), sdrWhite);
            result.Blit(tile, part.X - screen.X, part.Y - screen.Y);
        }
        return result;
    }

    public MonitorCapture? MonitorAt(int compositeX, int compositeY)
    {
        var x = compositeX + VirtualBounds.X;
        var y = compositeY + VirtualBounds.Y;
        return Monitors.FirstOrDefault(m => m.Bounds.Contains(x, y));
    }
}
