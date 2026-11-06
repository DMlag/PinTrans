using System.Windows;
using System.Windows.Threading;
using AiPhoto.App.Capture;
using AiPhoto.App.Diagnostics;
using AiPhoto.App.Infrastructure;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Settings;
using AiPhoto.Core.Stitching;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.LongCapture;

/// <summary>
/// Records a scrolling region: frames are captured continuously while the user scrolls (or while
/// auto-scroll injects mouse-wheel events) and stitched into one tall image.
/// </summary>
public sealed class LongCaptureSession
{
    private readonly PixelRect _rect;
    private readonly ScreenCaptureService _captureService;
    private readonly CaptureSettings _settings;
    private readonly Action<Bgra32Image> _onCompleted;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly ScrollStitcher _stitcher = new();

    private LongCaptureFrame? _frame;
    private LongCapturePanel? _panel;
    private PixelRect _panelRect;
    private volatile bool _autoScroll;
    private volatile bool _finishRequested;
    private bool _completed;

    public LongCaptureSession(PixelRect rect, ScreenCaptureService captureService, CaptureSettings settings, Action<Bgra32Image> onCompleted)
    {
        _rect = rect;
        _captureService = captureService;
        _settings = settings;
        _onCompleted = onCompleted;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        _frame = new LongCaptureFrame(_rect);
        _frame.Show();

        _panel = new LongCapturePanel();
        _panel.AutoScrollChanged += (_, on) => SetAutoScroll(on);
        _panel.FinishRequested += (_, _) => _finishRequested = true;
        _panel.CancelRequested += (_, _) => Cancel();
        _panel.SourceInitialized += (_, _) => PhysicalPlacement.ExcludeFromCapture(_panel);
        _panel.Loaded += (_, _) => PositionPanel();
        // The panel can be closed by the user (Alt+F4 once it has been activated by a click). Without this the
        // capture loop, the DXGI duplication and the click-through red frame would live on with no way to stop them.
        _panel.Closed += (_, _) =>
        {
            if (_panel is null) return; // closed by Cleanup()
            _panel = null;
            Cancel();
        };
        _panel.Show();

        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private void SetAutoScroll(bool on)
    {
        _autoScroll = on;
        if (on)
        {
            // Wheel events go to the window under the cursor: park the cursor in the region once when starting.
            // Never trap or re-center the cursor afterwards so the user can freely move to the panel to click Finish.
            var targetX = _rect.X + _rect.Width / 2;
            var targetY = _rect.Y + _rect.Height / 2;
            if (_panelRect.Width > 0 && _panelRect.Contains(targetX, targetY))
            {
                targetY = _rect.Y + Math.Max(10, _rect.Height / 4);
            }
            SetCursorPos(targetX, targetY);
        }
    }

    private void PositionPanel()
    {
        if (_panel is null) return;
        var size = PhysicalPlacement.GetRect(_panel);
        var work = GetWorkArea(new RECT { Left = _rect.X, Top = _rect.Y, Right = _rect.Right, Bottom = _rect.Bottom });
        var monitor = PixelRect.FromLTRB(work.Left, work.Top, work.Right, work.Bottom);
        int x, y;
        if (_rect.Right + size.Width + 8 <= monitor.Right) { x = _rect.Right + 8; y = _rect.Y; }
        else if (_rect.X - size.Width - 8 >= monitor.X) { x = _rect.X - size.Width - 8; y = _rect.Y; }
        else { x = _rect.Right - size.Width; y = Math.Min(_rect.Bottom + 8, monitor.Bottom - size.Height); }
        y = Math.Clamp(y, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - size.Height));
        _panelRect = new PixelRect(x - 6, y - 6, size.Width + 12, size.Height + 12);
        PhysicalPlacement.Place(_panel, new PixelRect(x, y, size.Width, size.Height));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        IRegionCapturer? capturer = null;
        try
        {
            // Give DWM a moment to remove the selection overlay from the screen.
            await Task.Delay(150, ct);
            capturer = _captureService.CreateRegionCapturer();
            var first = capturer.Capture(_rect);
            if (first is null) throw new InvalidOperationException("无法捕获所选区域。");
            _stitcher.AddFrame(first);
            Report(null);

            var noChange = 0;
            var notches = Math.Max(1, _settings.LongCaptureWheelNotches);
            var settle = Math.Clamp(_settings.LongCaptureSettleMs, 50, 2000);
            Bgra32Image? previous = first;

            while (!ct.IsCancellationRequested && !_finishRequested)
            {
                if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                {
                    _ = _dispatcher.BeginInvoke(Cancel);
                    return;
                }

                Bgra32Image? frame;
                if (_autoScroll)
                {
                    // If the user moved the mouse outside the capture region or onto the control panel
                    // (e.g. to click "完成" / "取消"), pause wheel injection and never pull the cursor back.
                    if (!IsCursorInScrollTarget())
                    {
                        await Task.Delay(60, ct);
                        continue;
                    }
                    SendWheel(-120 * notches);
                    frame = await CaptureSettledAsync(capturer, settle, ct);
                }
                else
                {
                    await Task.Delay(70, ct);
                    frame = capturer.Capture(_rect);
                    // Skip frames identical to the last one cheaply.
                    if (frame is not null && previous is not null && SamePixels(frame, previous)) continue;
                }
                if (frame is null) continue;
                previous = frame;

                var result = _stitcher.AddFrame(frame);
                switch (result.Status)
                {
                    case StitchStatus.Appended:
                        noChange = 0;
                        Report(null);
                        break;
                    case StitchStatus.NoChange when _autoScroll:
                        if (++noChange >= 3)
                        {
                            // The page stopped moving: we reached the end.
                            _autoScroll = false;
                            _dispatcher.Invoke(() => _panel?.SetAutoScroll(false));
                            Report("已到达底部，可点击“完成”。");
                        }
                        break;
                    case StitchStatus.NoOverlap:
                        if (_autoScroll && notches > 1)
                        {
                            // Scrolled further than one viewport: step back and use smaller steps.
                            SendWheel(120 * notches);
                            notches = 1;
                            previous = await CaptureSettledAsync(capturer, settle, ct);
                        }
                        Report("滚动过快或内容发生变化，请放慢滚动速度（向上滚动的内容不会被记录）。");
                        break;
                    case StitchStatus.LimitReached:
                        Report("已达到最大高度。");
                        _finishRequested = true;
                        break;
                }
            }

            if (ct.IsCancellationRequested) return;
            var image = _stitcher.Compose();
            _dispatcher.Invoke(() => Complete(image));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Long capture failed", ex);
            _dispatcher.Invoke(() =>
            {
                MessageBox.Show($"长截图失败：{ex.Message}", "PinTrans", MessageBoxButton.OK, MessageBoxImage.Error);
                Cleanup();
            });
        }
        finally
        {
            capturer?.Dispose();
        }
    }

