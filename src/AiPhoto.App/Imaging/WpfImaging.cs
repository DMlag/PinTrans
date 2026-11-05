using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiPhoto.App.Diagnostics;
using AiPhoto.Core.Imaging;

namespace AiPhoto.App.Imaging;

/// <summary>Conversions between Core image buffers and WPF bitmaps, plus encoding/clipboard helpers.</summary>
public static class WpfImaging
{
    public static BitmapSource ToBitmapSource(this Bgra32Image image)
    {
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bmp.Freeze();
        return bmp;
    }

    public static Bgra32Image ToBgra32Image(this BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }
        var image = new Bgra32Image(source.PixelWidth, source.PixelHeight);
        source.CopyPixels(image.Pixels, image.Stride, 0);
        return image;
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Saves according to the file extension (.png, .jpg/.jpeg, .bmp, .webp is not supported by WPF).</summary>
    public static void Save(BitmapSource source, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        // JPEG/BMP do not support alpha: flatten onto white.
        if (encoder is JpegBitmapEncoder or BmpBitmapEncoder) source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(source));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Puts the image on the clipboard as both a DIB (for most apps) and PNG (keeps exact pixels/alpha).</summary>
    public static bool CopyToClipboard(BitmapSource source)
    {
        var data = new DataObject();
        data.SetImage(source);
        data.SetData("PNG", new MemoryStream(EncodePng(source)), autoConvert: false);
        return TrySetClipboard(data);
    }

    public static bool CopyTextToClipboard(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        return TrySetClipboard(data);
    }

    private static bool TrySetClipboard(DataObject data)
    {
        // The clipboard may be briefly locked by another process (clipboard managers, RDP...).
        for (var i = 0; i < 10; i++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }
        Log.Warn("Clipboard is busy; copy failed.");
        return false;
    }

    public static Color ToColor(this Bgra32 c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static Rect ToRect(this PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    public static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? fallback : (Color)ColorConverter.ConvertFromString(text);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
