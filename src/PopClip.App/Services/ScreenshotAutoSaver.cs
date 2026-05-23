using System.IO;
using System.Text.RegularExpressions;
using PopClip.App.Config;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

/// <summary>截图自动保存的元信息载荷。
/// 展开模板时所需的全部上下文一次性传入，让 ScreenshotAutoSaver 不依赖任何全局状态，
/// 方便后续做单元测试与并发场景</summary>
internal readonly record struct ScreenshotAutoSaveContext(
    int Width,
    int Height,
    string ForegroundProcessName);

/// <summary>自动保存的结果。Path 不为空表示成功；否则 ErrorMessage 给出原因。
/// 失败由调用方决定是否降级（例如退到 SaveFileDialog 或仅提示）</summary>
internal readonly record struct ScreenshotAutoSaveResult(string? Path, string? ErrorMessage)
{
    public bool Success => !string.IsNullOrEmpty(Path);
    public static ScreenshotAutoSaveResult Failure(string message) => new(null, message);
}

/// <summary>把截图 PNG 字节按 settings 的目录 / 文件名模板自动落盘。
///
/// 设计原则：
/// 1. 完全无状态：所有外部依赖通过构造注入（settings / saveSettings / log），
///    不读全局；线程安全由 SaveAsync 内部 lock 保证 {counter} 自增不并发出错；
/// 2. 失败优雅：目录创建 / 写文件失败时返回 Failure，让 OcrCaptureCoordinator 决定要不要提示用户；
/// 3. 模板展开容错：未识别的占位符原样保留，避免误删用户加在文件名中的 {} 字面量。
///
/// 与 ScreenshotPreviewWindow 的"另存为"路径完全独立 —— 那是用户主动按按钮，
/// 这里是后台静默落盘，应优先保证不打扰用户</summary>
internal sealed class ScreenshotAutoSaver
{
    private static readonly Regex InvalidFileNameChars = new(
        "[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]",
        RegexOptions.Compiled);

    private readonly ILog _log;
    private readonly AppSettings _settings;
    private readonly Action? _saveSettings;
    private readonly object _counterLock = new();

    public ScreenshotAutoSaver(ILog log, AppSettings settings, Action? saveSettings)
    {
        _log = log;
        _settings = settings;
        _saveSettings = saveSettings;
    }