    /// <summary>Waits until two consecutive captures are identical (smooth scrolling finished).</summary>
    private async Task<Bgra32Image?> CaptureSettledAsync(IRegionCapturer capturer, int settleMs, CancellationToken ct)
    {
        await Task.Delay(settleMs / 2, ct);
        var last = capturer.Capture(_rect);
        for (var i = 0; i < 12 && !_finishRequested; i++)
        {
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
            {
                _ = _dispatcher.BeginInvoke(Cancel);
                return last;
            }
            await Task.Delay(Math.Max(30, settleMs / 3), ct);
            if (_finishRequested) break;
            var next = capturer.Capture(_rect);
            if (next is null) continue;
            if (last is not null && SamePixels(last, next)) return next;
            last = next;
        }
        return last;
    }

    private bool IsCursorInScrollTarget()
        => GetCursorPos(out var pt) && _rect.Contains(pt.X, pt.Y) && (_panelRect.Width <= 0 || !_panelRect.Contains(pt.X, pt.Y));

    private static void SendWheel(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = delta, dwFlags = MOUSEEVENTF_WHEEL } },
        };
        SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static bool SamePixels(Bgra32Image a, Bgra32Image b)
        => a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);

    private void Report(string? warning)
    {
        var height = _stitcher.TotalHeight;
        var frames = _stitcher.FrameCount;
        _dispatcher.BeginInvoke(() =>
        {
            _panel?.SetProgress(height, frames);
            if (warning is not null || frames > 1) _panel?.SetWarning(warning);
        });
    }

    private void Complete(Bgra32Image image)
    {
        if (_completed) return;
        _completed = true;
        Cleanup();
        _onCompleted(image);
    }

    private void Cancel()
    {
        _cts.Cancel();
        Cleanup();
    }

    private void Cleanup()
    {
        // Clear the fields first: closing the panel raises its Closed handler, which must see the session as cleaned up.
        var frame = _frame;
        var panel = _panel;
        _frame = null;
        _panel = null;
        frame?.Close();
        panel?.Close();
    }
}
