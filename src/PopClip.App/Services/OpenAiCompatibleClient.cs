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
    string ThinkingMode,
    /// <summary>用户配置的最大输出 token。0=自动按思考强度选取（参见 OpenAiCompatibleClient.ResolveMaxTokens）</summary>
    int MaxOutputTokens = 0);

public sealed record AiCompletionResult(
    string Text,
    string Model,
    TimeSpan Elapsed,
    string Reasoning = "",
    int PromptTokens = 0,
    int CompletionTokens = 0,
    /// <summary>思考模型在 reasoning 阶段消耗的 token（completion_tokens_details.reasoning_tokens）。
    /// 0 表示 provider 未返回该字段或非思考模型</summary>
    int ReasoningTokens = 0);

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

        var maxTokensComplete = ResolveMaxTokens(options);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model.Trim(),
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["temperature"] = 0.2,
            ["max_tokens"] = maxTokensComplete,
            ["max_completion_tokens"] = maxTokensComplete,
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
            var (pt, ct2, rt) = ParseUsage(root);
            return new AiCompletionResult(text, model, sw.Elapsed, reasoning, pt, ct2, rt);
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
        // 流式禁用 HttpClient.Timeout：它会把"建立连接 + 持续接收所有数据"算到一起，
        // 模型在深度思考时哪怕一直在 push reasoning_content delta，也会被整体 timeout 截断。
        // 这里改用"idle 超时"——只要在 idleTimeoutSec 内还有新数据，就视为服务正常；
        // 真正卡死（连接挂起、服务端无响应）才会触发取消
        var idleTimeoutSec = Math.Clamp(options.TimeoutSeconds, 5, 600);
        using var http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(options.BaseUrl));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var maxTokens = ResolveMaxTokens(options);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model.Trim(),
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            // 思考模式下 temperature 等采样参数被 provider 忽略（DeepSeek 文档明确）；
            // 非思考模式下 0.2 给出稳定一致的转换/翻译/整理输出
            ["temperature"] = 0.2,
            // max_tokens 是 OpenAI legacy / DeepSeek 主用字段；
            // max_completion_tokens 是 OpenAI o-series 新字段（合计 reasoning + visible output）。两个都填，provider 各取所需
            ["max_tokens"] = maxTokens,
            ["max_completion_tokens"] = maxTokens,
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
        var reasoningTokens = 0;

        // idleCts 由 watchdog 在 idle 超时时触发；combined 把它与用户 ct 串联起来供 HttpClient/Stream 使用
        using var idleCts = new CancellationTokenSource();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
        var lastDataAtUtc = DateTime.UtcNow;
        var idleTimedOut = false;

        // watchdog：每 500ms 检查一次距上次"任何流式数据"的时间，超阈值就把 idleCts 触发掉
        using var watchdogCts = new CancellationTokenSource();
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!watchdogCts.IsCancellationRequested)
                {
                    await Task.Delay(500, watchdogCts.Token).ConfigureAwait(false);
                    if ((DateTime.UtcNow - lastDataAtUtc).TotalSeconds <= idleTimeoutSec) continue;
                    idleTimedOut = true;
                    try { idleCts.Cancel(); } catch (ObjectDisposedException) { }
                    return;
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, combined.Token).ConfigureAwait(false);
            // 收到 headers 视为服务"有响应"，重置 idle 起算点
            lastDataAtUtc = DateTime.UtcNow;
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(combined.Token).ConfigureAwait(false);
                throw new AiClientException(ToFriendlyError(res.StatusCode, body));
            }

            await using var stream = await res.Content.ReadAsStreamAsync(combined.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                combined.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(combined.Token).ConfigureAwait(false);
                // 读到任何一行（包括 SSE 心跳/空行）都视为服务在持续响应，重置 idle 计时
                lastDataAtUtc = DateTime.UtcNow;
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                var data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;

                var parsed = ParseStreamFrame(data, ref model);
                if (parsed.PromptTokens > 0) promptTokens = parsed.PromptTokens;
                if (parsed.CompletionTokens > 0) completionTokens = parsed.CompletionTokens;
                if (parsed.ReasoningTokens > 0) reasoningTokens = parsed.ReasoningTokens;

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
                // 思考阶段已经收到 reasoning_content，但最终 content 为空：
                // 通常是 max_tokens 把推理过程"截到一半"了，或服务端在思考阶段断流
                if (reasoning.Length > 0)
                {
                    var reasoningTokensHint = reasoningTokens > 0 ? $"，已耗 reasoning {reasoningTokens} tok" : "";
                    throw new AiClientException(
                        $"模型只输出了思考过程未给出最终回复（思考 {sw.Elapsed.TotalSeconds:0.0}s{reasoningTokensHint}）。"
                        + "可能原因：思考链过长被 max_tokens 截断、服务端中途断流，"
                        + "或当前模型未稳定支持思考模式。可在「设置 → AI」把「思考模式」切到「快速」、"
                        + "调大「单次最大输出 tokens」，或换一个非思考模型再试。");
                }
                throw new AiClientException("模型返回了空内容");
            }
            return new AiCompletionResult(text, model, sw.Elapsed, reasoning.ToString().Trim(), promptTokens, completionTokens, reasoningTokens);
        }
        catch (OperationCanceledException) when (idleTimedOut && !ct.IsCancellationRequested)
        {
            throw new AiClientException(
                $"模型 {idleTimeoutSec} 秒内没有任何输出，已判为无响应。"
                + "如果是深度思考模型，可在「设置 → AI」里调大「超时」时间；"
                + "或检查网络与 Base URL 是否可达。");
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
        finally
        {
            try { watchdogCts.Cancel(); } catch (ObjectDisposedException) { }
            try { await watchdog.ConfigureAwait(false); } catch { /* watchdog 自己已经处理 OCE */ }
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

    /// <summary>根据 ThinkingMode 选取一个稳妥的 max_tokens 默认值。
    /// 思考模型 reasoning_tokens 与 visible content 共用同一额度上限；
    /// 上调默认值，是为了避免 thinking 把额度吃光导致 content 为空（"模型只输出了思考过程"）。
    /// 用户在「设置 → AI」里显式设置过 AiMaxOutputTokens（即 MaxOutputTokens > 0）时，优先用用户值</summary>
    private static int ResolveMaxTokens(AiClientOptions options)
    {
        if (options.MaxOutputTokens > 0)
        {
            // 给到 [256, 262144]：上界 256K 已经覆盖 DeepSeek V4 Pro Max（384K）以下的绝大多数实际需求，
            // 进一步避免误设过大引发账单意外
            return Math.Clamp(options.MaxOutputTokens, 256, 262144);
        }
        var mode = options.ThinkingMode?.Trim() ?? "";
        if (mode.Equals("Deep", StringComparison.OrdinalIgnoreCase))
        {
            // DeepSeek reasoning_effort=max 或 OpenAI reasoning_effort=high：长思考链常见 10K-30K reasoning tokens
            return 32768;
        }
        if (mode.Equals("Fast", StringComparison.OrdinalIgnoreCase))
        {
            // 关闭/降低思考；轻量输出
            return 4096;
        }
        // Auto：DeepSeek 默认 thinking enabled + high effort；OpenAI 走 provider 默认
        return 16384;
    }

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
        int CompletionTokens,
        int ReasoningTokens);

    private static StreamFrame ParseStreamFrame(string data, ref string model)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("model", out var modelElement))
        {
            model = modelElement.GetString() ?? model;
        }

        var (pt, ctok, rt) = ParseUsage(root);

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return new StreamFrame("", "", pt, ctok, rt);
        }
        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
        {
            return new StreamFrame("", "", pt, ctok, rt);
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

        return new StreamFrame(content, reasoning, pt, ctok, rt);
    }

    /// <summary>解析 usage 帧。
    /// reasoning_tokens 优先取 completion_tokens_details.reasoning_tokens（OpenAI / DeepSeek 官方位置）；
    /// 兼容部分 provider 把它直接放在 usage 顶层的写法</summary>
    private static (int Prompt, int Completion, int Reasoning) ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (0, 0, 0);
        }
        var p = usage.TryGetProperty("prompt_tokens", out var pe) && pe.ValueKind == JsonValueKind.Number
            ? pe.GetInt32()
            : 0;
        var c = usage.TryGetProperty("completion_tokens", out var ce) && ce.ValueKind == JsonValueKind.Number
            ? ce.GetInt32()
            : 0;
        var r = 0;
        if (usage.TryGetProperty("completion_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("reasoning_tokens", out var re)
            && re.ValueKind == JsonValueKind.Number)
        {
            r = re.GetInt32();
        }
        else if (usage.TryGetProperty("reasoning_tokens", out var rt2)
                 && rt2.ValueKind == JsonValueKind.Number)
        {
            r = rt2.GetInt32();
        }
        return (p, c, r);
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
