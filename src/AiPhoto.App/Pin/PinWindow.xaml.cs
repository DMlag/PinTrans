using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AiPhoto.App.Diagnostics;
using AiPhoto.App.Imaging;
using AiPhoto.App.Infrastructure;
using AiPhoto.App.Ocr;
using AiPhoto.App.Services;
using AiPhoto.App.Translation;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Ocr;
using AiPhoto.Core.Translation;
using Microsoft.Win32;

namespace AiPhoto.App.Pin;

/// <summary>
/// An image pinned on top of the desktop. Supports in-place Gemini translation, local offline OCR,
/// and QR/barcode recognition with interactive bounding boxes and a selectable text drawer.
/// </summary>
public partial class PinWindow : Window
{
    private readonly Bgra32Image _image;
    private readonly IAppHost _host;
    private readonly PixelRect _initialRect;
    private readonly PinMode _initialMode;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _hoverHideTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly long _createdAt = Environment.TickCount64;

    private double _scale = 1;
    private double _zoom = 1;
    private int _borderPx = 1;
    private CancellationTokenSource? _cts;
    private bool _translating;
    private bool _hasError;
    private bool _closed;
    private string _lastError = "";
    private string _ocrFullText = "";
    private Uri? _detectedQrUri;

    public PinWindow(Bgra32Image image, PixelRect screenRect, IAppHost host, PinMode mode = PinMode.None)
    {
        _image = image;
        _host = host;
        _initialRect = screenRect;
        _initialMode = mode;
        InitializeComponent();

        FloatingPopup.CustomPopupPlacementCallback = PlaceFloatingPopup;
        OcrDrawerPopup.CustomPopupPlacementCallback = PlaceOcrDrawerPopup;

        ImageView.Source = image.ToBitmapSource();
        ImageView.Width = image.Width;
        ImageView.Height = image.Height;
        PixelCanvas.Width = image.Width;
        PixelCanvas.Height = image.Height;
        Layer.Width = image.Width;
        Layer.Height = image.Height;

        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            if (!_translating && !_hasError)
            {
                StatusPill.Visibility = Visibility.Collapsed;
                UpdatePopupOpen();
            }
        };
        _hoverHideTimer.Tick += (_, _) =>
        {
            _hoverHideTimer.Stop();
            if (!IsMouseOver && !FloatingPopup.IsMouseOver && !OcrDrawerPopup.IsMouseOver && OcrDrawer.Visibility != Visibility.Visible)
            {
                HoverBar.Visibility = Visibility.Collapsed;
                UpdatePopupOpen();
            }
        };

