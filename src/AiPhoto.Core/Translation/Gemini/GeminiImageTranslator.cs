using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiPhoto.Core.Translation.Gemini;

/// <summary>
/// Translates screenshots with the Gemini <c>streamGenerateContent</c> REST API using structured JSON output.
/// The image is sent directly to the multimodal model; no OCR step is involved.
/// </summary>
public sealed class GeminiImageTranslator : IImageTranslator
{
    private readonly HttpClient _http;
    private readonly Func<GeminiOptions> _options;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _rejectsOptionalConfig = new();

    /// <param name="http">Client configured with the desired proxy/timeouts. Not disposed by this class.</param>
    /// <param name="options">Called for each request so that settings changes apply immediately.</param>
    public GeminiImageTranslator(HttpClient http, Func<GeminiOptions> options)
    {
        _http = http;
        _options = options;
    }

    public string DisplayName => "Google Gemini";

    public async IAsyncEnumerable<TranslationUpdate> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = _options();
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new TranslationException("尚未设置 Gemini API Key，请在设置中填写。");

        var parser = new IncrementalJsonArrayParser();
        TokenUsage? usage = null;
        string? finishReason = null;

        using var response = await SendWithFallbackAsync(request, options, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var chunk in ReadServerSentEventsAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            // OpenAI-style relays terminate the stream with a non-JSON "[DONE]" sentinel.
            if (chunk.AsSpan().Trim().SequenceEqual("[DONE]")) continue;

            ParsedChunk parsed;
            try
            {
                parsed = ParseChunk(chunk);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // Callers only expect TranslationException; previously a raw JsonException escaped here.
                throw new TranslationException($"Gemini 返回了无法解析的数据：{(chunk.Length > 200 ? chunk[..200] : chunk)}", ex);
            }
            if (parsed.Error is not null) throw new TranslationException(parsed.Error);
            usage = parsed.Usage ?? usage;
            finishReason = parsed.FinishReason ?? finishReason;

            if (parsed.Text.Length == 0) continue;
            foreach (var element in parser.Feed(parsed.Text))
            {
                var block = ParseBlock(element, request.ImageWidth, request.ImageHeight);
                if (block is not null) yield return new BlockTranslated(block);
            }
        }

        if (finishReason is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII")
            throw new TranslationException($"Gemini 拒绝了该请求（{finishReason}）。");

        yield return new TranslationCompleted(usage, finishReason);
    }

    /// <summary>Lists model ids that support generateContent (for the settings UI).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var options = _options();
        var models = new List<string>();
        string? pageToken = null;
        do
        {
            var url = $"{options.BaseUrl.TrimEnd('/')}/v1beta/models?pageSize=200" +
                      (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-goog-api-key", options.ApiKey);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw CreateApiException(resp.StatusCode, body);

            var json = JsonNode.Parse(body);
            foreach (var m in json?["models"]?.AsArray() ?? [])
            {
                var methods = m?["supportedGenerationMethods"]?.AsArray().Select(x => x?.GetValue<string>());
                var name = m?["name"]?.GetValue<string>();
                if (name is null || methods is null || !methods.Contains("generateContent")) continue;
                models.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name);
            }
            pageToken = json?["nextPageToken"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(pageToken));

        return models;
    }

