using System.Net;
using System.Text;
using AiPhoto.Core.Translation;
using AiPhoto.Core.Translation.Gemini;

namespace AiPhoto.Core.Tests.Translation;

public class GeminiErrorTests
{
    private const string FreeTierZeroLimit = """
        {
          "error": {
            "code": 429,
            "message": "You exceeded your current quota, please check your plan and billing details.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.QuotaFailure",
                "violations": [
                  {
                    "quotaMetric": "generativelanguage.googleapis.com/generate_content_free_tier_requests",
                    "quotaId": "GenerateRequestsPerDayPerProjectPerModel-FreeTier",
                    "quotaDimensions": { "location": "global", "model": "gemini-3-pro" },
                    "quotaValue": "0"
                  }
                ]
              },
              { "@type": "type.googleapis.com/google.rpc.Help", "links": [] },
              { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "21s" }
            ]
          }
        }
        """;

    private static string PerMinute(string retryDelay) => $$"""
        {
          "error": {
            "code": 429,
            "message": "Quota exceeded.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.QuotaFailure",
                "violations": [
                  {
                    "quotaMetric": "generativelanguage.googleapis.com/generate_content_paid_tier_requests",
                    "quotaId": "GenerateRequestsPerMinutePerProjectPerModel",
                    "quotaDimensions": { "model": "gemini-flash" },
                    "quotaValue": 1000
                  }
                ]
              },
              { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "{{retryDelay}}" }
            ]
          }
        }
        """;

    [Fact]
    public void Parse_FreeTierZeroLimit()
    {
        var e = GeminiError.Parse(FreeTierZeroLimit);
        Assert.Equal("RESOURCE_EXHAUSTED", e.Status);
        Assert.Equal(TimeSpan.FromSeconds(21), e.RetryDelay);
        var v = Assert.Single(e.Violations);
        Assert.Equal(0, v.Limit);
        Assert.Equal("gemini-3-pro", v.Model);
        Assert.True(e.IsFreeTier);
        Assert.True(e.HasZeroLimit);
        Assert.True(e.IsDailyLimit);

        var text = e.DescribeQuotaProblem();
        Assert.Contains("免费层", text);
        Assert.Contains("结算", text);
    }

    [Fact]
    public void Parse_PerMinute_NumericQuotaValue()
    {
        var e = GeminiError.Parse(PerMinute("3.5s"));
        Assert.Equal(TimeSpan.FromSeconds(3.5), e.RetryDelay);
        Assert.Equal(1000, Assert.Single(e.Violations).Limit);
        Assert.False(e.IsFreeTier);
        Assert.False(e.HasZeroLimit);
        Assert.Contains("每分钟", e.DescribeQuotaProblem());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("<html>429 Too Many Requests</html>")]
    public void Parse_Garbage_ReturnsEmpty(string body)
    {
        var e = GeminiError.Parse(body);
        Assert.Empty(e.Violations);
        Assert.Null(e.RetryDelay);
        Assert.False(string.IsNullOrWhiteSpace(e.DescribeQuotaProblem()));
    }

    [Theory]
    [InlineData("21s", 21.0)]
    [InlineData("0.25s", 0.25)]
    [InlineData("0s", 0.0)]
    public void ParseDuration_Valid(string text, double seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), GeminiError.ParseDuration(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("21")]
    [InlineData("abc s")]
    [InlineData("-1s")]
    public void ParseDuration_Invalid(string? text) => Assert.Null(GeminiError.ParseDuration(text));

    // ---------------------------------------------------------------- end to end

    private sealed class FakeHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(++Calls));
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Success()
    {
        const string chunk = """{"candidates":[{"content":{"parts":[{"text":"[{\"box_2d\":[0,0,500,500],\"source\":\"Hi\",\"translation\":\"你好\"}]"}]},"finishReason":"STOP"}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: " + chunk + "\r\n\r\n", Encoding.UTF8, "text/event-stream"),
        };
    }

    private static GeminiImageTranslator Translator(FakeHandler handler)
        => new(new HttpClient(handler), () => new GeminiOptions { ApiKey = "k", Model = "gemini-flash-latest", Thinking = ThinkingLevel.ModelDefault, MediaResolution = MediaResolution.ModelDefault });

    private static TranslationRequest Request() => new([1, 2, 3], "image/png", 100, 100, "简体中文");

    private static async Task<List<TranslationUpdate>> Collect(IAsyncEnumerable<TranslationUpdate> updates)
    {
        var list = new List<TranslationUpdate>();
        await foreach (var u in updates) list.Add(u);
        return list;
    }

    [Fact]
    public async Task ZeroLimit_FailsFastWithGuidance()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.TooManyRequests, FreeTierZeroLimit));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(Translator(handler).TranslateAsync(Request())));
        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("免费层", ex.Message);
        Assert.Contains("You exceeded your current quota", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransientRateLimit_IsRetried()
    {
        var handler = new FakeHandler(n => n == 1 ? Json(HttpStatusCode.TooManyRequests, PerMinute("0s")) : Success());
        var updates = await Collect(Translator(handler).TranslateAsync(Request()));
        Assert.Equal(2, handler.Calls);
        var block = Assert.IsType<BlockTranslated>(updates[0]).Block;
        Assert.Equal("你好", block.Translation);
    }

    [Fact]
    public async Task RateLimit_GivesUpAfterRetries()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.TooManyRequests, PerMinute("0s")));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(Translator(handler).TranslateAsync(Request())));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(3, handler.Calls); // first attempt + 2 retries
    }

    [Fact]
    public async Task LongRetryDelay_IsNotWaitedFor()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.TooManyRequests, PerMinute("60s")));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Collect(Translator(handler).TranslateAsync(Request())));
        Assert.Equal(1, handler.Calls);
        Assert.Contains("60 秒", ex.Message);
    }

    [Fact]
    public async Task TestGenerate_Reports429()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.TooManyRequests, FreeTierZeroLimit));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => Translator(handler).TestGenerateAsync());
        Assert.Equal(429, ex.StatusCode);
    }

    [Fact]
    public async Task TestGenerate_Success()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"candidates":[]}"""));
        await Translator(handler).TestGenerateAsync();
        Assert.Equal(1, handler.Calls);
    }
}
