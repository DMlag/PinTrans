using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using AiPhoto.App.Imaging;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Settings;
using AiPhoto.Core.Stitching;
using AiPhoto.Core.Translation;

namespace AiPhoto.App.Translation;

/// <summary>
/// Prepares an image for the translator (down-scaling, encoding, splitting very tall images) and maps the
/// returned boxes back to original image pixels.
/// </summary>
public sealed class ImageTranslationJob
{
    private readonly IImageTranslator _translator;
    private readonly TranslationSettings _settings;

    public ImageTranslationJob(IImageTranslator translator, TranslationSettings settings)
    {
        _translator = translator;
        _settings = settings;
    }

    public TokenUsage? TotalUsage { get; private set; }

    /// <summary>Streams translated blocks whose bounds are in pixels of <paramref name="image"/>.</summary>
    public async IAsyncEnumerable<TranslationBlock> RunAsync(Bgra32Image image, [EnumeratorCancellation] CancellationToken ct)
    {
        var chunkHeight = LongImageSplitter.SuggestChunkHeight(image.Width);
        var chunks = image.Height > chunkHeight * 1.3
            ? LongImageSplitter.Split(image, chunkHeight)
            : [image.Bounds];

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var part = chunks.Count == 1 ? image : image.Crop(chunk);
            var upload = part.DownscaleToFit(Math.Max(256, _settings.MaxImageEdge));
            var (bytes, mime) = await Task.Run(() => Encode(upload), ct).ConfigureAwait(true);

            var request = new TranslationRequest(bytes, mime, upload.Width, upload.Height, _settings.TargetLanguage);
            var sx = (double)part.Width / upload.Width;
            var sy = (double)part.Height / upload.Height;

            await foreach (var update in _translator.TranslateAsync(request, ct).ConfigureAwait(true))
            {
                switch (update)
                {
                    case BlockTranslated { Block: var block }:
                        yield return block with { Bounds = MapBack(block.Bounds, sx, sy, chunk.Y, image) };
                        break;
                    case TranslationCompleted { Usage: { } usage }:
                        TotalUsage = TotalUsage is null ? usage : new TokenUsage(
                            TotalUsage.PromptTokens + usage.PromptTokens, TotalUsage.OutputTokens + usage.OutputTokens,
                            TotalUsage.ThinkingTokens + usage.ThinkingTokens, TotalUsage.TotalTokens + usage.TotalTokens);
                        break;
                }
            }
        }
    }

    private static PixelRect? MapBack(PixelRect? bounds, double sx, double sy, int offsetY, Bgra32Image image)
    {
        if (bounds is not { } b) return null;
        var r = PixelRect.FromLTRB(
            (int)Math.Floor(b.X * sx), (int)Math.Floor(b.Y * sy) + offsetY,
            (int)Math.Ceiling(b.Right * sx), (int)Math.Ceiling(b.Bottom * sy) + offsetY);
        r = r.Intersect(image.Bounds);
        return r.IsEmpty ? null : r;
    }

    /// <summary>PNG keeps text crisp; very large images use high-quality JPEG to keep uploads fast.</summary>
    private static (byte[] Bytes, string Mime) Encode(Bgra32Image image)
    {
        var bitmap = image.ToBitmapSource();
        if ((long)image.Width * image.Height <= 1_600_000) return (WpfImaging.EncodePng(bitmap), "image/png");

        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgr24, null, 0)));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        return (ms.ToArray(), "image/jpeg");
    }
}
