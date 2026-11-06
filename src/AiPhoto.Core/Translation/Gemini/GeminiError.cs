using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiPhoto.Core.Translation.Gemini;

/// <summary>One quota that was exceeded (from <c>google.rpc.QuotaFailure</c>).</summary>
public sealed record QuotaViolation(string? Metric, string? QuotaId, string? Model, long? Limit)
{
    public bool IsFreeTier =>
        (QuotaId?.Contains("FreeTier", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Metric?.Contains("free_tier", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool IsPerDay => QuotaId?.Contains("PerDay", StringComparison.OrdinalIgnoreCase) ?? false;

    public bool IsPerMinute => QuotaId?.Contains("PerMinute", StringComparison.OrdinalIgnoreCase) ?? false;

    public string Describe()
    {
        var period = IsPerDay ? "每天" : IsPerMinute ? "每分钟" : "";
        var what = Metric switch
        {
            { } m when m.Contains("input_token", StringComparison.OrdinalIgnoreCase) => "输入 token",
            { } m when m.Contains("request", StringComparison.OrdinalIgnoreCase) => "请求数",
            _ => "用量",
        };
        var limit = Limit is { } l ? $" 上限 {l.ToString("N0", CultureInfo.InvariantCulture)}" : "";
        var model = string.IsNullOrEmpty(Model) ? "" : $"（{Model}）";
        return $"{period}{what}{limit}{model}";
    }
}

/// <summary>Structured view of a Gemini API error body (<c>{"error": {code, message, status, details}}</c>).</summary>
public sealed record GeminiError(string? Message, string? Status, TimeSpan? RetryDelay, IReadOnlyList<QuotaViolation> Violations)
{
    /// <summary>A quota whose limit is 0 cannot be fixed by waiting (model unavailable on this tier).</summary>
    public bool HasZeroLimit => Violations.Any(v => v.Limit == 0);

    public bool IsFreeTier => Violations.Any(v => v.IsFreeTier);

    public bool IsDailyLimit => Violations.Any(v => v.IsPerDay);

    public static GeminiError Parse(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            if (error is null) return new GeminiError(null, null, null, []);

            var message = error["message"]?.GetValue<string>();
            var status = error["status"]?.GetValue<string>();
            TimeSpan? retry = null;
            var violations = new List<QuotaViolation>();

            foreach (var detail in error["details"]?.AsArray() ?? [])
            {
                var type = detail?["@type"]?.GetValue<string>() ?? "";
                if (type.EndsWith("google.rpc.RetryInfo", StringComparison.Ordinal))
                {
                    retry = ParseDuration(detail?["retryDelay"]?.GetValue<string>());
                }
                else if (type.EndsWith("google.rpc.QuotaFailure", StringComparison.Ordinal))
                {
                    foreach (var v in detail?["violations"]?.AsArray() ?? [])
                    {
                        long? limit = null;
                        var raw = v?["quotaValue"];
                        if (raw is not null)
                        {
                            limit = raw.GetValueKind() == JsonValueKind.Number
                                ? raw.GetValue<long>()
                                : long.TryParse(raw.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
                        }
                        violations.Add(new QuotaViolation(
                            v?["quotaMetric"]?.GetValue<string>(),
                            v?["quotaId"]?.GetValue<string>(),
                            v?["quotaDimensions"]?["model"]?.GetValue<string>(),
                            limit));
                    }
                }
            }
            return new GeminiError(message, status, retry, violations);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new GeminiError(null, null, null, []);
        }
    }

    /// <summary>Parses protobuf JSON durations such as "21s" or "1.5s".</summary>
    internal static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.EndsWith('s')) return null;
        return double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>User-facing explanation (Chinese) for a 429 / RESOURCE_EXHAUSTED response.</summary>
    public string DescribeQuotaProblem()
    {
        const string billingHint =
            "这说明 API Key 所在的 Google Cloud 项目还在使用免费层，Ultra 附带的额度没有生效。" +
            "解决：在 Google AI Studio 的 API Keys 页面查看该 Key 所属项目，为该项目设置结算（选择激活 Google Developer Program 权益时关联的结算账号），" +
            "或在 Developer Program 权益页把额度关联到该项目所在的结算账号。";

        var first = Violations.FirstOrDefault(v => v.Limit == 0) ?? Violations.FirstOrDefault();
        var detail = first is null ? "" : $"超出的配额：{first.Describe()}。";

        if (IsFreeTier && HasZeroLimit)
            return $"该模型在免费层不可用（限额为 0）。{detail}{billingHint}也可以在设置中临时换用免费层可用的模型（如 gemini-flash-lite-latest）。";
        if (IsFreeTier)
            return $"已用完免费层额度。{detail}{billingHint}";
        if (IsDailyLimit)
            return $"已达到每日配额上限。{detail}请明天再试，或在 Google Cloud 控制台申请提高配额。";
        if (Violations.Count > 0)
            return $"请求太频繁，已达到配额上限。{detail}{(RetryDelay is { } d ? $"请约 {Math.Ceiling(d.TotalSeconds)} 秒后重试。" : "请稍后重试。")}";
        return "请求过于频繁，或账户额度/预付余额不足（检查 AI Studio 的结算页面：Cloud 额度是否已关联、预付余额是否为 0）。";
    }
}
