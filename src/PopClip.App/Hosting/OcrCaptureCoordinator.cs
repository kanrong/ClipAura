using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Hooks.Window;
using PopClip.Ocr.Layout;
using PopClip.Uia.Clipboard;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Hosting;

/// <summary>把"全局热键 → 选区窗 → 截屏 → OCR → 浮窗"串成一条独立链路。
/// 不进入选区状态机，是手动触发的第三采集路径。
///
/// 同一时刻只允许有一个截图会话：用户在窗口已经打开时再次按热键不会叠开第二个窗口。
///
/// OCR 后端通过 <see cref="OcrProviderRegistry"/> 动态选择：用户在设置里选哪个就用哪个，
/// "自动"模式按 Priority 倒序选第一个 IsAvailable 的。每次 Trigger / RecognizeBitmapAsync
/// 都重新 PickActive，所以用户在运行期间切换 provider / 修复缺失文件后无需重启即可生效。</summary>
internal sealed class OcrCaptureCoordinator
{
    private readonly ILog _log;
    private readonly OcrProviderRegistry _registry;
    private readonly SelectionSessionManager _session;
    private readonly ClipboardWriter _clipboard;
    private readonly ClipboardAccess _clipboardAccess;
    private readonly FloatingToolbar _toolbar;

    /// <summary>结果气泡 presenter。声明为具体类型而非 IInlineBubblePresenter，
    /// 是因为 OCR Quick 模式需要 ShowStaticAt 这个不在 Core 接口里的 anchor 重载——
    /// OCR 截图框就是 anchor，浮窗根本不会出现，无法依赖 GetCurrentBubbleAnchor</summary>
    private readonly FloatingToolbarBubblePresenter? _bubble;

    /// <summary>读取 OcrResultMode / OcrResultWindowBordered 等运行时偏好。
    /// 引用同一份主程序的 AppSettings 实例，设置面板写入后这里也能直接看到</summary>
    private readonly AppSettings _settings;
    private readonly AiTextService _aiText;

    /// <summary>"打开设置面板并定位到某个分页"的跨层回调。
    /// 由 AppHost 注入，参数为页面 tag（如 "AI" / "Ocr"），方便结果窗里的
    /// "启用 AI / 调整 OCR" 类引导按钮直达对应设置位置。可空：宿主未传入时按钮仅在 Toast 提示</summary>
    private readonly Action<string>? _openSettings;

    private OcrSelectionWindow? _currentWindow;
    private OcrResultWindow? _resultWindow;

    public OcrCaptureCoordinator(
        ILog log,
        OcrProviderRegistry registry,
        SelectionSessionManager session,
        ClipboardWriter clipboard,
        ClipboardAccess clipboardAccess,
        FloatingToolbar toolbar,
        AppSettings settings,
        AiTextService aiText,
        FloatingToolbarBubblePresenter? bubble = null,
        Action<string>? openSettings = null)
    {
        _log = log;
        _registry = registry;
        _session = session;
        _clipboard = clipboard;
        _clipboardAccess = clipboardAccess;
        _toolbar = toolbar;
        _settings = settings;
        _aiText = aiText;
        _bubble = bubble;
        _openSettings = openSettings;
    }

