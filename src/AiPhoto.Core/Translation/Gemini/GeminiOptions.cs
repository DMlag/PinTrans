namespace AiPhoto.Core.Translation.Gemini;

public enum ThinkingLevel
{
    /// <summary>Do not send a thinking configuration; the model decides.</summary>
    ModelDefault,
    /// <summary>As little thinking as the model allows (fastest).</summary>
    Minimal,
    Low,
    Medium,
    High,
}

public enum MediaResolution
{
    ModelDefault,
    Low,
    Medium,
    High,
}

public sealed record GeminiOptions
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    /// <summary>"gemini-flash-latest" is an alias that Google keeps pointing to the newest Flash model.</summary>
    public const string DefaultModel = "gemini-flash-latest";

    public string ApiKey { get; init; } = "";

    public string Model { get; init; } = DefaultModel;

    /// <summary>API root. Can point to a self-hosted relay/reverse proxy that forwards to Google.</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    public ThinkingLevel Thinking { get; init; } = ThinkingLevel.Minimal;

    /// <summary>Higher resolution reads small text better at the cost of more input tokens.</summary>
    public MediaResolution MediaResolution { get; init; } = MediaResolution.High;

    /// <summary>Optional sampling temperature. <c>null</c> keeps the model default (recommended for Gemini 3+).</summary>
    public double? Temperature { get; init; }

    /// <summary>Skip blocks that are already written in the target language.</summary>
    public bool SkipTextInTargetLanguage { get; init; } = true;

    /// <summary>Extra instructions appended to the system prompt (terminology, tone, ...).</summary>
    public string? CustomInstructions { get; init; }
}
