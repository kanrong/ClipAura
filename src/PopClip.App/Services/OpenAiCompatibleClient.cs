using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

public sealed record AiClientOptions(
    string BaseUrl,
    string Model,
    string ApiKey,
    int TimeoutSeconds,
    string ProviderPreset,
    string ThinkingMode);

public sealed record AiCompletionResult(
    string Text,
    string Model,
    TimeSpan Elapsed,
    string Reasoning = "",
    int PromptTokens = 0,
    int CompletionTokens = 0);

/// <summary>流式过程中分别回调"正文 delta"与"思考 delta"。
/// 思考通道仅在模型返回 reasoning_content 时被调用，UI 可独立渲染</summary>
public sealed record AiStreamCallbacks(
    Func<string, Task> OnContentDelta,
    Func<string, Task>? OnReasoningDelta = null);

public sealed class OpenAiCompatibleClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILog _log;

    public OpenAiCompatibleClient(ILog log) => _log = log;

    public async Task<AiCompletionResult> CompleteAsync(
        AiClientOptions options,
        IReadOnlyList<(string Role, string Content)> messages,
        CancellationToken ct)
    {
        Validate(options);
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 180)),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(options.BaseUrl));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model.Trim(),
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["temperature"] = 0.2,
            ["max_tokens"] = 1200,
        };
        ApplyThinkingOptions(payload, options);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            if (!res.IsSuccessStatusCode)
            {
                throw new AiClientException(ToFriendlyError(res.StatusCode, body));
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var message = root.GetProperty("choices")[0].GetProperty("message");
            var text = message.GetProperty("content").GetString()?.Trim() ?? "";
            if (text.Length == 0)
            {
                throw new AiClientException("模型返回了空内容");
            }

            var reasoning = "";
            if (message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                reasoning = rc.GetString() ?? "";
            }

            var model = root.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString() ?? options.Model
                : options.Model;
            var (pt, ct2) = ParseUsage(root);
            return new AiCompletionResult(text, model, sw.Elapsed, reasoning, pt, ct2);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiClientException("请求超时，请检查网络或调大超时时间");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn("ai http request failed", ("err", ex.Message));
            throw new AiClientException("网络请求失败：" + ex.Message);
        }
        catch (JsonException ex)
        {
            _log.Warn("ai response parse failed", ("err", ex.Message));
            throw new AiClientException("模型响应格式无法解析");
        }
    }

    public Task<AiCompletionResult> StreamAsync(
        AiClientOptions options,
        IReadOnlyList<(string Role, string Content)> messages,
        Func<string, Task> onDeltaAsync,
        CancellationToken ct)
        => StreamAsync(options, messages, new AiStreamCallbacks(onDeltaAsync), ct);

    public async Task<AiCompletionResult> StreamAsync(
        AiClientOptions options,
        IReadOnlyList<(string Role, string Content)> messages,
        AiStreamCallbacks callbacks,
        CancellationToken ct)
    {
        Validate(options);
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 180)),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(options.BaseUrl));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model.Trim(),
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["temperature"] = 0.2,
            ["max_tokens"] = 1200,
            ["stream"] = true,
            // 多数 provider 在 stream_options.include_usage=true 时会在最后一帧返回 usage
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true },
        };
        ApplyThinkingOptions(payload, options);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        var full = new StringBuilder();
        var reasoning = new StringBuilder();
        var model = options.Model;
        var promptTokens = 0;
        var completionTokens = 0;
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new AiClientException(ToFriendlyError(res.StatusCode, body));
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                var data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;

                var parsed = ParseStreamFrame(data, ref model);
                if (parsed.PromptTokens > 0) promptTokens = parsed.PromptTokens;
                if (parsed.CompletionTokens > 0) completionTokens = parsed.CompletionTokens;

                if (parsed.ReasoningDelta.Length > 0)
                {
                    reasoning.Append(parsed.ReasoningDelta);
                    if (callbacks.OnReasoningDelta is not null)
                    {
                        await callbacks.OnReasoningDelta(parsed.ReasoningDelta).ConfigureAwait(false);
                    }
                }

                if (parsed.ContentDelta.Length > 0)
                {
                    full.Append(parsed.ContentDelta);
                    await callbacks.OnContentDelta(parsed.ContentDelta).ConfigureAwait(false);
                }
            }

            sw.Stop();
            var text = full.ToString().Trim();
            if (text.Length == 0)
            {
                throw new AiClientException("模型返回了空内容");
            }
            return new AiCompletionResult(text, model, sw.Elapsed, reasoning.ToString().Trim(), promptTokens, completionTokens);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiClientException("请求超时，请检查网络或调大超时时间");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn("ai stream request failed", ("err", ex.Message));
            throw new AiClientException("网络请求失败：" + ex.Message);
        }
        catch (JsonException ex)
        {
            _log.Warn("ai stream parse failed", ("err", ex.Message));
            throw new AiClientException("流式响应格式无法解析");
        }
    }

    public async Task<AiCompletionResult> TestAsync(AiClientOptions options, CancellationToken ct)
        => await CompleteAsync(
            options,
            new[]
            {
                ("system", "You are a concise assistant. Reply with exactly: OK"),
                ("user", "Connection test"),
            },
            ct).ConfigureAwait(false);

    public static Uri BuildChatCompletionsUri(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim();
        if (trimmed.Length == 0) throw new AiClientException("Base URL 不能为空");
        trimmed = trimmed.TrimEnd('/');
        return new Uri(trimmed + "/chat/completions", UriKind.Absolute);
    }

    private static void Validate(AiClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl)) throw new AiClientException("Base URL 不能为空");
        if (string.IsNullOrWhiteSpace(options.Model)) throw new AiClientException("模型名不能为空");
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new AiClientException("API Key 不能为空");
    }

    private readonly record struct StreamFrame(
        string ContentDelta,
        string ReasoningDelta,
        int PromptTokens,
        int CompletionTokens);

    private static StreamFrame ParseStreamFrame(string data, ref string model)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("model", out var modelElement))
        {
            model = modelElement.GetString() ?? model;
        }

        var (pt, ctok) = ParseUsage(root);

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return new StreamFrame("", "", pt, ctok);
        }
        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
        {
            return new StreamFrame("", "", pt, ctok);
        }

        var content = "";
        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
        {
            content = c.GetString() ?? "";
        }

        // reasoning_content：DeepSeek-R1 / deepseek thinking 风格
        // reasoning：部分 OpenAI 兼容服务商的字段命名
        var reasoning = "";
        if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
        {
            reasoning = rc.GetString() ?? "";
        }
        else if (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
        {
            reasoning = r.GetString() ?? "";
        }

        return new StreamFrame(content, reasoning, pt, ctok);
    }

    private static (int Prompt, int Completion) ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }
        var p = usage.TryGetProperty("prompt_tokens", out var pe) && pe.ValueKind == JsonValueKind.Number
            ? pe.GetInt32()
            : 0;
        var c = usage.TryGetProperty("completion_tokens", out var ce) && ce.ValueKind == JsonValueKind.Number
            ? ce.GetInt32()
            : 0;
        return (p, c);
    }

    private static void ApplyThinkingOptions(Dictionary<string, object?> payload, AiClientOptions options)
    {
        var mode = options.ThinkingMode?.Trim();
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isDeepSeek = options.ProviderPreset.StartsWith("DeepSeek", StringComparison.OrdinalIgnoreCase)
            || options.BaseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

        if (isDeepSeek)
        {
            if (mode.Equals("Fast", StringComparison.OrdinalIgnoreCase))
            {
                payload["thinking"] = new { type = "disabled" };
            }
            else if (mode.Equals("Deep", StringComparison.OrdinalIgnoreCase))
            {
                payload["thinking"] = new { type = "enabled" };
                payload["reasoning_effort"] = "max";
            }
            return;
        }

        if (mode.Equals("Fast", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning_effort"] = "low";
        }
        else if (mode.Equals("Deep", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning_effort"] = "high";
        }
    }

    private static string ToFriendlyError(HttpStatusCode status, string body)
    {
        var message = ExtractErrorMessage(body);
        var suffix = string.IsNullOrWhiteSpace(message) ? "" : "：" + message;
        return status switch
        {
            HttpStatusCode.Unauthorized => "API Key 无效或未授权" + suffix,
            HttpStatusCode.Forbidden => "当前 API Key 没有访问权限" + suffix,
            HttpStatusCode.PaymentRequired => "账户余额不足或需要开通计费" + suffix,
            HttpStatusCode.NotFound => "模型或接口地址不存在" + suffix,
            (HttpStatusCode)429 => "请求过于频繁或达到限流" + suffix,
            _ => $"请求失败 ({(int)status} {status})" + suffix,
        };
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var message)) return message.GetString() ?? "";
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "";
            }
        }
        catch { }
        return body.Length > 240 ? body[..240] : body;
    }
}

public sealed class AiClientException : Exception
{
    public AiClientException(string message) : base(message) { }
}