    public void Trigger()
    {
        var provider = PickActiveOrNotify();
        if (provider is null) return;

        // 提前预热活跃 provider：用户从按热键到松开拖框通常 1~2 秒，
        // RapidOcr / ChineseLite 冷启动 ~500 ms-1 秒（ONNX session 加载），
        // WeChat 是 no-op（wcocr 内部按需 spawn 子进程），
        // 在蒙层弹出的同时后台加载可以把感知延迟压到接近 0
        provider.PrewarmInBackground();

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            if (_currentWindow is not null)
            {
                try { _currentWindow.Activate(); } catch { }
                return;
            }

            var window = new OcrSelectionWindow(_log);
            _currentWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_currentWindow, window)) _currentWindow = null;
            };
            window.Cancelled += () => _log.Info("ocr capture cancelled");
            window.RegionSelected += physical => _ = RunCaptureAsync(physical);
            window.Show();
        });
    }

    public void TriggerClipboardImage(SelectionRect anchorRect)
    {
        var provider = PickActiveOrNotify();
        if (provider is null) return;
        provider.PrewarmInBackground();
        _ = Task.Run(() => RunClipboardImageAsync(anchorRect));
    }

    private async Task RunCaptureAsync(Rectangle physical)
    {
        if (physical.Width <= 0 || physical.Height <= 0) return;
        try
        {
            // 关键缓冲：选区窗 Hide/Close 后 DWM 需要 1~2 帧合成才会从屏幕移除蒙层（@60Hz 约 32 ms/帧），
            // 此时立刻 CopyFromScreen 会截到半透明黑色蒙层覆盖的内容，导致 OCR 输出乱码。
            // 80 ms ≈ 5 帧，足够覆盖普通显示刷新率甚至 30Hz 远程会话
            await Task.Delay(80).ConfigureAwait(false);

            using var bitmap = new Bitmap(physical.Width, physical.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(physical.Left, physical.Top, 0, 0, bitmap.Size);
            }

            var anchorRect = new SelectionRect(physical.Left, physical.Top, physical.Right, physical.Bottom);
            await RecognizeBitmapAsync(bitmap, anchorRect, $"{physical.Width}x{physical.Height}").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("ocr capture timed out");
            WpfApplication.Current.Dispatcher.Invoke(() =>
                MessageBox.Show("OCR 超时，请缩小截图区域后再试。", "OCR 超时", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        catch (Exception ex)
        {
            _log.Error("ocr capture failed", ex);
            WpfApplication.Current.Dispatcher.Invoke(() =>
                MessageBox.Show("OCR 失败：" + ex.Message, "OCR 错误", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private async Task RunClipboardImageAsync(SelectionRect anchorRect)
    {
        try
        {
            var pngBytes = _clipboardAccess.GetImagePngBytes();
            if (pngBytes is null || pngBytes.Length == 0)
            {
                ShowAnchoredToast("剪贴板中没有图片", anchorRect, isError: true, durationMs: 2500);
                return;
            }

            using var ms = new MemoryStream(pngBytes);
            using var decoded = new Bitmap(ms);
            using var bitmap = new Bitmap(decoded);
            await RecognizeBitmapAsync(bitmap, anchorRect, "clipboard-image").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("ocr clipboard image timed out");
            WpfApplication.Current.Dispatcher.Invoke(() =>
                MessageBox.Show("OCR 超时，请裁小剪贴板图片后再试。", "OCR 超时", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        catch (Exception ex)
        {
            _log.Error("ocr clipboard image failed", ex);
            WpfApplication.Current.Dispatcher.Invoke(() =>
                MessageBox.Show("OCR 失败：" + ex.Message, "OCR 错误", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private async Task RecognizeBitmapAsync(Bitmap bitmap, SelectionRect anchorRect, string source)
    {
        var provider = _registry.PickActive();
        if (provider is null)
        {
            NotifyNoProvider();
            return;
        }

        // 引擎未就绪时识别可能耗时 1~3 秒（含 ONNX 三个 session 冷启动 / WeChatOCR 子进程 spawn），
        // 先给个轻量 toast 让用户感知到正在工作；已就绪时跳过 toast 避免噪音
        if (!provider.IsEngineReady)
        {
            ShowAnchoredToast($"文字识别中… ", anchorRect, isError: false, durationMs: 1500);
        }

        // Bitmap → PNG bytes：跨 provider 统一输入。PNG 编码 ~10-30 ms，相对 OCR 本身的 300 ms 可忽略
        byte[] pngBytes;
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            pngBytes = ms.ToArray();
        }

        // 超时给 30 秒：冷启动 + 大图识别 + WeChat 子进程通信极端情况下也够用
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        OcrResult result;
        try
        {
            result = await provider.RecognizeAsync(pngBytes, cts.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _log.Error($"ocr provider failed: {provider.Id}", ex);
            ShowAnchoredToast($"OCR 失败: {ex.Message}", anchorRect, isError: true, durationMs: 4000);
            return;
        }
        var layout = OcrLayoutAnalyzer.Analyze(result);
        var fullText = !string.IsNullOrWhiteSpace(layout.PlainText)
            ? layout.PlainText.Trim()
            : result.FullText.Trim();
        _log.Info("ocr recognized",
            ("len", fullText.Length), ("blocks", result.Blocks.Count),
            ("regions", layout.Regions.Count),
            ("source", source), ("provider", provider.Id));

        if (string.IsNullOrEmpty(fullText) || result.Blocks.Count == 0)
        {
            // 浮窗这条路径不会显示，必须用 ShowToastAt 直接给屏幕坐标，
            // 否则 ShowInlineToast 会以浮窗左上角（默认 0,0 或上一次位置）为锚 → toast 飘到屏幕外看不见
            ShowAnchoredToast("OCR 未识别到文本", anchorRect, isError: true, durationMs: 3500);
            return;
        }

        var mode = _settings.OcrResultMode;
        if (mode == OcrResultMode.Interactive)
        {
            // iOS 风格：弹结果窗在截图位置上叠加高亮，用户点选 / 框选 / 复制。
            // 剪贴板与浮窗气泡都不在这条路径触发，所有反馈走结果窗内部；
            // 但允许结果窗按用户意愿"临时切到 Quick 输出"，传 quickFallback 回调让它能调用同一套 Quick 渲染
            ShowInteractiveResult(result, pngBytes, anchorRect, fullText,
                quickFallback: text => RenderQuickResult(text, anchorRect, provider.DisplayName));
            return;
        }

        // Quick 模式（旧行为）：直接写剪贴板 + 浮窗 / 气泡
        RenderQuickResult(fullText, anchorRect, provider.DisplayName);
    }

    /// <summary>Quick 模式的结果渲染逻辑：剪贴板 + 气泡 / inline toast，锚点直接用 OCR 截图框。
    ///
    /// 抽出为独立方法是为了让 Interactive 结果窗的"Quick 输出"按钮能复用同一套展示，
    /// 而不需要重新走一遍 RecognizeAsync。两种触发：
    /// 1) settings.OcrResultMode == Quick 时 RecognizeBitmapAsync 直接调；
    /// 2) settings.OcrResultMode == Interactive 时，用户在结果窗点"Quick 输出" → quickFallback 回调。
    ///    结果窗会接收已按 OCR 版面分析过的全文，Quick 输出不再重复整理。
    ///
    /// 不再走 SelectionSessionManager.ShowToolbarForExternalText：那是给"剪贴板兜底文本"展示动作浮窗用的，
    /// OCR 框选是用户明确的截图识字操作，按钮浮窗会和气泡叠在一起干扰阅读；
    /// 这里只显示气泡（多行可读 + 复制按钮）或 inline toast（短预览），用 anchorRect 直接锚定截图框下方。
    ///
    /// 气泡比单行 toast 优势：
    /// - 支持多行，长文本 OCR 不会被截断；
    /// - 用户能即时看到识别质量并手动复制；
    /// - 不依赖浮窗位置，OCR 截图框就是天然 anchor</summary>
    private void RenderQuickResult(string fullText, SelectionRect anchorRect, string providerDisplayName)
    {
        _clipboard.SetText(fullText);

        // anchor 物理像素 → DIP：取 anchor 所在 monitor 的真实 effective DPI，
        // 跨屏异构 DPI 时副屏框选的气泡/toast 才不会按主屏 DPI 比例缩到错位。
        var monitor = MonitorQuery.FromRect(
            anchorRect.Left, anchorRect.Top, anchorRect.Right, anchorRect.Bottom);
        double dpiScaleX = Math.Max(96, monitor.DpiX) / 96.0;
        double dpiScaleY = Math.Max(96, monitor.DpiY) / 96.0;
        double anchorCenterDip = (anchorRect.Left + anchorRect.Width / 2.0) / dpiScaleX;
        double anchorTopDip = (anchorRect.Bottom + 8) / dpiScaleY;
        double monitorBottomDip = monitor.WorkBottom / dpiScaleY;
        double monitorTopDip = monitor.WorkTop / dpiScaleY;

        if (_bubble is not null)
        {
            // 标题去掉 OCR 后端名（用户反馈"已识别"后展示后端名信息冗余）；
            // 仅保留"OCR · 字数"作为最简一行，状态栏会展示完成时间，元信息无需再重复 provider
            var bubbleTitle = $"OCR · {fullText.Length} 字";
            var translateFn = BuildBubbleTranslateFunc();
            _ = WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
                _bubble.ShowStaticAt(
                    bubbleTitle,
                    fullText,
                    new BubbleAnchor(anchorCenterDip, anchorTopDip, monitorBottomDip, monitorTopDip),
                    translateAsync: translateFn)));
        }
        else
        {
            // 兜底：没有气泡 presenter 时（理论上不会发生），用独立锚点 toast 给出简短提示 + 复制按钮。
            // ShowToastAt 直接接 DIP 坐标，与主浮窗解耦，OCR 框选区域下方就能显示
            var preview = BuildPreview(fullText);
            _ = WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
                _toolbar.ShowToastAt(
                    $"OCR 已识别 {fullText.Length} 字 · 已复制：{preview}",
                    anchorCenterDip, anchorTopDip,
                    isError: false, durationMs: 5000)));
        }
    }

    /// <summary>把 OcrResult 用 iOS 风格弹窗展示出来。
    /// 必须切到 UI 线程：OcrResultWindow 是 WPF Window，跨线程构造会抛 InvalidOperationException。
    /// 同时同一时刻只允许一个结果窗，新结果直接关掉旧窗（避免叠层 + 多个 topmost 抢焦点）。
    ///
    /// DPI 走 <see cref="MonitorQuery.FromRect"/> 取 anchor 截图所在 monitor 的 effective DPI，
    /// 而不是浮窗当前 PresentationSource 的 DPI —— 副屏框选 + 浮窗在主屏时两者不同，
    /// 后者会让结果窗按主屏 DIP 解读跑回主屏。OcrResultWindow 内部用 Win32 SetWindowPos
    /// 物理像素绝对定位，跟 FloatingToolbar / ToolbarToastWindow 走同一条多屏正确路径</summary>
    private void ShowInteractiveResult(OcrResult result, byte[] pngBytes, SelectionRect anchorPhysical, string layoutFullText, Action<string> quickFallback)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var monitor = MonitorQuery.FromRect(
                anchorPhysical.Left, anchorPhysical.Top,
                anchorPhysical.Right, anchorPhysical.Bottom);
            var anchorRectangle = new Rectangle(
                anchorPhysical.Left,
                anchorPhysical.Top,
                anchorPhysical.Width,
                anchorPhysical.Height);

            // 旧窗存在则先关掉，避免叠层
            if (_resultWindow is not null)
            {
                try { _resultWindow.Close(); } catch { }
                _resultWindow = null;
            }

            var win = new OcrResultWindow(_log, result, pngBytes, anchorRectangle,
                monitor.DpiX, monitor.DpiY,
                _clipboard,
                _settings, _aiText,
                layoutFullText: layoutFullText,
                quickFallback: quickFallback,
                openSettings: _openSettings,
                onCloseRequested: () =>
                {
                    if (ReferenceEquals(_resultWindow, null)) return;
                    _resultWindow = null;
                });
            _resultWindow = win;
            win.Closed += (_, _) => { if (ReferenceEquals(_resultWindow, win)) _resultWindow = null; };
            win.Show();
            win.Activate();
        });
    }

    /// <summary>给 Quick 模式气泡返回一个"翻译当前正文"的回调。
    /// AI 未启用 / 未配置 Key 时返回 null，让气泡不显示翻译按钮 — 用户不会按到"按了无反应"的死按钮。
    /// 60s 超时与主流程一致；超时与异常都在 catch 中吞并返回 null，UI 层据此回到原文态</summary>
    private Func<string, Task<string?>>? BuildBubbleTranslateFunc()
    {
        if (_aiText is null || !_aiText.CanRun) return null;
        return async (source) =>
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                var list = await _aiText.TranslateBatchAsync(new[] { source }, cts.Token).ConfigureAwait(true);
                return list.Count > 0 ? (list[0] ?? "").Trim() : null;
            }
            catch (Exception ex)
            {
                _log.Warn("ocr quick bubble translate failed", ("err", ex.Message));
                return null;
            }
        };
    }

    private IOcrProvider? PickActiveOrNotify()
    {
        var provider = _registry.PickActive();
        if (provider is null) NotifyNoProvider();
        return provider;
    }

    /// <summary>所有 provider 都不可用时弹一次 MessageBox，把各 provider 的不可用原因列出来。
    /// 这样用户能在一个对话框里看到三种安装路径，按需选一个去补文件即可。</summary>
    private void NotifyNoProvider()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("当前没有可用的 OCR 后端。请按下列任一方式启用：");
        sb.AppendLine();
        foreach (var p in _registry.All)
        {
            sb.AppendLine($"• {p.DisplayName}");
            sb.AppendLine($"  状态：{p.UnavailableReason ?? "可用"}");
            sb.AppendLine();
        }
        sb.AppendLine("详情见各 provider 目录下的 README.md。");

        var msg = sb.ToString();
        _log.Warn("ocr no available provider, user notified");
        WpfApplication.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(msg, "OCR 不可用", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    /// <summary>给 toast 用的内容预览：去掉换行、压缩空白、最多 36 字符 + 省略号。
    /// toast 单行展示，太长会被截断且失去可读性；36 字符在 13px 字号下大约 200px 宽，
    /// 与浮窗常见宽度相近</summary>
    private static string BuildPreview(string text)
    {
        var compact = string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= 36) return compact;
        return compact[..36] + "…";
    }

    /// <summary>把"截图框 + toast 显示"打包到 UI 线程一次性完成。
    /// 必须在 UI 线程做：Application.MainWindow / FloatingToolbar 都是 DispatcherObject，
    /// 跨线程访问会抛 InvalidOperationException（RunCaptureAsync 跑在后台线程，必须显式切回 UI 线程）。
    ///
    /// DPI 走 MonitorQuery 取 anchor 截图所在 monitor 的真实 effective DPI，
    /// 而不是浮窗当前 PresentationSource 的 DPI —— 副屏框选 + 浮窗在主屏时两者不同，
    /// 后者会让 toast 按主屏 DIP 解读，副屏框选时 toast 飘到错位置</summary>
    private void ShowAnchoredToast(string text, SelectionRect anchorRect, bool isError, int durationMs)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var monitor = MonitorQuery.FromRect(
                anchorRect.Left, anchorRect.Top, anchorRect.Right, anchorRect.Bottom);
            double dpiScaleX = Math.Max(96, monitor.DpiX) / 96.0;
            double dpiScaleY = Math.Max(96, monitor.DpiY) / 96.0;
            double centerDip = (anchorRect.Left + anchorRect.Width / 2.0) / dpiScaleX;
            double topDip = (anchorRect.Bottom + 8) / dpiScaleY;
            _toolbar.ShowToastAt(text, centerDip, topDip, isError: isError, durationMs: durationMs);
        });
    }
}
