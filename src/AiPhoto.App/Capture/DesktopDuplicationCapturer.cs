using System.Runtime.InteropServices;
using AiPhoto.Core.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AiPhoto.App.Capture;

/// <summary>
/// Captures monitors with DXGI Desktop Duplication. On HDR displays it requests
/// <c>DXGI_FORMAT_R16G16B16A16_FLOAT</c> frames (scRGB), which is the only way to obtain the real HDR
/// pixels – GDI/BitBlt returns washed-out, clipped images on HDR desktops.
/// <para>
/// An instance keeps its duplications alive, so it can be reused for many captures in a row
/// (long screenshots). Dispose it as soon as you are done: active duplications cost GPU time.
/// </para>
/// </summary>
public sealed class DesktopDuplicationCapturer : IDisposable
{
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    private readonly List<OutputDuplicator> _outputs = [];
    private readonly Func<CaptureToneMapping> _toneMapping;
    private bool _disposed;

    public DesktopDuplicationCapturer(Func<CaptureToneMapping> toneMapping)
    {
        _toneMapping = toneMapping;
        Initialize();
    }

    public IReadOnlyList<PixelRect> MonitorBounds => _outputs.Select(o => o.Bounds).ToList();

    /// <summary>Captures every monitor and returns a frozen snapshot of the virtual desktop.</summary>
    public ScreenSnapshot CaptureAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mapping = _toneMapping();
        var whiteLevels = DisplayConfigHelper.GetSdrWhiteLevels();
        var monitors = new List<MonitorCapture>();

        foreach (var output in _outputs)
        {
            var frame = output.Read(PixelRect.FromLTRB(0, 0, output.Bounds.Width, output.Bounds.Height), ensureFresh: true);
            if (frame is null) continue;
            monitors.Add(BuildMonitorCapture(output, frame.Value, mapping, whiteLevels));
        }

