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

public sealed record AiCompletionResult(string Text, string Model, TimeSpan Elapsed);

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
            var text = root.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?
                .Trim() ?? "";
            if (text.Length == 0)
            {
                throw new AiClientException("模型返回了空内容");
            }

            var model = root.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString() ?? options.Model
                : options.Model;
            return new AiCompletionResult(text, model, sw.Elapsed);
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

    public async Task<AiCompletionResult> StreamAsync(
        AiClientOptions options,
        IReadOnlyList<(string Role, string Content)> messages,
        Func<string, Task> onDeltaAsync,
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
        };
        ApplyThinkingOptions(payload, options);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        var full = new StringBuilder();
        var model = options.Model;
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

                var delta = ParseStreamDelta(data, ref model);
                if (delta.Length == 0) continue;
                full.Append(delta);
                await onDeltaAsync(delta).ConfigureAwait(false);
            }

            sw.Stop();
            var text = full.ToString().Trim();
            if (text.Length == 0)
            {
                throw new AiClientException("模型返回了空内容");
            }
            return new AiCompletionResult(text, model, sw.Elapsed);
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

    private static string ParseStreamDelta(string data, ref string model)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("model", out var modelElement))
        {
            model = modelElement.GetString() ?? model;
        }
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return "";
        }
        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
        {
            return "";
        }
        if (delta.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }
        return "";
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
