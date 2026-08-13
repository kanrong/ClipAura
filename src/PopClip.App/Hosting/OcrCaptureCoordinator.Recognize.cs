using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Hooks;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using PopClip.Ocr.Layout;
using PopClip.Uia.Clipboard;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Hosting;

internal sealed partial class OcrCaptureCoordinator
{

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

        // OCR 文本回写图片历史：钉图 / 截图自动保存等场景图片已入库；
        // 这里用 PNG 字节 hash 反查 clipboard_image_history 并 UPDATE ocr_text。
        // 找不到记录时 SetImageOcrTextByHash 静默 no-op，无副作用
        if (_clipHistory is not null && !string.IsNullOrEmpty(fullText))
        {
            try
            {
                var pngBytes = image.GetPngBytes();
                if (pngBytes is { Length: > 0 })
                {
                    var hash = ClipboardHistoryService.HashImagePngBytes(pngBytes);
                    _clipHistory.SetImageOcrTextByHash(hash, fullText);
                }
            }
            catch (Exception ex) { _log.Debug("ocr writeback skipped", ("err", ex.Message)); }
        }

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
                pinScreenshot: pinAnchor =>
                {
                    if (_pinnedManager is null) return;
                    // 用 OCR 内部缓存的 PNG 字节钉，避免重新走 RenderTargetBitmap。
                    // OCR 结果窗本身不关，让用户继续在结果窗交互（同 ScreenshotPreviewWindow.CommandPin 行为不同）
                    byte[]? pngBytes = null;
                    try { pngBytes = image.GetPngBytes(); }
                    catch (Exception ex) { _log.Warn("encode png for pin failed", ("err", ex.Message)); return; }
                    if (pngBytes is null || pngBytes.Length == 0) return;
                    _pinnedManager.Pin(pngBytes, monitor.DpiX, monitor.DpiY, pinAnchor);
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
}
