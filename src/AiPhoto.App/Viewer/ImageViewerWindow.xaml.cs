using System.Text;
using System.Windows;
using System.Windows.Input;
using AiPhoto.App.Diagnostics;
using AiPhoto.App.Imaging;
using AiPhoto.App.Services;
using AiPhoto.App.Translation;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Translation;
using Microsoft.Win32;

namespace AiPhoto.App.Viewer;

/// <summary>Shows a (long) screenshot with copy/save and whole-image AI translation.</summary>
public partial class ImageViewerWindow : Window
{
    private readonly Bgra32Image _image;
    private readonly IAppHost _host;
    private CancellationTokenSource? _cts;
    private double _zoom = 1;

    public ImageViewerWindow(Bgra32Image image, string title, IAppHost host)
    {
        _image = image;
        _host = host;
        InitializeComponent();
        Title = $"{title} — {image.Width} × {image.Height}";

        ImageView.Source = image.ToBitmapSource();
        ImageView.Width = image.Width;
        ImageView.Height = image.Height;
        Layer.Width = image.Width;
        Layer.Height = image.Height;

        Loaded += (_, _) => FitWidth();
        Closed += (_, _) => _cts?.Cancel();
        KeyDown += OnKeyDown;
    }

    private double DeviceScale => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    /// <summary>Zoom relative to physical pixels (1 = one image pixel per screen pixel).</summary>
    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.05, 8);
        var s = _zoom / DeviceScale;
        ContentScale.ScaleX = ContentScale.ScaleY = s;
        StatusText.Text = $"{_zoom:P0}";
    }

    private void FitWidth()
    {
        var available = Math.Max(100, Scroller.ActualWidth - SystemParameters.VerticalScrollBarWidth - 4);
        SetZoom(Math.Min(1, available * DeviceScale / _image.Width));
    }

    private void OnFitWidth(object sender, RoutedEventArgs e) => FitWidth();
    private void OnActualSize(object sender, RoutedEventArgs e) => SetZoom(1);

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        switch (e.Key)
        {
            case Key.C: OnCopy(this, e); break;
            case Key.S: OnSave(this, e); break;
            case Key.T: OnTranslate(this, e); break;
            case Key.D0 or Key.NumPad0: SetZoom(1); break;
            default: return;
        }
        e.Handled = true;
    }

    private Bgra32Image CurrentImage()
        => Layer.Blocks.Count > 0 && !Layer.ShowOriginal ? TranslationRenderer.RenderToImage(_image, Layer.Blocks) : _image;

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (WpfImaging.CopyToClipboard(CurrentImage().ToBitmapSource())) StatusText.Text = "已复制图片";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var suggested = _host.SuggestFilePath(Layer.Blocks.Count > 0 && !Layer.ShowOriginal ? "_长图_译文" : "_长图");
        var dialog = new SaveFileDialog
        {
            InitialDirectory = Path.GetDirectoryName(suggested),
            FileName = Path.GetFileName(suggested),
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
            DefaultExt = ".png",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            WpfImaging.Save(CurrentImage().ToBitmapSource(), dialog.FileName);
            StatusText.Text = "已保存";
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnTranslate(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Layer.Clear();
        OriginalToggle.IsChecked = false;
        Layer.ShowOriginal = false;
        TranslateButton.IsEnabled = false;
        StatusText.Text = "AI 翻译中…";

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
                StatusText.Text = $"AI 翻译中… {++count}";
            }
            var usage = job.TotalUsage is { } u ? $" · {u.TotalTokens:N0} tokens" : "";
            StatusText.Text = count == 0 ? "未发现需要翻译的文字" : $"翻译完成 · {count} 段{usage}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (TranslationException ex)
        {
            StatusText.Text = "翻译失败";
            MessageBox.Show(this, ex.Message, "翻译失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("Viewer translation failed", ex);
            StatusText.Text = "翻译失败";
            MessageBox.Show(this, ex.Message, "翻译失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    private void OnToggleOriginal(object sender, RoutedEventArgs e) => Layer.ShowOriginal = OriginalToggle.IsChecked == true;

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if (Layer.Blocks.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var b in Layer.Blocks) sb.AppendLine(b.Block.Translation);
        if (WpfImaging.CopyTextToClipboard(sb.ToString().TrimEnd())) StatusText.Text = "已复制全部译文";
    }
}