        if (monitors.Count == 0) throw new InvalidOperationException("Desktop duplication returned no frames.");
        return new ScreenSnapshot(monitors);
    }

    /// <summary>
    /// Fast SDR capture of a screen rectangle (virtual-desktop pixels) that lies on a single monitor.
    /// Used repeatedly while recording a long screenshot. Returns the previous frame if nothing changed.
    /// </summary>
    public Bgra32Image? CaptureRegion(PixelRect screenRect)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var output = _outputs.FirstOrDefault(o => o.Bounds.Contains(screenRect.X + screenRect.Width / 2, screenRect.Y + screenRect.Height / 2));
        if (output is null) return null;

        var local = screenRect.Offset(-output.Bounds.X, -output.Bounds.Y).Intersect(new PixelRect(0, 0, output.Bounds.Width, output.Bounds.Height));
        var frame = output.Read(local, ensureFresh: false);
        if (frame is null) return null;

        if (frame.Value.Hdr is { } hdr)
        {
            var mapping = _toneMapping();
            var white = mapping.SdrWhiteOverrideNits > 0 ? mapping.SdrWhiteOverrideNits
                : DisplayConfigHelper.GetSdrWhiteLevels().GetValueOrDefault(output.DeviceName, ToneMapper.DefaultSdrWhiteNits);
            return ToneMapper.ToSdr(hdr, new ToneMapOptions { SdrWhiteNits = white, PeakNits = output.PeakNits, Operator = mapping.Operator });
        }
        return frame.Value.Sdr;
    }

    private static MonitorCapture BuildMonitorCapture(OutputDuplicator output, RawFrame frame, CaptureToneMapping mapping, Dictionary<string, float> whiteLevels)
    {
        if (frame.Hdr is { } hdr)
        {
            var white = mapping.SdrWhiteOverrideNits > 0 ? mapping.SdrWhiteOverrideNits
                : whiteLevels.GetValueOrDefault(output.DeviceName, ToneMapper.DefaultSdrWhiteNits);
            var sdr = ToneMapper.ToSdr(hdr, new ToneMapOptions { SdrWhiteNits = white, PeakNits = output.PeakNits, Operator = mapping.Operator });
            return new MonitorCapture
            {
                Bounds = output.Bounds, DeviceName = output.DeviceName, IsHdr = true,
                SdrWhiteNits = white, PeakNits = output.PeakNits, Sdr = sdr, Hdr = hdr,
            };
        }

        return new MonitorCapture { Bounds = output.Bounds, DeviceName = output.DeviceName, Sdr = frame.Sdr! };
    }

    private void Initialize()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                ID3D11Device? device = null;
                ID3D11DeviceContext? context = null;
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        if (!desc.AttachedToDesktop) continue;

                        if (device is null)
                        {
                            var hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                                out device, out context);
                            if (hr.Failure || device is null || context is null) break;
                        }

                        try
                        {
                            _outputs.Add(new OutputDuplicator(output, device, context!));
                        }
                        catch (SharpGen.Runtime.SharpGenException ex)
                        {
                            Diagnostics.Log.Warn($"Desktop duplication unavailable for {desc.DeviceName}: {ex.Message}");
                        }
                    }
                }
                // Each OutputDuplicator holds its own (QueryInterface'd) references to the device/context.
                context?.Dispose();
                device?.Dispose();
            }
        }

        if (_outputs.Count == 0) throw new InvalidOperationException("No monitor supports desktop duplication.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var o in _outputs) o.Dispose();
        _outputs.Clear();
    }

    private readonly record struct RawFrame(Bgra32Image? Sdr, RgbaHalfImage? Hdr);

    /// <summary>Duplication of a single output plus a CPU-readable copy of its latest frame.</summary>
    private sealed class OutputDuplicator : IDisposable
    {
        private readonly IDXGIOutput _output;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _staging;
        private Format _format;
        private ModeRotation _rotation;
        private bool _hasFrame;

        public OutputDuplicator(IDXGIOutput output, ID3D11Device device, ID3D11DeviceContext context)
        {
            // The caller disposes its wrappers right after this constructor returns. In SharpGen, Dispose()
            // releases the reference *and* zeroes the wrapper's NativePointer, so keeping the caller's wrapper
            // (even after an AddRef) would leave us with a dead object (null vtable => crash on first use).
            // QueryInterface returns a new wrapper that owns its own reference.
            _output = output.QueryInterface<IDXGIOutput>();
            _device = device.QueryInterface<ID3D11Device>();
            _context = context.QueryInterface<ID3D11DeviceContext>();

            try
            {
                var desc = output.Description;
                DeviceName = desc.DeviceName.TrimEnd('\0');
                var r = desc.DesktopCoordinates;
                Bounds = PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

                using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                if (output6 is not null)
                {
                    var d1 = output6.Description1;
                    IsHdrColorSpace = d1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                    if (d1.MaxLuminance > 0) PeakNits = d1.MaxLuminance;
                }

                CreateDuplication();
            }
            catch
            {
                // The instance is never handed out, so release the references taken above.
                Dispose();
                throw;
            }
        }

        public string DeviceName { get; }
        public PixelRect Bounds { get; }
        public bool IsHdrColorSpace { get; }
        public float PeakNits { get; } = 1000f;

        private void CreateDuplication()
        {
            _duplication?.Dispose();
            _duplication = null;
            _staging?.Dispose();
            _staging = null;
            _hasFrame = false;

            using var output5 = _output.QueryInterfaceOrNull<IDXGIOutput5>();
            if (output5 is not null)
            {
                try
                {
                    // Listing FP16 first makes Windows hand out the native scRGB surface on HDR displays.
                    // On SDR displays it still returns BGRA8.
                    _duplication = output5.DuplicateOutput1(_device, [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm]);
                }
                catch (SharpGen.Runtime.SharpGenException ex)
                {
                    Diagnostics.Log.Warn($"DuplicateOutput1 failed for {DeviceName}, falling back to DuplicateOutput: {ex.Message}");
                }
            }

            if (_duplication is null)
            {
                using var output1 = _output.QueryInterface<IDXGIOutput1>();
                _duplication = output1.DuplicateOutput(_device);
            }

            var dd = _duplication.Description;
            _format = dd.ModeDescription.Format;
            _rotation = dd.Rotation;
        }

        /// <summary>
        /// Reads a rectangle (in un-rotated monitor coordinates as seen by the user) of the latest frame.
        /// </summary>
        public RawFrame? Read(PixelRect rect, bool ensureFresh)
        {
            if (!Acquire(ensureFresh ? 500 : 60)) return null;
            return CopyFromStaging(rect);
        }

        private bool Acquire(int timeoutMs)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (_duplication is null)
                {
                    // A previous re-creation failed (e.g. secure desktop / UAC prompt was showing): retry now.
                    try { CreateDuplication(); } catch (SharpGen.Runtime.SharpGenException) { return false; }
                }

                var hr = _duplication!.AcquireNextFrame((uint)(_hasFrame ? Math.Min(timeoutMs, 30) : timeoutMs), out var info, out var resource);
                if (hr.Code == DxgiErrorWaitTimeout)
                {
                    // No new frame: the cached copy is still the current desktop image.
                    if (_hasFrame) return true;
                    continue;
                }
                if (hr.Code == DxgiErrorAccessLost)
                {
                    // Mode change, UAC secure desktop, full-screen app switch...: recreate and retry.
                    resource?.Dispose();
                    try { CreateDuplication(); } catch (SharpGen.Runtime.SharpGenException) { return false; }
                    continue;
                }
                if (hr.Failure)
                {
                    resource?.Dispose();
                    Diagnostics.Log.Warn($"AcquireNextFrame failed on {DeviceName}: 0x{hr.Code:X8}");
                    return _hasFrame;
                }

                var firstFrameWithoutPresent = false;
                try
                {
                    // A frame with LastPresentTime == 0 only updated the mouse pointer; the surface is still valid.
                    if (info.LastPresentTime != 0 || !_hasFrame)
                    {
                        firstFrameWithoutPresent = !_hasFrame && info.LastPresentTime == 0;
                        using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                        EnsureStaging(texture.Description);
                        _context.CopyResource(_staging!, texture);
                        _hasFrame = true;
                    }
                }
                finally
                {
                    resource?.Dispose();
                    _duplication.ReleaseFrame();
                }

                // Some (hybrid GPU) drivers hand out a black initial surface that was never presented.
                // Try once more briefly; if nothing new arrives the copied frame is kept (timeout path above).
                if (firstFrameWithoutPresent && attempt == 0) continue;
                return true;
            }
            return _hasFrame;
        }

        private void EnsureStaging(Texture2DDescription source)
        {
            if (_staging is not null)
            {
                var d = _staging.Description;
                if (d.Width == source.Width && d.Height == source.Height && d.Format == source.Format) return;
                _staging.Dispose();
            }

            _format = source.Format;
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = source.Width,
                Height = source.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = source.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        private unsafe RawFrame CopyFromStaging(PixelRect rect)
        {
            var desc = _staging!.Description;
            var texW = (int)desc.Width;
            var texH = (int)desc.Height;
            var quarterTurns = _rotation switch
            {
                ModeRotation.Rotate90 => 1,
                ModeRotation.Rotate180 => 2,
                ModeRotation.Rotate270 => 3,
                _ => 0,
            };

            // For rotated monitors the texture is in panel orientation: read it whole, rotate, then crop.
            var readRect = quarterTurns == 0 ? rect : new PixelRect(0, 0, texW, texH);
            readRect = readRect.Intersect(new PixelRect(0, 0, texW, texH));

            var hr = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
            if (hr.Failure) throw new InvalidOperationException($"Map failed: 0x{hr.Code:X8}");
            try
            {
                var src = (byte*)mapped.DataPointer;
                var pitch = (int)mapped.RowPitch;

                if (_format == Format.R16G16B16A16_Float)
                {
                    var hdr = new RgbaHalfImage(readRect.Width, readRect.Height);
                    var dst = MemoryMarshal.AsBytes(hdr.Pixels.AsSpan());
                    var rowBytes = readRect.Width * RgbaHalfImage.BytesPerPixel;
                    for (var y = 0; y < readRect.Height; y++)
                    {
                        new ReadOnlySpan<byte>(src + (readRect.Y + y) * pitch + readRect.X * 8, rowBytes)
                            .CopyTo(dst.Slice(y * rowBytes, rowBytes));
                    }
                    // Desktop alpha is undefined here too (same as the BGRA path below); force opaque so the
                    // .jxr export (64bppRGBAHalf keeps alpha) is not saved as a transparent image.
                    var halves = hdr.Pixels;
                    for (var i = 3; i < halves.Length; i += 4) halves[i] = Half.One;
                    if (quarterTurns != 0) hdr = ImageRotation.RotateClockwise(hdr, quarterTurns).Crop(rect);
                    return new RawFrame(null, hdr);
                }

                var sdr = new Bgra32Image(readRect.Width, readRect.Height);
                for (var y = 0; y < readRect.Height; y++)
                {
                    var row = sdr.GetRow(y);
                    new ReadOnlySpan<byte>(src + (readRect.Y + y) * pitch + readRect.X * 4, row.Length).CopyTo(row);
                    // Desktop alpha is undefined; force opaque.
                    for (var i = 3; i < row.Length; i += 4) row[i] = 255;
                }
                if (quarterTurns != 0) sdr = ImageRotation.RotateClockwise(sdr, quarterTurns).Crop(rect);
                return new RawFrame(sdr, null);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }

        public void Dispose()
        {
            _staging?.Dispose();
            _duplication?.Dispose();
            _context.Dispose();
            _device.Dispose();
            _output.Dispose();
        }
    }
}

/// <summary>How HDR frames are converted to SDR for display, clipboard and translation.</summary>
public sealed record CaptureToneMapping(ToneMapOperator Operator, float SdrWhiteOverrideNits);