    private async Task<HttpResponseMessage> SendWithFallbackAsync(TranslationRequest request, GeminiOptions options, CancellationToken ct)
    {
        // Not every model accepts every thinking level / media resolution. If the API rejects the
        // configuration, retry once with a plain request instead of failing the user's translation,
        // and remember the model so later requests skip the failing round-trip.
        var cacheKey = options.BaseUrl + "|" + options.Model + "|" + options.Thinking + "|" + options.MediaResolution;
        var useOptional = HasOptionalConfig(options) && !_rejectsOptionalConfig.ContainsKey(cacheKey);

        var response = await SendAsync(request, options, includeOptionalConfig: useOptional, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (useOptional && response.StatusCode == HttpStatusCode.BadRequest && LooksLikeConfigError(body))
        {
            response.Dispose();
            response = await SendAsync(request, options, includeOptionalConfig: false, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _rejectsOptionalConfig[cacheKey] = true;
                return response;
            }
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        var status = response.StatusCode;
        response.Dispose();
        throw CreateApiException(status, body);
    }

    private async Task<HttpResponseMessage> SendAsync(TranslationRequest request, GeminiOptions options, bool includeOptionalConfig, CancellationToken ct)
    {
        var url = $"{options.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(options.Model)}:streamGenerateContent?alt=sse";
        var body = BuildRequestBody(request, options, includeOptionalConfig);

        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("x-goog-api-key", options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        try
        {
            return await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException($"无法连接到 Gemini 服务：{ex.Message}。如在中国大陆使用，请在设置中配置代理或中转地址。", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException("请求 Gemini 超时，请检查网络或代理设置。", ex);
        }
    }

    public static JsonObject BuildRequestBody(TranslationRequest request, GeminiOptions options, bool includeOptionalConfig = true)
    {
        var generationConfig = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = BuildResponseSchema(),
        };
        if (options.Temperature is { } t) generationConfig["temperature"] = t;

        if (includeOptionalConfig)
        {
            var thinking = BuildThinkingConfig(options.Model, options.Thinking);
            if (thinking is not null) generationConfig["thinkingConfig"] = thinking;
            if (options.MediaResolution != MediaResolution.ModelDefault)
                generationConfig["mediaResolution"] = "MEDIA_RESOLUTION_" + options.MediaResolution.ToString().ToUpperInvariant();
        }

        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["text"] = TranslationPrompts.BuildSystemPrompt(request.TargetLanguage, options.SkipTextInTargetLanguage, options.CustomInstructions),
                }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = request.MimeType,
                            ["data"] = Convert.ToBase64String(request.ImageData),
                        },
                    },
                    new JsonObject { ["text"] = TranslationPrompts.BuildUserPrompt(request.TargetLanguage) }),
            }),
            ["generationConfig"] = generationConfig,
        };
    }

    private static JsonObject BuildResponseSchema() => new()
    {
        ["type"] = "ARRAY",
        ["items"] = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["box_2d"] = new JsonObject { ["type"] = "ARRAY", ["items"] = new JsonObject { ["type"] = "INTEGER" } },
                ["source"] = new JsonObject { ["type"] = "STRING" },
                ["translation"] = new JsonObject { ["type"] = "STRING" },
            },
            ["required"] = new JsonArray("box_2d", "source", "translation"),
            ["propertyOrdering"] = new JsonArray("box_2d", "source", "translation"),
        },
    };

    /// <summary>
    /// Gemini 2.x models use a token budget, Gemini 3+ use named levels. Unknown models get levels
    /// (and the request falls back to no thinking config if the API rejects it).
    /// </summary>
    public static JsonObject? BuildThinkingConfig(string model, ThinkingLevel level)
    {
        if (level == ThinkingLevel.ModelDefault) return null;
        var m = model.ToLowerInvariant();

        if (m.Contains("gemini-2.5") || m.Contains("gemini-2.0"))
        {
            var isPro = m.Contains("pro");
            var budget = level switch
            {
                ThinkingLevel.Minimal => isPro ? 128 : 0,
                ThinkingLevel.Low => 1024,
                ThinkingLevel.Medium => 4096,
                _ => 16384,
            };
            return new JsonObject { ["thinkingBudget"] = budget };
        }

        // Gemini 3 Pro models only accept "low" and "high".
        if (level == ThinkingLevel.Minimal && m.Contains("pro")) level = ThinkingLevel.Low;
        return new JsonObject { ["thinkingLevel"] = level.ToString().ToLowerInvariant() };
    }

    private static bool HasOptionalConfig(GeminiOptions o)
        => o.Thinking != ThinkingLevel.ModelDefault || o.MediaResolution != MediaResolution.ModelDefault;

    private static bool LooksLikeConfigError(string body)
        => body.Contains("thinking", StringComparison.OrdinalIgnoreCase)
        || body.Contains("media_resolution", StringComparison.OrdinalIgnoreCase)
        || body.Contains("mediaResolution", StringComparison.OrdinalIgnoreCase)
        || body.Contains("Invalid JSON payload", StringComparison.OrdinalIgnoreCase)
        || body.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    private static TranslationException CreateApiException(HttpStatusCode status, string body)
    {
        string? message = null;
        try { message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch (JsonException) { /* not JSON, e.g. an HTML error page from a proxy */ }

        message ??= body.Length > 300 ? body[..300] : body;
        var hint = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "（API Key 无效或没有权限）",
            HttpStatusCode.TooManyRequests => "（请求过于频繁或额度用尽）",
            HttpStatusCode.NotFound => "（模型名称不存在？）",
            _ => "",
        };
        return new TranslationException($"Gemini API 错误 {(int)status}{hint}：{message}") { StatusCode = (int)status };
    }

    internal readonly record struct ParsedChunk(string Text, TokenUsage? Usage, string? FinishReason, string? Error);

    internal static ParsedChunk ParseChunk(string json)
    {
        var root = JsonNode.Parse(json);

        // Non-streaming relays may return the whole response as an array of chunks.
        if (root is JsonArray array)
        {
            // (Local names must not clash with `u` declared further down in this method: CS0136.)
            var text = new StringBuilder();
            TokenUsage? mergedUsage = null;
            string? mergedFinish = null;
            foreach (var item in array)
            {
                if (item is null) continue;
                var p = ParseChunk(item.ToJsonString());
                if (p.Error is not null) return p;
                text.Append(p.Text);
                mergedUsage = p.Usage ?? mergedUsage;
                mergedFinish = p.FinishReason ?? mergedFinish;
            }
            return new ParsedChunk(text.ToString(), mergedUsage, mergedFinish, null);
        }

        var node = root;
        if (node?["error"]?["message"]?.GetValue<string>() is { } err) return new ParsedChunk("", null, null, err);

        var blockReason = node?["promptFeedback"]?["blockReason"]?.GetValue<string>();
        if (blockReason is not null) return new ParsedChunk("", null, null, $"Gemini 拒绝了该请求（{blockReason}）。");

        var sb = new StringBuilder();
        string? finish = null;
        var candidate = node?["candidates"]?.AsArray().FirstOrDefault();
        if (candidate is not null)
        {
            finish = candidate["finishReason"]?.GetValue<string>();
            foreach (var part in candidate["content"]?["parts"]?.AsArray() ?? [])
            {
                if (part?["thought"]?.GetValue<bool>() == true) continue;
                if (part?["text"]?.GetValue<string>() is { } text) sb.Append(text);
            }
        }

        TokenUsage? usage = null;
        if (node?["usageMetadata"] is JsonObject u)
        {
            static int I(JsonNode? n) => n?.GetValue<int>() ?? 0;
            usage = new TokenUsage(I(u["promptTokenCount"]), I(u["candidatesTokenCount"]), I(u["thoughtsTokenCount"]), I(u["totalTokenCount"]));
        }
        return new ParsedChunk(sb.ToString(), usage, finish, null);
    }

    internal static TranslationBlock? ParseBlock(string elementJson, int imageWidth, int imageHeight)
    {
        try
        {
            var node = JsonNode.Parse(elementJson);
            if (node is not JsonObject obj) return null;
            var source = obj["source"]?.GetValue<string>() ?? "";
            var translation = obj["translation"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(translation) && string.IsNullOrWhiteSpace(source)) return null;

            List<int>? box = null;
            if (obj["box_2d"] is JsonArray arr)
            {
                box = [];
                foreach (var v in arr)
                {
                    if (v is null) continue;
                    box.Add(v.GetValueKind() == JsonValueKind.Number ? (int)Math.Round(v.GetValue<double>()) : 0);
                }
            }
            return new TranslationBlock(BoundingBox.FromNormalized(box, imageWidth, imageHeight), source, translation);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Minimal SSE reader: yields the payload of each event (joined "data:" lines).</summary>
    internal static async IAsyncEnumerable<string> ReadServerSentEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var rawJson = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (rawJson)
            {
                // Raw (unframed) JSON may be pretty-printed over many lines: keep every line. A blank line only
                // ends the document if what we have so far is complete (relays emitting one object per paragraph).
                if (line.Length == 0 && IsCompleteJson(data))
                {
                    yield return data.ToString();
                    data.Clear();
                    rawJson = false;
                }
                else if (line.Length > 0)
                {
                    data.Append('\n').Append(line);
                }
                continue;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
            else if (data.Length == 0 && (line.TrimStart().StartsWith('{') || line.TrimStart().StartsWith('[')))
            {
                // Some relays strip the SSE framing and return raw JSON (e.g. the pretty-printed array returned by
                // streamGenerateContent without alt=sse). Previously only lines starting with '{' / '[' were kept,
                // which corrupted multi-line documents.
                rawJson = true;
                data.Append(line);
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }

    private static bool IsCompleteJson(StringBuilder text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text.ToString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
