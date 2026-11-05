using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Translation;

/// <summary>One translated text block located in the source image.</summary>
/// <param name="Bounds">Location in image pixels, or <c>null</c> when the model did not return a usable box.</param>
public sealed record TranslationBlock(PixelRect? Bounds, string Source, string Translation);

/// <summary>Image to translate. <see cref="ImageData"/> is an encoded image (PNG/JPEG/WebP).</summary>
public sealed record TranslationRequest(
    byte[] ImageData,
    string MimeType,
    int ImageWidth,
    int ImageHeight,
    string TargetLanguage);

public sealed record TokenUsage(int PromptTokens, int OutputTokens, int ThinkingTokens, int TotalTokens);

/// <summary>Streaming updates produced while a translation is running.</summary>
public abstract record TranslationUpdate;

public sealed record BlockTranslated(TranslationBlock Block) : TranslationUpdate;

public sealed record TranslationCompleted(TokenUsage? Usage, string? FinishReason) : TranslationUpdate;

/// <summary>
/// A provider that translates the text inside an image in a single multimodal call.
/// Implementations: <see cref="Gemini.GeminiImageTranslator"/>. Future providers (OpenAI, Claude, local models)
/// only need to implement this interface.
/// </summary>
public interface IImageTranslator
{
    string DisplayName { get; }

    IAsyncEnumerable<TranslationUpdate> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
}

public class TranslationException : Exception
{
    public TranslationException(string message, Exception? inner = null) : base(message, inner) { }

    public int? StatusCode { get; init; }
}
