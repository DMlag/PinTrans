using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AiPhoto.App.Diagnostics;
using AiPhoto.App.Imaging;
using AiPhoto.App.Infrastructure;
using AiPhoto.App.Services;
using AiPhoto.App.Translation;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Translation;
using Microsoft.Win32;

namespace AiPhoto.App.Pin;

/// <summary>
/// An image pinned on top of the desktop. When created by "translate", it sits exactly over the original
/// region and paints the translations in place of the original text.
/// </summary>
public partial class PinWindow : Window
{
    private readonly Bgra32Image _image;
    private readonly IAppHost _host;
    private readonly PixelRect _initialRect;
    private readonly bool _translateOnLoad;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly long _createdAt = Environment.TickCount64;

    private double _scale = 1;
    private double _zoom = 1;
    private int _borderPx = 1;
    private CancellationTokenSource? _cts;
    private bool _translating;
    private bool _hasError;

    public PinWindow(Bgra32Image image, PixelRect screenRect, IAppHost host, bool translate)
    {
        _image = image;
        _host = host;
        _initialRect = screenRect;
        _translateOnLoad = translate;
        InitializeComponent();

        ImageView.Source = image.ToBitmapSource();
        ImageView.Width = image.Width;
        ImageView.Height = image.Height;
        PixelCanvas.Width = image.Width;
        PixelCanvas.Height = image.Height;
        Layer.Width = image.Width;
        Layer.Height = image.Height;

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); if (!_translating && !_hasError) StatusPill.Visibility = Visibility.Collapsed; };

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
            if (_translateOnLoad) _ = TranslateAsync();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // Keep the image pixel-exact when the window moves to a monitor with another DPI.
            var current = PhysicalPlacement.GetRect(this);
            ApplyScale();
            Place(current.X, current.Y);
        }, DispatcherPriority.Background);

        MouseLeftButtonDown += OnMouseLeftDown;
        MouseMove += OnMouseMoved;
        MouseLeave += (_, _) => { HoverBar.Visibility = Visibility.Collapsed; Layer.Highlight = null; };
        MouseEnter += (_, _) => HoverBar.Visibility = Visibility.Visible;
        MouseWheel += OnMouseWheel;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Closed += (_, _) => { _cts?.Cancel(); _statusTimer.Stop(); };
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
        // Smooth scaling when zoomed out, crisp pixels when zoomed in.
        RenderOptions.SetBitmapScalingMode(ImageView, _zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        => SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));

    // ------------------------------------------------------------------ mouse / keyboard

    private Point ImagePoint(MouseEventArgs e) => e.GetPosition(PixelCanvas);

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInside(d, HoverBar)) return;
        if (e.ClickCount == 2)
        {
            // Ignore the second click of the double-click that may have created this pin (translate on selection).
            if (Environment.TickCount64 - _createdAt > 600) Close();
            return;
        }

        var before = PhysicalPlacement.GetRect(this);
        var point = ImagePoint(e);
        try { DragMove(); } catch (InvalidOperationException) { }
        var after = PhysicalPlacement.GetRect(this);

        if (before == after && Layer.BlockAt(point) is { } block)
        {
            // A click (not a drag) on a translated block copies it.
            WpfImaging.CopyTextToClipboard(block.Block.Translation);
            ShowStatus("已复制该段译文", spinning: false, autoHide: true);
        }
    }

    private void OnMouseMoved(object sender, MouseEventArgs e)
    {
        var block = Layer.BlockAt(ImagePoint(e));
        Layer.Highlight = block;
        var tip = block is null ? null : $"原文：{block.Block.Source}\n（单击复制译文）";
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
            default: return;
        }
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) Layer.ShowOriginal = OriginalToggle.IsChecked == true;
    }

    // ------------------------------------------------------------------ translation

    private async Task TranslateAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Layer.Clear();
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
            // Superseded by a new request or window closed.
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
        ShowStatus(message + "（点击重试，右键可打开设置）", spinning: false);
        StatusPill.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0xB7, 0x1C, 0x1C));
    }

    private void ShowStatus(string text, bool spinning, bool autoHide = false)
    {
        StatusPill.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x21, 0x24));
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
    }

    private void OnStatusClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_hasError) _ = TranslateAsync();
        else StatusPill.Visibility = Visibility.Collapsed;
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

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if (Layer.Blocks.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var b in Layer.Blocks) sb.AppendLine(b.Block.Translation);
        WpfImaging.CopyTextToClipboard(sb.ToString().TrimEnd());
        ShowStatus("已复制全部译文", spinning: false, autoHide: true);
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
        Topmost = false;
        var ok = dialog.ShowDialog(this) == true;
        Topmost = true;
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
