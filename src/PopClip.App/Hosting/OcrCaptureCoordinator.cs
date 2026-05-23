using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Hooks.Interop;
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
/// "自动"模式按 Priority 倒序选第一个 IsAvailable 的。每次 Trigger / RecognizeImageAsync
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
    private readonly Action? _saveSettings;

    /// <summary>"打开设置面板并定位到某个分页"的跨层回调。
    /// 由 AppHost 注入，参数为页面 tag（如 "AI" / "Ocr"），方便结果窗里的
    /// "启用 AI / 调整 OCR" 类引导按钮直达对应设置位置。可空：宿主未传入时按钮仅在 Toast 提示</summary>
    private readonly Action<string>? _openSettings;

    private OcrSelectionWindow? _currentWindow;
    private OcrResultWindow? _resultWindow;
    private ScreenshotPreviewWindow? _screenshotWindow;

    private enum CapturePurpose
    {
        Ocr,
        Screenshot,
    }

    private sealed record CapturedImage(
        OcrImage Image,
        SelectionRect AnchorRect,
        string Source,
        byte[] PngBytes);

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
        Action<string>? openSettings = null,
        Action? saveSettings = null)
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
        _saveSettings = saveSettings;
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

        ShowSelectionWindow(CapturePurpose.Ocr);
    }

    public void TriggerScreenshot()
    {
        ShowSelectionWindow(CapturePurpose.Screenshot);
    }

    public void TriggerClipboardImage(SelectionRect anchorRect)
    {
        var provider = PickActiveOrNotify();
        if (provider is null) return;
        provider.PrewarmInBackground();
        _ = Task.Run(() => RunClipboardImageAsync(anchorRect));
    }

    private void ShowSelectionWindow(CapturePurpose purpose)
    {
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
            window.Cancelled += () => _log.Info(purpose == CapturePurpose.Ocr
                ? "ocr capture cancelled"
                : "screenshot capture cancelled");
            window.RegionSelected += physical => _ = RunCaptureAsync(physical, purpose);
            window.Show();
        });
    }

    private async Task RunCaptureAsync(Rectangle physical, CapturePurpose purpose)
    {
        if (physical.Width <= 0 || physical.Height <= 0) return;
        try
        {
            // 等 DWM 消化选区窗 Hide/Close 对桌面合成的影响，再截屏。
            // 比固定 sleep 80ms 更快；如果 DwmFlush 在异常环境失败，退回短延迟兜底。
            await WaitForSelectionOverlayToDisappearAsync().ConfigureAwait(false);

            var captured = CaptureRegion(physical);
            if (purpose == CapturePurpose.Ocr)
            {
                await RecognizeImageAsync(captured.Image, captured.AnchorRect, captured.Source, closeAfterLoad: null).ConfigureAwait(false);
            }
            else
            {
                await HandleScreenshotCaptureAsync(captured).ConfigureAwait(false);
            }
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

    private CapturedImage CaptureRegion(Rectangle physical)
    {
        using var bitmap = new Bitmap(physical.Width, physical.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(physical.Left, physical.Top, 0, 0, bitmap.Size);
        }

        var image = CreateOcrImage(bitmap);
        var pngBytes = image.GetPngBytes();
        var anchorRect = new SelectionRect(physical.Left, physical.Top, physical.Right, physical.Bottom);
        return new CapturedImage(image, anchorRect, $"{physical.Width}x{physical.Height}", pngBytes);
    }

    private async Task HandleScreenshotCaptureAsync(CapturedImage captured)
    {
        var copiedDirectly = _settings.ScreenshotAfterCaptureMode == ScreenshotAfterCaptureMode.Clipboard;
        if (copiedDirectly)
        {
            _clipboard.SetImagePngBytes(captured.PngBytes);
            ShowAnchoredToast("截图已复制", captured.AnchorRect, isError: false, durationMs: 1800);
        }
        else
        {
            ShowScreenshotPreview(captured);
        }

        if (_settings.ScreenshotAutoOcr)
        {
            await RecognizeImageAsync(captured.Image, captured.AnchorRect, captured.Source, closeAfterLoad: null).ConfigureAwait(false);
        }
    }

    /// <summary>把已识别后的 OcrImage 转换回一个截图 PNG 字节 + Anchor，再走标准 ScreenshotPreview 路径。
    /// 用于 OCR 结果窗的"切回截图"按钮：用户在结果窗看完识别再想做"复制原图 / 保存 PNG"等截图侧操作时，
    /// 不需要重新拉框 + 重新截屏，直接复用已有像素。OcrImage.GetPngBytes 是带 lazy cache 的 PNG 编码，
    /// 第二次调用走缓存几乎零成本。
    ///
    /// closeAfterLoad：在新预览窗 Loaded（已 Show + 物理像素定位完成）后再关闭的旧窗。
    /// 让"OCR 结果窗 → 截图预览窗"切换时两窗在同一位置短暂共存，截图区域背景视觉无缝衔接</summary>
    private void ShowScreenshotPreviewFromOcr(OcrImage image, SelectionRect anchorRect, string source, Window? closeAfterLoad = null)
    {
        byte[] pngBytes;
        try { pngBytes = image.GetPngBytes(); }
        catch (Exception ex)
        {
            _log.Warn("encode png for switch-to-screenshot failed", ("err", ex.Message));
            return;
        }
        var captured = new CapturedImage(image, anchorRect, source, pngBytes);
        ShowScreenshotPreview(captured, closeAfterLoad);
    }

    private void ShowScreenshotPreview(CapturedImage captured, Window? closeAfterLoad = null)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            // closeAfterLoad 不为空时表示"切换路径"，旧窗由新窗 Loaded 后关闭以保持视觉连续，
            // 不能在这里 pre-close。其他路径（快捷键 / 托盘）保留原行为：直接关闭已有的预览窗
            if (closeAfterLoad is null && _screenshotWindow is not null)
            {
                try { _screenshotWindow.Close(); } catch { }
                _screenshotWindow = null;
            }

            var monitor = MonitorQuery.FromRect(
                captured.AnchorRect.Left, captured.AnchorRect.Top,
                captured.AnchorRect.Right, captured.AnchorRect.Bottom);
            var anchorRectangle = new Rectangle(
                captured.AnchorRect.Left,
                captured.AnchorRect.Top,
                captured.AnchorRect.Width,
                captured.AnchorRect.Height);

            var win = new ScreenshotPreviewWindow(
                _log,
                captured.PngBytes,
                anchorRectangle,
                monitor.DpiX,
                monitor.DpiY,
                _clipboard,
                onOcrRequested: currentAnchor =>
                {
                    // 用户在预览窗内拖动 / 跨屏后再点 OCR：把当前图像物理位置作为新 anchor，
                    // 让 OCR 结果窗在预览窗当前位置弹出。同时把当前预览窗作为 closeAfterLoad，
                    // OCR 结果窗 Loaded 后再关，避免切换瞬间出现空白屏幕
                    var newRect = ToSelectionRect(currentAnchor);
                    var pendingClose = _screenshotWindow;
                    _ = RecognizeImageAsync(captured.Image, newRect, captured.Source, closeAfterLoad: pendingClose);
                },
                onReshootRequested: () =>
                {
                    // 重新截图路径：直接关掉当前预览（含工具栏 Owner 链），再走选区状态机进入新一轮采集。
                    // 与快捷键 / 托盘触发的区别在于这里明确隐藏了旧预览（按钮入口里已 Hide），
                    // 让 OcrSelectionWindow 全屏蒙层下方看到的是桌面原始内容而不是旧的高亮截图
                    var oldWin = _screenshotWindow;
                    _screenshotWindow = null;
                    try { oldWin?.Close(); } catch { }
                    ShowSelectionWindow(CapturePurpose.Screenshot);
                });
            _screenshotWindow = win;
            win.Closed += (_, _) => { if (ReferenceEquals(_screenshotWindow, win)) _screenshotWindow = null; };

            if (closeAfterLoad is not null)
            {
                // 关键：Loaded 触发时机 = WPF 完成布局 + PlaceWindowAtAnchorPhysical 已把窗口定位到目标
                // 物理像素，此时新窗已显示在屏幕上目标位置。在这里关闭旧窗，用户视觉感受是"原地无缝切换"
                win.Loaded += (_, _) =>
                {
                    try { closeAfterLoad.Close(); } catch { }
                };
            }
            win.Show();
            win.Activate();
        });
    }

    private static async Task WaitForSelectionOverlayToDisappearAsync()
    {
        var hr = NativeMethods.DwmFlush();
        if (hr == 0) return;

        await Task.Delay(32).ConfigureAwait(false);
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
            var image = CreateOcrImage(bitmap, pngFactory: () => pngBytes);
            var displayRect = CreateClipboardImageDisplayRect(image, anchorRect);
            await RecognizeImageAsync(image, displayRect, "clipboard-image").ConfigureAwait(false);
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

    private async Task RecognizeImageAsync(OcrImage image, SelectionRect anchorRect, string source, Window? closeAfterLoad = null)
    {
        var provider = _registry.PickActive();
        if (provider is null)
        {
            NotifyNoProvider();
            return;
        }

        var mode = _settings.OcrResultMode;
        OcrResultWindow? loadingWin = null;

        // Interactive 模式：在 OCR 开始前先创建"识别中"占位结果窗。
        // 目的：让"截图预览 → OCR"切换的视觉过渡零空窗 —— 截图背景在新窗里立即可见，
        // 然后旧 ScreenshotPreviewWindow 由 Loaded 事件链关闭，最后 OCR 异步完成时 PopulateResult。
        // 即使用户是从快捷键 / 托盘直接进 OCR（没有旧预览窗），loading 占位也比"OCR 跑完前屏幕空白"更友好；
        // 引擎冷启动时尤其明显，无需依赖 Toast 弥补反馈缺失。
        // Quick 模式保持原路径：识别完成后直接出气泡 / toast，没有窗体可让它"先显示"
        if (mode == OcrResultMode.Interactive)
        {
            loadingWin = ShowInteractiveResult(
                OcrResult.Empty, image, anchorRect, layoutFullText: "",
                quickFallback: text => RenderQuickResult(text, anchorRect, provider.DisplayName),
                closeAfterLoad: closeAfterLoad,
                isLoading: true);
        }

        // 引擎未就绪时识别可能耗时 1~3 秒（含 ONNX 三个 session 冷启动 / WeChatOCR 子进程 spawn）。
        // Interactive 模式下 loading 窗已经在显示"识别中…"，不需要再叠 toast；
        // Quick 模式下没有 loading 占位，仍用 toast 让用户知道正在工作
        if (!provider.IsEngineReady && loadingWin is null)
        {
            ShowAnchoredToast($"文字识别中… ", anchorRect, isError: false, durationMs: 1500);
        }

        // 超时给 30 秒：冷启动 + 大图识别 + WeChat 子进程通信极端情况下也够用
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        OcrResult result;
        try
        {
            result = await provider.RecognizeAsync(image, cts.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _log.Error($"ocr provider failed: {provider.Id}", ex);
            ShowAnchoredToast($"OCR 失败: {ex.Message}", anchorRect, isError: true, durationMs: 4000);
            // OCR 失败时把 loading 占位窗关掉，避免用户对着"识别中…"无限等
            CloseLoadingWindow(loadingWin);
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
            CloseLoadingWindow(loadingWin);
            return;
        }

        if (mode == OcrResultMode.Interactive)
        {
            if (loadingWin is not null)
            {
                // 已存在的 loading 窗升级为正常结果窗：刷新 _result / 重建 polygon / 激活工具栏全部能力。
                // 用 Invoke 确保 PopulateResult 在 UI 线程同步完成，与构造时同线程，
                // 避免 UI 元素跨线程操作的所有竞态
                WpfApplication.Current.Dispatcher.Invoke(() =>
                {
                    try { loadingWin.PopulateResult(result, fullText); }
                    catch (Exception ex) { _log.Warn("populate result failed", ("err", ex.Message)); }
                });
            }
            else
            {
                // 兜底：loading 窗未成功创建（极端线程或资源问题）时走原"OCR 完后再展示"路径
                ShowInteractiveResult(result, image, anchorRect, fullText,
                    quickFallback: text => RenderQuickResult(text, anchorRect, provider.DisplayName),
                    closeAfterLoad: closeAfterLoad);
            }
            return;
        }

        // Quick 模式（旧行为）：直接写剪贴板 + 浮窗 / 气泡。
        // Quick 模式不弹结果窗，但调用方可能传了 closeAfterLoad（如从 ScreenshotPreview 切到 OCR Quick），
        // 此时旧预览窗已无意义，统一关掉
        if (closeAfterLoad is not null)
        {
            WpfApplication.Current.Dispatcher.Invoke(() => { try { closeAfterLoad.Close(); } catch { } });
        }
        RenderQuickResult(fullText, anchorRect, provider.DisplayName);
    }

    /// <summary>关闭可能为 null 的 loading 占位窗。
    /// OCR 失败 / 未识别到文本时统一收尾路径，避免把 try / catch / Dispatcher.Invoke 同样的几行重复 3 次</summary>
    private static void CloseLoadingWindow(OcrResultWindow? loadingWin)
    {
        if (loadingWin is null) return;
        WpfApplication.Current.Dispatcher.Invoke(() => { try { loadingWin.Close(); } catch { } });
    }

    /// <summary>Quick 模式的结果渲染逻辑：剪贴板 + 气泡 / inline toast，锚点直接用 OCR 截图框。
    ///
    /// 抽出为独立方法是为了让 Interactive 结果窗的"Quick 输出"按钮能复用同一套展示，
    /// 而不需要重新走一遍 RecognizeAsync。两种触发：
    /// 1) settings.OcrResultMode == Quick 时 RecognizeImageAsync 直接调；
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
            // OCR Quick 正文通常是整段段落，默认 320~520 太窄导致频繁折行；
            // 放宽到 480~640 让一行能容纳约 30~40 个中文 / 60~80 个 ASCII，显著减少滚动需求，
            // 同时仍保留 SizeToContent 自适应，短文本仍能贴紧最小宽度
            _ = WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
                _bubble.ShowStaticAt(
                    bubbleTitle,
                    fullText,
                    new BubbleAnchor(anchorCenterDip, anchorTopDip, monitorBottomDip, monitorTopDip),
                    translateAsync: translateFn,
                    preferredMinWidth: 480.0,
                    preferredMaxWidth: 640.0)));
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
    /// 物理像素绝对定位，跟 FloatingToolbar / ToolbarToastWindow 走同一条多屏正确路径。
    ///
    /// closeAfterLoad：在新结果窗 Loaded（已 Show + 物理像素定位完成）后再关闭的旧窗，
    /// 用于"截图预览 → OCR 结果"切换时让截图背景视觉无缝衔接。
    ///
    /// isLoading：当 true 时本调用只创建"识别中"占位窗（截图背景立即可见，polygon / 工具按钮置灰）；
    /// 调用方应在 OCR 完成后调用 win.PopulateResult(result, fullText) 把占位窗升级为完整结果窗。
    /// 这条路径让 ScreenshotPreview → OCR 切换的截图背景"零空窗期"无缝衔接，
    /// 用户不必等 1~3 秒 OCR 跑完才看到窗体变化。
    ///
    /// 返回创建的窗口引用，便于调用方后续调 PopulateResult；如果 UI 线程没有成功创建窗口（极端兜底），返回 null</summary>
    private OcrResultWindow? ShowInteractiveResult(OcrResult result, OcrImage image, SelectionRect anchorPhysical, string layoutFullText, Action<string> quickFallback, Window? closeAfterLoad = null, bool isLoading = false)
    {
        OcrResultWindow? created = null;
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

            // 旧结果窗存在则先关掉，避免叠层（与 closeAfterLoad 互斥：closeAfterLoad 关的是
            // ScreenshotPreviewWindow 这种"对偶"窗口；这里关的是同类型的"上一个结果窗"）
            if (_resultWindow is not null)
            {
                try { _resultWindow.Close(); } catch { }
                _resultWindow = null;
            }

            var win = new OcrResultWindow(_log, result, image, anchorRectangle,
                monitor.DpiX, monitor.DpiY,
                _clipboard,
                _settings, _aiText,
                layoutFullText: layoutFullText,
                quickFallback: quickFallback,
                openSettings: _openSettings,
                saveSettings: _saveSettings,
                onCloseRequested: () =>
                {
                    if (ReferenceEquals(_resultWindow, null)) return;
                    _resultWindow = null;
                },
                // "切到截图预览"回调：复用已识别的 OcrImage（含像素 + PNG 缓存），
                // 跳过重新拉框 / 重新 CopyFromScreen，让两个窗口形成双向无缝切换。
                // 参数 currentAnchor 是用户在 OCR 结果窗里拖动 / 跨屏后的当前位置，
                // 拿它作为截图预览窗的 anchor 让两者位置完全对齐
                switchToScreenshot: currentAnchor =>
                {
                    var newRect = ToSelectionRect(currentAnchor);
                    var pendingClose = _resultWindow;
                    ShowScreenshotPreviewFromOcr(image, newRect, "from-ocr-result", closeAfterLoad: pendingClose);
                },
                reshootOcr: () =>
                {
                    var oldWin = _resultWindow;
                    _resultWindow = null;
                    try { oldWin?.Close(); } catch { }
                    ShowSelectionWindow(CapturePurpose.Ocr);
                },
                isLoading: isLoading);
            _resultWindow = win;
            win.Closed += (_, _) => { if (ReferenceEquals(_resultWindow, win)) _resultWindow = null; };

            if (closeAfterLoad is not null)
            {
                // 关键：Loaded = WPF 完成布局 + PlaceWindowAtAnchorPhysical 已把窗口定位到目标物理像素。
                // 此时新结果窗已可见，关闭旧 ScreenshotPreviewWindow 不会留下空白屏幕
                win.Loaded += (_, _) =>
                {
                    try { closeAfterLoad.Close(); } catch { }
                };
            }
            win.Show();
            win.Activate();
            created = win;
        });
        return created;
    }

    /// <summary>WinRectangle → SelectionRect 的简短转换。SelectionRect 用 (Left, Top, Right, Bottom)
    /// 而 WinRectangle 用 (X, Y, Width, Height)，几个换算点重复出现，抽个静态方法保持简洁</summary>
    private static SelectionRect ToSelectionRect(Rectangle rect)
        => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static OcrImage CreateOcrImage(Bitmap bitmap, Func<byte[]>? pngFactory = null)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        using var normalized = bitmap.PixelFormat == PixelFormat.Format32bppArgb
            ? null
            : bitmap.Clone(rect, PixelFormat.Format32bppArgb);
        var source = normalized ?? bitmap;
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * source.Height];
            if (data.Stride > 0)
            {
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            else
            {
                for (var y = 0; y < source.Height; y++)
                {
                    var row = IntPtr.Add(data.Scan0, data.Stride * y);
                    Marshal.Copy(row, pixels, y * stride, stride);
                }
            }
            return new OcrImage(
                source.Width,
                source.Height,
                OcrImagePixelFormat.Bgra32,
                pixels,
                stride,
                pngFactory ?? (() => EncodePngFromBgra32(source.Width, source.Height, pixels, stride)));
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private static byte[] EncodePngFromBgra32(int width, int height, byte[] pixels, int stride)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var destStride = Math.Abs(data.Stride);
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var dest = data.Stride > 0
                    ? IntPtr.Add(data.Scan0, y * data.Stride)
                    : IntPtr.Add(data.Scan0, (height - 1 - y) * destStride);
                Marshal.Copy(pixels, y * stride, dest, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static SelectionRect CreateClipboardImageDisplayRect(OcrImage image, SelectionRect fallbackAnchor)
    {
        MonitorMetrics monitor;
        if (NativeMethods.GetCursorPos(out var pt))
        {
            monitor = MonitorQuery.FromPoint(pt.X, pt.Y);
        }
        else
        {
            monitor = MonitorQuery.FromRect(
                fallbackAnchor.Left, fallbackAnchor.Top,
                fallbackAnchor.Right, fallbackAnchor.Bottom);
        }

        var workWidth = Math.Max(1, monitor.WorkWidth);
        var workHeight = Math.Max(1, monitor.WorkHeight);
        var marginX = Math.Max(24, (int)Math.Round(workWidth * 0.08));
        var marginY = Math.Max(24, (int)Math.Round(workHeight * 0.08));
        var maxWidth = Math.Min(workWidth, Math.Max(120, workWidth - marginX * 2));
        var maxHeight = Math.Min(workHeight, Math.Max(80, workHeight - marginY * 2));
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)image.Width, maxHeight / (double)image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));

        var left = monitor.WorkLeft + (workWidth - width) / 2;
        var top = monitor.WorkTop + (workHeight - height) / 2;
        left = Math.Clamp(left, monitor.WorkLeft, monitor.WorkRight - width);
        top = Math.Clamp(top, monitor.WorkTop, monitor.WorkBottom - height);
        return new SelectionRect(left, top, left + width, top + height);
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
