using AiPhoto.Core.Imaging;
using Vortice.WIC;

namespace AiPhoto.App.Capture;

/// <summary>Saves scRGB HDR images as JPEG XR (.jxr), the format Windows itself uses for HDR screenshots.</summary>
public static class JxrEncoder
{
    public static void Save(RgbaHalfImage image, string path)
    {
        using var factory = new IWICImagingFactory();
        using var file = File.Create(path);
        using var stream = factory.CreateStream(file);
        using var encoder = factory.CreateEncoder(ContainerFormatGuids.Wmp);
        encoder.Initialize(stream, BitmapEncoderCacheOption.NoCache);

        using var frame = encoder.CreateNewFrame(out var options);
        // Note: in Vortice, IWICBitmapFrameEncode.Initialize/SetSize/WritePixels return a Result instead of
        // throwing, so failures must be checked explicitly (otherwise a broken/empty file is written silently).
        frame.Initialize(options).CheckError();
        options?.Dispose();
        frame.SetSize((uint)image.Width, (uint)image.Height).CheckError();

        var format = PixelFormat.Format64bppRGBAHalf;
        frame.SetPixelFormat(ref format);
        if (format != PixelFormat.Format64bppRGBAHalf)
            throw new NotSupportedException("The JPEG XR encoder does not accept 64bpp RGBA half-float pixels.");

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Pixels.AsSpan());
        frame.WritePixels((uint)image.Height, (uint)image.Stride, (ReadOnlySpan<byte>)bytes).CheckError();
        frame.Commit();
        encoder.Commit();
    }
}
