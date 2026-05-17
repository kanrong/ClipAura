using PopClip.Core.Logging;

namespace PopClip.App.Ocr;

/// <summary>OCR provider 注册中心，负责：
/// (1) 持有所有已注册 provider 实例（在 AppHost 装配时一次性 new 好）；
/// (2) 把"用户偏好 id"翻译成"当前活跃 provider"（不可用时自动 fallback 到优先级最高的可用项）；
/// (3) 启动期输出一份诊断日志，让用户在 log 里能直接看到三个 provider 谁可用、谁缺什么。
///
/// 不负责：lazy 加载 native（每个 provider 自己管），也不负责 settings 持久化（settings 通过
/// 构造函数注入的 Func&lt;string?&gt; 实时读取，避免循环依赖）。</summary>
public sealed class OcrProviderRegistry : IDisposable
{
    private readonly ILog _log;
    private readonly Func<string?> _preferredIdReader;
    private readonly List<IOcrProvider> _providers;

    /// <param name="preferredIdReader">每次取 AppSettings.OcrProviderId 的委托。
    /// 用委托而不是直接传字符串：用户可以在设置 UI 里改完立即生效，不需要重启 Registry。
    /// 返回 null/empty 表示"自动"模式，按 Priority 选可用的第一个。</param>
    public OcrProviderRegistry(ILog log, Func<string?> preferredIdReader, IEnumerable<IOcrProvider> providers)
    {
        _log = log;
        _preferredIdReader = preferredIdReader;
        _providers = providers.ToList();
        LogStartupDiagnostics();
    }

    /// <summary>所有已注册 provider，按 Priority 倒序。UI 列表 / 自动选择都按这个顺序。</summary>
    public IReadOnlyList<IOcrProvider> All =>
        _providers.OrderByDescending(p => p.Priority).ToList();

    /// <summary>选择当前应该被使用的 provider：
    /// (1) 用户显式偏好 + 该 provider 可用 → 用它；
    /// (2) 否则按 Priority 倒序找第一个 IsAvailable=true 的；
    /// (3) 全部都不可用 → 返回 null（调用方应给出"OCR 不可用"提示）。</summary>
    public IOcrProvider? PickActive()
    {
        var preferred = _preferredIdReader();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = _providers.FirstOrDefault(p =>
                string.Equals(p.Id, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is { IsAvailable: true })
                return match;
            // 用户选了某 provider 但它现在不可用：日志记一行让用户能查到原因，并 fallback 到自动模式
            if (match is not null)
                _log.Warn("ocr preferred provider unavailable, fallback to auto",
                    ("id", preferred), ("reason", match.UnavailableReason ?? "unknown"));
            else
                _log.Warn("ocr preferred provider not registered, fallback to auto", ("id", preferred));
        }

        return _providers
            .Where(p => p.IsAvailable)
            .OrderByDescending(p => p.Priority)
            .FirstOrDefault();
    }

    /// <summary>启动时给所有可用 provider 预热（让 native 加载与用户首次截图并行）。
    /// 当前实现：只预热"活跃 provider"，其他 provider 即使可用也按需加载，避免一次性占用 ~50 MB 多份。</summary>
    public void PrewarmActiveInBackground()
    {
        var active = PickActive();
        active?.PrewarmInBackground();
    }

    private void LogStartupDiagnostics()
    {
        foreach (var p in _providers.OrderByDescending(p => p.Priority))
        {
            if (p.IsAvailable)
            {
                _log.Info("ocr provider registered",
                    ("id", p.Id), ("name", p.DisplayName), ("priority", p.Priority), ("available", true));
            }
            else
            {
                _log.Info("ocr provider registered (unavailable)",
                    ("id", p.Id), ("name", p.DisplayName),
                    ("priority", p.Priority), ("reason", p.UnavailableReason ?? "unknown"));
            }
        }

        var active = PickActive();
        if (active is null)
            _log.Warn("ocr no available provider; OCR feature will be disabled until user installs one");
        else
            _log.Info("ocr active provider selected", ("id", active.Id), ("name", active.DisplayName));
    }

    public void Dispose()
    {
        foreach (var p in _providers)
        {
            try { p.Dispose(); }
            catch (Exception ex) { _log.Debug("ocr provider dispose swallowed", ("id", p.Id), ("err", ex.Message)); }
        }
        _providers.Clear();
    }
}