        SourceInitialized += (_, _) =>
        {
            PhysicalPlacement.MakeToolWindow(this);
            PhysicalPlacement.DisableRoundedCorners(this);
            ApplyScale();
            Place(_initialRect.X - _borderPx, _initialRect.Y - _borderPx);
        };
        Loaded += (_, _) =>
        {
            ApplyScale();
            Place(_initialRect.X - _borderPx, _initialRect.Y - _borderPx);
            UpdatePopupOpen();
            if (_initialMode == PinMode.Translate) _ = TranslateAsync();
            else if (_initialMode == PinMode.Ocr) _ = RunOfflineOcrAsync();
        };
        LocationChanged += (_, _) => RepositionPopup();
        SizeChanged += (_, _) => RepositionPopup();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var current = PhysicalPlacement.GetRect(this);
            ApplyScale();
            Place(current.X, current.Y);
            RepositionPopup();
        }, DispatcherPriority.Background);

        MouseLeftButtonDown += OnMouseLeftDown;
        MouseMove += OnMouseMoved;
        MouseEnter += (_, _) =>
        {
            _hoverHideTimer.Stop();
            HoverBar.Visibility = Visibility.Visible;
            UpdatePopupOpen();
        };
        MouseLeave += (_, _) =>
        {
            Layer.Highlight = null;
            Layer.HighlightOcr = null;
            Layer.HighlightBarcode = null;
            _hoverHideTimer.Start();
        };
        MouseWheel += OnMouseWheel;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Closed += (_, _) =>
        {
            _closed = true;
            _cts?.Cancel();
            _statusTimer.Stop();
            _hoverHideTimer.Stop();
            FloatingPopup.IsOpen = false;
            OcrDrawerPopup.IsOpen = false;
        };
    }

    public PinWindow(Bgra32Image image, PixelRect screenRect, IAppHost host, bool translate)
        : this(image, screenRect, host, translate ? PinMode.Translate : PinMode.None)
    {
    }

    // ------------------------------------------------------------------ floating popup placement

    private static CustomPopupPlacement[] PlaceFloatingPopup(Size popupSize, Size targetSize, Point offset)
    {
        var x = Math.Max(0, targetSize.Width - popupSize.Width);
        return
        [
            new CustomPopupPlacement(new Point(x, -popupSize.Height), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(x, targetSize.Height), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(Math.Max(4, x - 4), 4), PopupPrimaryAxis.Horizontal),
        ];
    }

    private static CustomPopupPlacement[] PlaceOcrDrawerPopup(Size popupSize, Size targetSize, Point offset)
    {
        return
        [
            // 1. Place directly below the pinned image (HoverBar stays above, so the pinned image is completely unobstructed).
            new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal),
            // 2. If near the bottom of the monitor, place to the right or left of the pinned image.
            new CustomPopupPlacement(new Point(targetSize.Width + 6, 0), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(new Point(-popupSize.Width - 6, 0), PopupPrimaryAxis.Vertical),
        ];
    }

    private void OnPopupMouseEnter(object sender, MouseEventArgs e)
    {
        _hoverHideTimer.Stop();
        HoverBar.Visibility = Visibility.Visible;
        UpdatePopupOpen();
    }

    private void OnPopupMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverHideTimer.Start();
    }

    private void UpdatePopupOpen()
    {
        if (_closed || !IsLoaded)
        {
            FloatingPopup.IsOpen = false;
            OcrDrawerPopup.IsOpen = false;
            return;
        }
        var shouldOpenToolbar = StatusPill.Visibility == Visibility.Visible
                             || HoverBar.Visibility == Visibility.Visible
                             || OcrDrawer.Visibility == Visibility.Visible;
        FloatingPopup.IsOpen = shouldOpenToolbar;
        OcrDrawerPopup.IsOpen = OcrDrawer.Visibility == Visibility.Visible;
        if (shouldOpenToolbar || OcrDrawerPopup.IsOpen) RepositionPopup();
    }

    private void RepositionPopup()
    {
        if (FloatingPopup.IsOpen)
        {
            var offset = FloatingPopup.HorizontalOffset;
            FloatingPopup.HorizontalOffset = offset + 0.001;
            FloatingPopup.HorizontalOffset = offset;
        }
        if (OcrDrawerPopup.IsOpen)
        {
            var offset = OcrDrawerPopup.HorizontalOffset;
            OcrDrawerPopup.HorizontalOffset = offset + 0.001;
            OcrDrawerPopup.HorizontalOffset = offset;
        }
    }

    // ------------------------------------------------------------------ placement / zoom

    private void ApplyScale()
    {
        _scale = PhysicalPlacement.GetScale(this);
        _borderPx = Math.Max(1, (int)Math.Round(_scale));
        Root.Margin = new Thickness(_borderPx / _scale);
        PixelScale.ScaleX = PixelScale.ScaleY = _zoom / _scale;
    }

    private void Place(int x, int y)
    {
        var w = (int)Math.Round(_image.Width * _zoom) + _borderPx * 2;
        var h = (int)Math.Round(_image.Height * _zoom) + _borderPx * 2;
        PhysicalPlacement.Place(this, new PixelRect(x, y, w, h));
    }

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.1, 8);
        if (Math.Abs(zoom - _zoom) < 0.001) return;
        var rect = PhysicalPlacement.GetRect(this);
        _zoom = zoom;
        ApplyScale();
        Place(rect.X, rect.Y);
        RepositionPopup();
        RenderOptions.SetBitmapScalingMode(ImageView, _zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        => SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));

    // ------------------------------------------------------------------ mouse / keyboard

    private Point ImagePoint(MouseEventArgs e) => e.GetPosition(PixelCanvas);

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && (IsInside(d, HoverBar) || IsInside(d, OcrDrawer))) return;
        if (e.ClickCount == 2)
        {
            if (Environment.TickCount64 - _createdAt > 600) Close();
            return;
        }

        var before = PhysicalPlacement.GetRect(this);
        var point = ImagePoint(e);
        try { DragMove(); } catch (InvalidOperationException) { }
        var after = PhysicalPlacement.GetRect(this);
        RepositionPopup();

        if (before != after) return;

        if (Layer.BarcodeAt(point) is { } qr)
        {
            WpfImaging.CopyTextToClipboard(qr.Text);
            ShowStatus($"已复制{qr.Format}内容", spinning: false, autoHide: true);
        }
        else if (Layer.BlockAt(point) is { } block)
        {
            WpfImaging.CopyTextToClipboard(block.Block.Translation);
            ShowStatus("已复制该段译文", spinning: false, autoHide: true);
        }
        else if (Layer.OcrLineAt(point) is { } ocrLine)
        {
            WpfImaging.CopyTextToClipboard(ocrLine.Text);
            ShowStatus("已复制该行文字", spinning: false, autoHide: true);
        }
    }

    private void OnMouseMoved(object sender, MouseEventArgs e)
    {
        var p = ImagePoint(e);
        var qr = Layer.BarcodeAt(p);
        var block = qr is null ? Layer.BlockAt(p) : null;
        var ocrLine = qr is null && block is null ? Layer.OcrLineAt(p) : null;

        Layer.HighlightBarcode = qr;
        Layer.Highlight = block;
        Layer.HighlightOcr = ocrLine;

        string? tip = null;
        if (qr is not null)
        {
            tip = $"{qr.Format}：{qr.Text}\n（单击复制内容）";
        }
        else if (block is not null)
        {
            tip = $"原文：{block.Block.Source}\n（单击复制译文）";
        }
        else if (ocrLine is not null)
        {
            tip = $"识别文本：{ocrLine.Text}\n（单击复制该行）";
        }

        if (!Equals(Root.ToolTip, tip)) Root.ToolTip = tip;
    }

    private static bool IsInside(DependencyObject child, DependencyObject parent)
    {
        for (var d = child; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, parent)) return true;
        }
        return false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.LeftShift or Key.RightShift when !ctrl: Layer.ShowOriginal = true; break;
            case Key.C when ctrl && shift: OnCopyText(this, e); break;
            case Key.C when ctrl: OnCopyImage(this, e); break;
            case Key.S when ctrl: OnSave(this, e); break;
            case Key.T when ctrl: _ = TranslateAsync(); break;
            case Key.O: _ = RunOfflineOcrAsync(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) Layer.ShowOriginal = OriginalToggle.IsChecked == true;
    }

    // ------------------------------------------------------------------ offline OCR & QR

    private async Task RunOfflineOcrAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _translating = true;
        _hasError = false;
        ShowStatus("离线识字 / 扫码中…", spinning: true);

        try
        {
            var result = await WindowsOfflineOcrService.RecognizeAsync(_image, cts.Token);
            _translating = false;

            Layer.SetOcrResults(result.Lines, result.Barcodes);

            var sb = new StringBuilder();
            foreach (var qr in result.Barcodes)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append($"[{qr.Format}] ").Append(qr.Text);
            }
            if (!string.IsNullOrWhiteSpace(result.FullText))
            {
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(result.FullText);
            }

            _ocrFullText = sb.ToString();
            _detectedQrUri = null;
            foreach (var qr in result.Barcodes)
            {
                if (QrCodeScanner.TryGetHttpUri(qr.Text, out var uri))
                {
                    _detectedQrUri = uri;
                    break;
                }
            }

            OpenQrUrlButton.Visibility = _detectedQrUri is not null ? Visibility.Visible : Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(_ocrFullText))
            {
                OcrDrawerToggle.Visibility = Visibility.Collapsed;
                OcrDrawer.Visibility = Visibility.Collapsed;
                ShowStatus("未识别到文字或二维码", spinning: false, autoHide: true);
                return;
            }

            // Copy primary text immediately to clipboard for instant Ctrl+V workflow.
            var textToCopy = result.Barcodes.Count == 1 && result.Lines.Count == 0
                ? result.Barcodes[0].Text
                : (!string.IsNullOrWhiteSpace(result.FullText) ? result.FullText : _ocrFullText);
            WpfImaging.CopyTextToClipboard(textToCopy);

            OcrResultBox.Text = _ocrFullText;
            OcrDrawerToggle.Visibility = Visibility.Visible;
            OcrDrawerToggle.IsChecked = true;
            OcrDrawer.Visibility = Visibility.Visible;
            HoverBar.Visibility = Visibility.Visible;

            var summaryParts = new List<string>(2);
            if (result.Barcodes.Count > 0) summaryParts.Add($"{result.Barcodes.Count} 个码");
            if (result.Lines.Count > 0) summaryParts.Add($"{result.Lines.Count} 行文字");
            ShowStatus($"识别完成 · {string.Join(" + ", summaryParts)}（已复制到剪贴板）", spinning: false, autoHide: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Offline OCR failed", ex);
            ShowError($"离线识别失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _translating = false;
        }
    }

    // ------------------------------------------------------------------ translation

    private async Task TranslateAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Layer.Clear();
        OcrDrawer.Visibility = Visibility.Collapsed;
        OcrDrawerToggle.IsChecked = false;
        _translating = true;
        _hasError = false;
        ShowStatus("AI 翻译中…", spinning: true);

        var settings = _host.Settings.Translation;
        var job = new ImageTranslationJob(_host.Translator, settings);
        var count = 0;
        try
        {
            await foreach (var block in job.RunAsync(_image, cts.Token))
            {
                var rendered = TranslationRenderer.Layout(_image, block, settings.OverlayFontFamily);
                if (rendered is null) continue;
                Layer.Add(rendered);
                count++;
                ShowStatus($"AI 翻译中… {count}", spinning: true);
            }

            _translating = false;
            var usage = job.TotalUsage is { } u ? $" · {u.TotalTokens:N0} tokens" : "";
            ShowStatus(count == 0 ? "未发现需要翻译的文字" : $"翻译完成 · {count} 段{usage}", spinning: false, autoHide: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (TranslationException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Translation failed", ex);
            ShowError($"翻译失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _translating = false;
        }
    }

    private void ShowError(string message)
    {
        _translating = false;
        _hasError = true;
        _lastError = message;
        var firstLine = message.Split('\n')[0];
        if (firstLine.Length > 60) firstLine = firstLine[..60] + "…";
        ShowStatus(firstLine + "（点击查看详情）", spinning: false);
        StatusPill.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0xB7, 0x1C, 0x1C));
        StatusPill.ToolTip = message;
    }

    private void ShowStatus(string text, bool spinning, bool autoHide = false)
    {
        StatusPill.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x21, 0x24));
        StatusPill.ToolTip = null;
        StatusText.Text = text;
        StatusPill.Visibility = Visibility.Visible;
        Spinner.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        if (spinning)
        {
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
        _statusTimer.Stop();
        if (autoHide) _statusTimer.Start();
        UpdatePopupOpen();
    }

    private void OnStatusClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_hasError)
        {
            StatusPill.Visibility = Visibility.Collapsed;
            UpdatePopupOpen();
            return;
        }

        FloatingPopup.IsOpen = false;
        OcrDrawerPopup.IsOpen = false;
        Topmost = false;
        var choice = MessageBox.Show(this, _lastError + "\n\n是：重新翻译    否：打开设置    取消：关闭此提示",
            "AiPhoto 提示", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        Topmost = true;
        UpdatePopupOpen();
        switch (choice)
        {
            case MessageBoxResult.Yes: _ = TranslateAsync(); break;
            case MessageBoxResult.No: _host.OpenSettings(); break;
        }
    }

    // ------------------------------------------------------------------ commands

    private Bgra32Image CurrentImage()
        => Layer.Blocks.Count > 0 && !Layer.ShowOriginal ? TranslationRenderer.RenderToImage(_image, Layer.Blocks) : _image;

    private void OnToggleOriginal(object sender, RoutedEventArgs e)
    {
        var show = sender == MenuToggleOriginal ? MenuToggleOriginal.IsChecked : OriginalToggle.IsChecked == true;
        OriginalToggle.IsChecked = show;
        MenuToggleOriginal.IsChecked = show;
        Layer.ShowOriginal = show;
    }

    private void OnRunOcr(object sender, RoutedEventArgs e) => _ = RunOfflineOcrAsync();

    private void OnToggleOcrDrawer(object sender, RoutedEventArgs e)
    {
        OcrDrawer.Visibility = OcrDrawerToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdatePopupOpen();
    }

    private void OnOpenQrUrl(object sender, RoutedEventArgs e)
    {
        if (_detectedQrUri is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_detectedQrUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to open QR URL: {ex.Message}");
        }
    }

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if (Layer.Blocks.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var b in Layer.Blocks) sb.AppendLine(b.Block.Translation);
            WpfImaging.CopyTextToClipboard(sb.ToString().TrimEnd());
            ShowStatus("已复制全部译文", spinning: false, autoHide: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_ocrFullText))
        {
            WpfImaging.CopyTextToClipboard(_ocrFullText);
            ShowStatus("已复制全部识别内容", spinning: false, autoHide: true);
        }
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        WpfImaging.CopyToClipboard(CurrentImage().ToBitmapSource());
        ShowStatus("已复制图片", spinning: false, autoHide: true);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var suggested = _host.SuggestFilePath(Layer.Blocks.Count > 0 ? "_译文" : null);
        var dialog = new SaveFileDialog
        {
            InitialDirectory = Path.GetDirectoryName(suggested),
            FileName = Path.GetFileName(suggested),
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
            DefaultExt = ".png",
            AddExtension = true,
        };
        FloatingPopup.IsOpen = false;
        OcrDrawerPopup.IsOpen = false;
        Topmost = false;
        var ok = dialog.ShowDialog(this) == true;
        Topmost = true;
        UpdatePopupOpen();
        if (!ok) return;
        try
        {
            WpfImaging.Save(CurrentImage().ToBitmapSource(), dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            _host.Notify("保存失败", ex.Message, isError: true);
        }
    }

    private void OnRetranslate(object sender, RoutedEventArgs e) => _ = TranslateAsync();

    private void OnResetZoom(object sender, RoutedEventArgs e) => SetZoom(1);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