    /// <summary>把 PNG 字节写入按模板展开后的目标路径。
    /// 在调用方决定"截图后自动保存"开启时调用；本方法不再二次校验该开关，
    /// 让调用方语义更清晰（"我要保存"而非"看下要不要保存"）</summary>
    public async Task<ScreenshotAutoSaveResult> SaveAsync(byte[] pngBytes, ScreenshotAutoSaveContext context)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return ScreenshotAutoSaveResult.Failure("空截图字节");
        }

        string directory;
        try
        {
            directory = ResolveDirectory(_settings.ScreenshotSaveDirectory);
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            _log.Warn("auto-save dir create failed", ("err", ex.Message), ("dir", _settings.ScreenshotSaveDirectory));
            return ScreenshotAutoSaveResult.Failure("无法创建目录：" + ex.Message);
        }

        // 计数器自增前先校验日期：跨天时归零。串行修改保证 {counter} 唯一
        int counter;
        lock (_counterLock)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!string.Equals(_settings.ScreenshotAutoSaveCounterDate, today, StringComparison.Ordinal))
            {
                _settings.ScreenshotAutoSaveCounterDate = today;
                _settings.ScreenshotAutoSaveCounter = 0;
            }
            _settings.ScreenshotAutoSaveCounter++;
            counter = _settings.ScreenshotAutoSaveCounter;
        }

        var baseName = ExpandTemplate(_settings.ScreenshotFileNameTemplate, context, counter);
        baseName = SanitizeFileName(baseName);
        if (string.IsNullOrEmpty(baseName)) baseName = "ClipAura-Screenshot";
        var path = ChooseAvailablePath(directory, baseName);

        try
        {
            await File.WriteAllBytesAsync(path, pngBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("auto-save write failed", ("err", ex.Message), ("path", path));
            return ScreenshotAutoSaveResult.Failure("写入失败：" + ex.Message);
        }

        // 仅在写盘成功后持久化计数器；失败时不写回，避免 counter 跳号
        try { _saveSettings?.Invoke(); } catch { }

        _log.Info("screenshot auto saved",
            ("path", path), ("size", pngBytes.Length),
            ("w", context.Width), ("h", context.Height));
        return new ScreenshotAutoSaveResult(path, null);
    }

    /// <summary>把模板字符串中的 {date} / {time} / {datetime} / {counter} / {app} / {w} / {h}
    /// 替换为本次截图的具体值。未识别占位符原样保留，给用户的字面量留余地</summary>
    public static string ExpandTemplate(string template, ScreenshotAutoSaveContext context, int counter)
    {
        if (string.IsNullOrEmpty(template)) return "ClipAura-Screenshot";
        var now = DateTime.Now;
        var sb = new System.Text.StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var brace = template.IndexOf('{', i);
            if (brace < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, brace - i);
            var end = template.IndexOf('}', brace + 1);
            if (end < 0)
            {
                sb.Append(template, brace, template.Length - brace);
                break;
            }
            var token = template.Substring(brace + 1, end - brace - 1).Trim();
            sb.Append(ResolveToken(token, now, context, counter));
            i = end + 1;
        }
        return sb.ToString();
    }

    private static string ResolveToken(string token, DateTime now, ScreenshotAutoSaveContext context, int counter)
        => token.ToLowerInvariant() switch
        {
            "date" => now.ToString("yyyy-MM-dd"),
            "time" => now.ToString("HH-mm-ss"),
            "datetime" => now.ToString("yyyyMMdd-HHmmss"),
            "counter" => counter.ToString(),
            "app" => string.IsNullOrEmpty(context.ForegroundProcessName)
                ? "unknown"
                : Path.GetFileNameWithoutExtension(context.ForegroundProcessName),
            "w" => context.Width.ToString(),
            "h" => context.Height.ToString(),
            _ => "{" + token + "}",
        };

    /// <summary>把 %USERPROFILE% 等 Win32 环境变量展开为实际路径。
    /// 同时把 ~ 视作 %USERPROFILE%（兼容部分用户的跨平台习惯）</summary>
    public static string ResolveDirectory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Pictures", "ClipAura");
        }
        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (expanded.StartsWith("~", StringComparison.Ordinal))
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        }
        return Path.GetFullPath(expanded);
    }

    /// <summary>剔除文件名中的非法字符（: / \ ? * 等），用 _ 替代。
    /// 路径分隔符在 baseName 这里也视作非法 —— 模板里若用户写了 / 我们当作目录的意图，但当前简化方案不支持子目录</summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var cleaned = InvalidFileNameChars.Replace(name, "_");
        return cleaned.Trim().TrimEnd('.', ' ');
    }

    /// <summary>当目标文件已存在时追加 -2 / -3 ...，避免覆盖用户已有文件。
    /// 上限 99 个后缀，理论场景永远到不了；保险起见加上限避免无限循环</summary>
    private static string ChooseAvailablePath(string directory, string baseName)
    {
        var firstChoice = Path.Combine(directory, baseName + ".png");
        if (!File.Exists(firstChoice)) return firstChoice;
        for (var n = 2; n <= 99; n++)
        {
            var candidate = Path.Combine(directory, baseName + "-" + n + ".png");
            if (!File.Exists(candidate)) return candidate;
        }
        // 兜底：用毫秒时间戳后缀几乎不可能再撞
        return Path.Combine(directory, baseName + "-" + DateTimeOffset.Now.ToUnixTimeMilliseconds() + ".png");
    }
}
