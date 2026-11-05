using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Translation;
using AiPhoto.Core.Translation.Gemini;

namespace AiPhoto.Core.Tests.Translation;

public class GeminiImageTranslatorTests
{
    private static readonly byte[] ImageBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 250];

    private static TranslationRequest Request(int w = 1000, int h = 500)
        => new(ImageBytes, "image/png", w, h, "简体中文");

    private static GeminiOptions Options(string model = "gemini-2.5-flash", ThinkingLevel thinking = ThinkingLevel.Minimal,
        MediaResolution media = MediaResolution.High)
        => new() { ApiKey = "test-key", Model = model, Thinking = thinking, MediaResolution = media };

    // ---------------------------------------------------------------- BuildRequestBody

    [Fact]
    public void BuildRequestBody_ContainsImagePromptsAndSchema()
    {
        var body = GeminiImageTranslator.BuildRequestBody(Request(), Options() with { Temperature = 0.3, CustomInstructions = "Use formal tone." });

        var parts = body["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("user", body["contents"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("image/png", parts[0]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(ImageBytes), parts[0]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Contains("简体中文", parts[1]!["text"]!.GetValue<string>());

        var system = body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("简体中文", system);
        Assert.Contains("box_2d", system);
        Assert.Contains("Use formal tone.", system);

        var config = body["generationConfig"]!;
        Assert.Equal("application/json", config["responseMimeType"]!.GetValue<string>());
        var schema = config["responseSchema"]!;
        Assert.Equal("ARRAY", schema["type"]!.GetValue<string>());
        Assert.Equal("OBJECT", schema["items"]!["type"]!.GetValue<string>());
        var props = schema["items"]!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("box_2d"));
        Assert.True(props.ContainsKey("source"));
        Assert.True(props.ContainsKey("translation"));
        Assert.Equal("INTEGER", props["box_2d"]!["items"]!["type"]!.GetValue<string>());
        Assert.Equal(0.3, config["temperature"]!.GetValue<double>());
        Assert.Equal("MEDIA_RESOLUTION_HIGH", config["mediaResolution"]!.GetValue<string>());

        // The body must serialise to valid JSON.
        Assert.NotNull(JsonNode.Parse(body.ToJsonString()));
    }

    [Fact]
    public void BuildRequestBody_NoTemperatureByDefault()
    {
        var config = GeminiImageTranslator.BuildRequestBody(Request(), Options())["generationConfig"]!.AsObject();
        Assert.False(config.ContainsKey("temperature"));
    }

    [Theory]
    [InlineData("gemini-2.5-flash", ThinkingLevel.Minimal, 0)]
    [InlineData("gemini-2.5-flash-lite", ThinkingLevel.Minimal, 0)]
    [InlineData("gemini-2.5-pro", ThinkingLevel.Minimal, 128)]
    [InlineData("models/Gemini-2.5-Pro", ThinkingLevel.Minimal, 128)]
    [InlineData("gemini-2.5-flash", ThinkingLevel.Low, 1024)]
    [InlineData("gemini-2.5-flash", ThinkingLevel.Medium, 4096)]
    [InlineData("gemini-2.5-pro", ThinkingLevel.High, 16384)]
    public void ThinkingConfig_Gemini25_UsesBudget(string model, ThinkingLevel level, int budget)
    {
        var config = GeminiImageTranslator.BuildRequestBody(Request(), Options(model, level))["generationConfig"]!;
        var thinking = config["thinkingConfig"]!.AsObject();
        Assert.Equal(budget, thinking["thinkingBudget"]!.GetValue<int>());
        Assert.False(thinking.ContainsKey("thinkingLevel"));
    }

    [Theory]
    [InlineData("gemini-3-pro-preview", ThinkingLevel.Low, "low")]
    [InlineData("gemini-3-flash-preview", ThinkingLevel.Minimal, "minimal")]
    [InlineData("gemini-3-pro-preview", ThinkingLevel.High, "high")]
    [InlineData("gemini-3.5-flash", ThinkingLevel.Medium, "medium")]
    [InlineData("gemini-flash-latest", ThinkingLevel.Minimal, "minimal")]
    public void ThinkingConfig_Gemini3_UsesLevelString(string model, ThinkingLevel level, string expected)
    {
        var thinking = GeminiImageTranslator.BuildRequestBody(Request(), Options(model, level))["generationConfig"]!["thinkingConfig"]!.AsObject();
        Assert.Equal(expected, thinking["thinkingLevel"]!.GetValue<string>());
        Assert.False(thinking.ContainsKey("thinkingBudget"));
    }

    [Theory]
    [InlineData("gemini-2.5-flash")]
    [InlineData("gemini-3-pro-preview")]
    public void ThinkingConfig_ModelDefault_IsOmitted(string model)
    {
        var config = GeminiImageTranslator.BuildRequestBody(Request(), Options(model, ThinkingLevel.ModelDefault))["generationConfig"]!.AsObject();
        Assert.False(config.ContainsKey("thinkingConfig"));
        Assert.Null(GeminiImageTranslator.BuildThinkingConfig(model, ThinkingLevel.ModelDefault));
    }

    [Fact]
    public void MediaResolution_ModelDefault_IsOmitted()
    {
        var config = GeminiImageTranslator.BuildRequestBody(Request(), Options(media: MediaResolution.ModelDefault))["generationConfig"]!.AsObject();
        Assert.False(config.ContainsKey("mediaResolution"));
        var low = GeminiImageTranslator.BuildRequestBody(Request(), Options(media: MediaResolution.Low))["generationConfig"]!;
        Assert.Equal("MEDIA_RESOLUTION_LOW", low["mediaResolution"]!.GetValue<string>());
    }

    [Fact]
    public void IncludeOptionalConfigFalse_OmitsThinkingAndMediaResolution()
    {
        var config = GeminiImageTranslator.BuildRequestBody(Request(), Options("gemini-3-pro-preview", ThinkingLevel.High, MediaResolution.High) with { Temperature = 1 },
            includeOptionalConfig: false)["generationConfig"]!.AsObject();
        Assert.False(config.ContainsKey("thinkingConfig"));
        Assert.False(config.ContainsKey("mediaResolution"));
        Assert.True(config.ContainsKey("responseSchema"));
        Assert.True(config.ContainsKey("temperature"));
    }

    [Fact]
    public void Prompts_SkipRuleFollowsOption()
    {
        var skip = TranslationPrompts.BuildSystemPrompt("English", true, null);
        var keep = TranslationPrompts.BuildSystemPrompt("English", false, "   ");
        Assert.Contains("Omit blocks", skip);
        Assert.Contains("Include blocks", keep);
        Assert.DoesNotContain("Additional instructions", keep);
    }

    // ---------------------------------------------------------------- ParseChunk

    [Fact]
    public void ParseChunk_TextPartsAreConcatenated_ThoughtsSkipped()
    {
        const string json = """
            {"candidates":[{"content":{"role":"model","parts":[
                {"text":"I am thinking about it","thought":true},
                {"text":"[{\"a\":"},
                {"text":"1}"}
            ]}}]}
            """;
        var p = GeminiImageTranslator.ParseChunk(json);
        Assert.Equal("[{\"a\":1}", p.Text);
        Assert.Null(p.Usage);
        Assert.Null(p.FinishReason);
        Assert.Null(p.Error);
    }

    [Fact]
    public void ParseChunk_UsageAndFinishReason()
    {
        const string json = """
            {"candidates":[{"content":{"parts":[{"text":"]"}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":1290,"candidatesTokenCount":55,"thoughtsTokenCount":12,"totalTokenCount":1357},
             "modelVersion":"gemini-2.5-flash"}
            """;
        var p = GeminiImageTranslator.ParseChunk(json);
        Assert.Equal("]", p.Text);
        Assert.Equal("STOP", p.FinishReason);
        Assert.Equal(new TokenUsage(1290, 55, 12, 1357), p.Usage);
    }

    [Fact]
    public void ParseChunk_PartialUsage_DefaultsMissingCountsToZero()
    {
        var p = GeminiImageTranslator.ParseChunk("""{"usageMetadata":{"promptTokenCount":10}}""");
        Assert.Equal(new TokenUsage(10, 0, 0, 0), p.Usage);
        Assert.Equal("", p.Text);
    }

    [Fact]
    public void ParseChunk_Error()
    {
        var p = GeminiImageTranslator.ParseChunk("""{"error":{"code":500,"message":"Internal error","status":"INTERNAL"}}""");
        Assert.Equal("Internal error", p.Error);
    }

    [Fact]
    public void ParseChunk_BlockReason()
    {
        var p = GeminiImageTranslator.ParseChunk("""{"promptFeedback":{"blockReason":"SAFETY"},"usageMetadata":{"promptTokenCount":5}}""");
        Assert.NotNull(p.Error);
        Assert.Contains("SAFETY", p.Error);
    }

    [Fact]
    public void ParseChunk_ArrayRoot_MergesAllChunks()
    {
        const string json = """
            [
              {"candidates":[{"content":{"parts":[{"text":"[{\"x\":"}]}}]},
              {"candidates":[{"content":{"parts":[{"text":"1}]"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"totalTokenCount":4}}
            ]
            """;
        var p = GeminiImageTranslator.ParseChunk(json);
        Assert.Equal("[{\"x\":1}]", p.Text);
        Assert.Equal("STOP", p.FinishReason);
        Assert.Equal(3, p.Usage!.PromptTokens);
    }

    [Fact]
    public void ParseChunk_ArrayRootWithError_ReturnsError()
    {
        var p = GeminiImageTranslator.ParseChunk("""[{"candidates":[]},{"error":{"message":"boom"}}]""");
        Assert.Equal("boom", p.Error);
    }

    [Fact]
    public void ParseChunk_NoCandidates()
    {
        var p = GeminiImageTranslator.ParseChunk("""{"candidates":[]}""");
        Assert.Equal("", p.Text);
        Assert.Null(p.Error);
    }

    // ---------------------------------------------------------------- ParseBlock

    [Fact]
    public void ParseBlock_ConvertsBoxToPixels()
    {
        var b = GeminiImageTranslator.ParseBlock("""{"box_2d":[100,200,300,400],"source":"Hello","translation":"你好"}""", 1000, 500)!;
        Assert.Equal("Hello", b.Source);
        Assert.Equal("你好", b.Translation);
        Assert.Equal(PixelRect.FromLTRB(200, 50, 400, 150), b.Bounds);
    }

    [Fact]
    public void ParseBlock_FloatingPointBoxIsRounded()
    {
        var b = GeminiImageTranslator.ParseBlock("""{"box_2d":[100.4,199.6,300,400],"source":"a","translation":"b"}""", 1000, 1000)!;
        Assert.Equal(PixelRect.FromLTRB(200, 100, 400, 300), b.Bounds);
    }

    [Fact]
    public void ParseBlock_MissingOrBadBox_GivesNullBounds()
    {
        Assert.Null(GeminiImageTranslator.ParseBlock("""{"source":"a","translation":"b"}""", 100, 100)!.Bounds);
        Assert.Null(GeminiImageTranslator.ParseBlock("""{"box_2d":[1,2],"source":"a","translation":"b"}""", 100, 100)!.Bounds);
        Assert.Null(GeminiImageTranslator.ParseBlock("""{"box_2d":"oops","source":"a","translation":"b"}""", 100, 100)!.Bounds);
    }

    [Theory]
    [InlineData("""{"box_2d":[1,2,3,4],"source":"","translation":"  "}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"source": 5, "translation": "x"}""")]
    [InlineData("""{not json""")]
    [InlineData("""42""")]
    public void ParseBlock_Invalid_ReturnsNull(string json)
    {
        Assert.Null(GeminiImageTranslator.ParseBlock(json, 100, 100));
    }

    // ---------------------------------------------------------------- SSE

    private static async Task<List<string>> ReadSse(string text)
    {
        var list = new List<string>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await foreach (var e in GeminiImageTranslator.ReadServerSentEventsAsync(stream, CancellationToken.None))
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task Sse_MultipleEvents()
    {
        var events = await ReadSse("data: {\"a\":1}\r\n\r\ndata: {\"b\":2}\n\n: comment\nevent: message\ndata:{\"c\":3}\n\n");
        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"c\":3}"], events);
    }

    [Fact]
    public async Task Sse_MultipleDataLinesAreJoinedWithNewline()
    {
        var events = await ReadSse("data: {\"a\":\ndata: 1}\n\n");
        Assert.Single(events);
        Assert.Equal("{\"a\":\n1}", events[0]);
        Assert.Equal(1, JsonNode.Parse(events[0])!["a"]!.GetValue<int>());
    }

    [Fact]
    public async Task Sse_LastEventWithoutTrailingBlankLine()
    {
        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], await ReadSse("data: {\"a\":1}\n\ndata: {\"b\":2}"));
    }

    [Fact]
    public async Task Sse_RawJsonWithoutFraming()
    {
        Assert.Equal(["{\"a\":1}"], await ReadSse("{\"a\":1}\n"));
    }

    [Fact]
    public async Task Sse_RawPrettyPrintedJsonArray_IsReturnedAsOneDocument()
    {
        // Non-SSE streamGenerateContent responses (or relays that strip SSE framing) are a pretty-printed JSON array.
        const string raw = """
            [{
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "text": "[{\"source\":\"a\",\"translation\":\"b\"}]"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            ]
            """;
        var events = await ReadSse(raw);
        Assert.Single(events);
        var p = GeminiImageTranslator.ParseChunk(events[0]);
        Assert.Equal("[{\"source\":\"a\",\"translation\":\"b\"}]", p.Text);
        Assert.Equal("STOP", p.FinishReason);
    }

    // ---------------------------------------------------------------- TranslateAsync (end to end)

    private sealed class FakeHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request, body, Requests.Count);
        }
    }

    private static HttpResponseMessage Sse(params string[] eventPayloads)
    {
        var sb = new StringBuilder();
        foreach (var e in eventPayloads) sb.Append("data: ").Append(e).Append("\r\n\r\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }

    private static string TextEvent(string text, string? finish = null, string? usage = null)
    {
        var candidate = new JsonObject
        {
            ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
        };
        if (finish is not null) candidate["finishReason"] = finish;
        var root = new JsonObject { ["candidates"] = new JsonArray(candidate) };
        if (usage is not null) root["usageMetadata"] = JsonNode.Parse(usage);
        return root.ToJsonString();
    }

    private static async Task<List<TranslationUpdate>> Collect(IAsyncEnumerable<TranslationUpdate> updates)
    {
        var list = new List<TranslationUpdate>();
        await foreach (var u in updates) list.Add(u);
        return list;
    }

    private const string FullAnswer =
        """[{"box_2d":[100,100,200,500],"source":"Hello [1]","translation":"你好 {1}"},{"box_2d":[500,0,1000,1000],"source":"Bye \"x\"","translation":"再见"}]""";

    private static HttpResponseMessage SplitAnswerStream()
    {
        // Split the JSON answer at awkward places (inside strings, inside box_2d, between elements).
        var cuts = new[] { 0, 3, 17, 40, 41, 77, 95, FullAnswer.Length };
        var events = new List<string>();
        for (var i = 0; i < cuts.Length - 1; i++)
        {
            var piece = FullAnswer[cuts[i]..cuts[i + 1]];
            events.Add(i == cuts.Length - 2
                ? TextEvent(piece, "STOP", """{"promptTokenCount":1000,"candidatesTokenCount":80,"thoughtsTokenCount":0,"totalTokenCount":1080}""")
                : TextEvent(piece));
        }
        return Sse([.. events]);
    }

    [Fact]
    public async Task TranslateAsync_StreamsBlocksAndCompletes()
    {
        var handler = new FakeHandler((_, _, _) => SplitAnswerStream());
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options() with { BaseUrl = "https://relay.example.com/" });

        var updates = await Collect(translator.TranslateAsync(Request(1000, 500)));

        Assert.Equal(3, updates.Count);
        var b1 = Assert.IsType<BlockTranslated>(updates[0]).Block;
        Assert.Equal("Hello [1]", b1.Source);
        Assert.Equal("你好 {1}", b1.Translation);
        Assert.Equal(PixelRect.FromLTRB(100, 50, 500, 100), b1.Bounds);
        var b2 = Assert.IsType<BlockTranslated>(updates[1]).Block;
        Assert.Equal("Bye \"x\"", b2.Source);
        Assert.Equal(PixelRect.FromLTRB(0, 250, 1000, 500), b2.Bounds);
        var done = Assert.IsType<TranslationCompleted>(updates[2]);
        Assert.Equal("STOP", done.FinishReason);
        Assert.Equal(new TokenUsage(1000, 80, 0, 1080), done.Usage);

        var (req, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://relay.example.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", req.RequestUri!.ToString());
        Assert.Equal("test-key", req.Headers.GetValues("x-goog-api-key").Single());
        Assert.False(req.RequestUri.Query.Contains("key="), "API key must not be sent in the URL");
        Assert.Equal(0, JsonNode.Parse(body)!["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
    }

    [Fact]
    public async Task TranslateAsync_BlocksAreYieldedBeforeStreamEnds()
    {
        // Each element becomes available as soon as the chunk completing it was parsed.
        var handler = new FakeHandler((_, _, _) => SplitAnswerStream());
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        await using var e = translator.TranslateAsync(Request()).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        Assert.IsType<BlockTranslated>(e.Current);
    }

    [Fact]
    public async Task TranslateAsync_400ThinkingError_RetriesWithoutOptionalConfig()
    {
        var handler = new FakeHandler((_, _, n) => n == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":400,"message":"Thinking level is not supported for this model.","status":"INVALID_ARGUMENT"}}"""),
            }
            : SplitAnswerStream());
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options("gemini-3-pro-preview", ThinkingLevel.Minimal));

        var updates = await Collect(translator.TranslateAsync(Request()));

        Assert.Equal(2, handler.Requests.Count);
        var first = JsonNode.Parse(handler.Requests[0].Body)!["generationConfig"]!.AsObject();
        var second = JsonNode.Parse(handler.Requests[1].Body)!["generationConfig"]!.AsObject();
        Assert.True(first.ContainsKey("thinkingConfig"));
        Assert.False(second.ContainsKey("thinkingConfig"));
        Assert.False(second.ContainsKey("mediaResolution"));
        Assert.Equal(2, updates.OfType<BlockTranslated>().Count());
        Assert.IsType<TranslationCompleted>(updates[^1]);
    }

    [Fact]
    public async Task TranslateAsync_400UnrelatedError_IsNotRetried()
    {
        var handler = new FakeHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"code":400,"message":"Image is too large.","status":"INVALID_ARGUMENT"}}"""),
        });
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Image is too large.", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TranslateAsync_400FallbackAlsoFails_ReportsSecondError()
    {
        var handler = new FakeHandler((_, _, n) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(n == 1 ? "thinking budget invalid" : """{"error":{"message":"second failure"}}"""),
        });
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("second failure", ex.Message);
    }

    [Fact]
    public async Task TranslateAsync_401_ThrowsWithStatusCode()
    {
        var handler = new FakeHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"code":401,"message":"API key not valid.","status":"UNAUTHENTICATED"}}"""),
        });
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("API key not valid.", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TranslateAsync_NonJsonErrorBody_IsTruncatedIntoMessage()
    {
        var html = "<html>" + new string('x', 1000) + "</html>";
        var handler = new FakeHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(html) });
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.Equal(502, ex.StatusCode);
        Assert.True(ex.Message.Length < 400);
    }

    [Fact]
    public async Task TranslateAsync_MissingApiKey_Throws()
    {
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("should not be called"));
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options() with { ApiKey = " " });
        await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TranslateAsync_ErrorEventInStream_Throws()
    {
        var handler = new FakeHandler((_, _, _) => Sse(TextEvent("[{\"source\":\"a\",\"translation\":\"b\"}"), """{"error":{"message":"overloaded"}}"""));
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.Contains("overloaded", ex.Message);
    }

    [Fact]
    public async Task TranslateAsync_SafetyFinishReason_Throws()
    {
        var handler = new FakeHandler((_, _, _) => Sse(TextEvent("", "SAFETY")));
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.Contains("SAFETY", ex.Message);
    }

    [Fact]
    public async Task TranslateAsync_DoneSentinel_IsIgnored()
    {
        // e.g. an OpenAI-style relay terminating the stream with "[DONE]".
        var handler = new FakeHandler((_, _, _) => Sse(TextEvent("[]", "STOP"), "[DONE]"));
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var updates = await Collect(translator.TranslateAsync(Request()));
        Assert.IsType<TranslationCompleted>(Assert.Single(updates));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"candidates": {"oops": true}}""")]
    public async Task TranslateAsync_MalformedEvent_ThrowsTranslationException(string payload)
    {
        var handler = new FakeHandler((_, _, _) => Sse(payload));
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Sse_RawJsonObjectsSeparatedByBlankLines_AreSeparateEvents()
    {
        var events = await ReadSse("{\n  \"a\": 1\n}\n\n{\"b\":\n\n2}\n");
        Assert.Equal(2, events.Count);
        Assert.Equal(1, JsonNode.Parse(events[0])!["a"]!.GetValue<int>());
        Assert.Equal(2, JsonNode.Parse(events[1])!["b"]!.GetValue<int>());
    }

    [Fact]
    public async Task TranslateAsync_ConnectionFailure_IsWrapped()
    {
        var handler = new FakeHandler((_, _, _) => throw new HttpRequestException("No such host"));
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(translator.TranslateAsync(Request())));
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task TranslateAsync_Cancellation_IsNotWrapped()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHandler((_, _, _) => SplitAnswerStream());
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(translator.TranslateAsync(Request(), cts.Token)));
    }

    [Fact]
    public async Task ListModelsAsync_FiltersAndPaginates()
    {
        var handler = new FakeHandler((req, _, n) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(n == 1
                ? """{"models":[{"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent","countTokens"]},{"name":"models/embedding-001","supportedGenerationMethods":["embedContent"]}],"nextPageToken":"p2"}"""
                : """{"models":[{"name":"models/gemini-3-pro-preview","supportedGenerationMethods":["generateContent"]}]}"""),
        });
        var translator = new GeminiImageTranslator(new HttpClient(handler), () => Options());
        var models = await translator.ListModelsAsync();
        Assert.Equal(["gemini-2.5-flash", "gemini-3-pro-preview"], models);
        Assert.Contains("pageToken=p2", handler.Requests[1].Request.RequestUri!.Query);
    }
}
