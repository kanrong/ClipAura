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

    private async Task HandleScreenshotCaptureAsync(CapturedImage captured, Stopwatch? timing = null)
    {
        // 自动保存与 ScreenshotAfterCaptureMode 解耦：可与 Clipboard / Toolbar 任一模式并存。
        // 用户故事："保存到 D:\Screenshots，同时复制到剪贴板，不打扰预览窗"。
        // 异步发起但不 await：避免磁盘 IO 阻塞预览窗 / 剪贴板写入的视觉响应
        Task<ScreenshotAutoSaveResult>? autoSaveTask = null;
        if (_settings.ScreenshotAutoSaveEnabled)
        {
            autoSaveTask = TryAutoSaveScreenshotAsync(captured);
        }

        var copiedDirectly = _settings.ScreenshotAfterCaptureMode == ScreenshotAfterCaptureMode.Clipboard;
        if (copiedDirectly)
        {
            // ClipboardWriter.SetImagePngBytes 内部已调 NoteSelfWrittenImage，让监听路径忽略本次回环。
            // 之后再 AppendImage 让"图片复制 → 同时入历史"无需依赖 WM_CLIPBOARDUPDATE 监听。
            // 这里首次 GetPngBytes 触发 Lazy 编码（仍在后台线程，因 HandleScreenshotCaptureAsync
            // 整链路 ConfigureAwait(false)），不会阻塞 UI
            _clipboard.SetImagePngBytes(captured.Image.GetPngBytes());
            TryAppendImageToHistory(captured);
            ShowAnchoredToast("截图已复制", captured.AnchorRect, isError: false, durationMs: 1800);
        }
        else
        {
            ShowScreenshotPreview(captured, timing: timing);
        }

        if (_settings.ScreenshotAutoOcr)
        {
            await RecognizeImageAsync(captured.Image, captured.AnchorRect, captured.Source, closeAfterLoad: null).ConfigureAwait(false);
        }

        if (autoSaveTask is not null)
        {
            try
            {
                var result = await autoSaveTask.ConfigureAwait(false);
                if (result.Success)
                {
                    var fileName = Path.GetFileName(result.Path!);
                    // Clipboard 模式下不会有预览窗 toast，这里再补一条；Toolbar 模式 ScreenshotPreviewToolbar 会自己提示，
                    // 但用户在自动保存场景往往希望"同时看到落盘提示"，所以无论哪个模式都补一条 anchored toast。
                    // 文案与"截图已复制"协调：复制走预览/剪贴板路径已提示，自动保存路径补 "已保存"
                    ShowAnchoredToast($"已保存 {fileName}", captured.AnchorRect, isError: false, durationMs: 2400);
                    // 同步落到图片历史：用户在历史窗里也能看到"自动保存的截图"
                    TryAppendImageToHistory(captured);
                }
                else if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    ShowAnchoredToast("自动保存失败：" + result.ErrorMessage, captured.AnchorRect, isError: true, durationMs: 3000);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("auto-save unexpected error", ("err", ex.Message));
            }
        }
    }

    /// <summary>把一张已截好的截图 PNG 字节同步到剪贴板图片历史。
    /// 不依赖剪贴板 API：钉图 / 自动保存路径不会改剪贴板，但仍希望用户在历史窗能回看</summary>
    private void TryAppendImageToHistory(CapturedImage captured)
    {
        if (_clipHistory is null) return;
        try
        {
            var foreground = "";
            try { foreground = ForegroundWatcher.Snapshot().ProcessName; } catch { }
            _clipHistory.AppendImage(captured.Image.GetPngBytes(), foreground);
        }
        catch (Exception ex)
        {
            _log.Warn("append image history failed", ("err", ex.Message));
        }
    }

    /// <summary>把已截好的 PNG 字节走 ScreenshotAutoSaver 落盘。
    /// 拆出独立方法便于其它路径（钉图入库 / 延时截图）后续复用</summary>
    private Task<ScreenshotAutoSaveResult> TryAutoSaveScreenshotAsync(CapturedImage captured)
    {
        var foreground = "";
        try { foreground = ForegroundWatcher.Snapshot().ProcessName; }
        catch (Exception ex) { _log.Warn("get foreground for auto-save failed", ("err", ex.Message)); }
        var ctx = new ScreenshotAutoSaveContext(
            Width: captured.Image.Width,
            Height: captured.Image.Height,
            ForegroundProcessName: foreground);
        return _autoSaver.SaveAsync(captured.Image.GetPngBytes(), ctx);
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
        // OCR 已识别完成的 image 在结果窗内一定调过 GetPngBytes（hash 回写或工具栏按钮）二次走缓存，
        // 不需要再单独 try/catch 预热；下游 ShowScreenshotPreview 会在切回 UI 线程前显式触发 Lazy 编码
        var captured = new CapturedImage(image, anchorRect, source);
        ShowScreenshotPreview(captured, closeAfterLoad);
    }

    private void ShowScreenshotPreview(CapturedImage captured, Window? closeAfterLoad = null, Stopwatch? timing = null)
    {
        // 旧实现这里阻塞调用 captured.Image.GetPngBytes() —— PNG 编码 1080p 大约 30ms、4K 100ms+，
        // 把"松手 → 预览出现"的延迟撑大。新实现改成"BGRA32 像素直接构造 BitmapSource"，
        // 完全绕开"BGRA32 → PNG → BGRA32"这趟来回，预览只为显示存在，不需要 PNG 字节。
        // PNG 字节仍走 OcrImage.GetPngBytes 的 Lazy 路径，在用户点复制 / 保存 / 钉图时才编码
        var beforeBuildMs = timing?.ElapsedMilliseconds ?? 0;
        BitmapSource imageSource;
        try
        {
            // BitmapSource.Create 直接吃 BGRA32 像素 + stride + anchor monitor DPI 构造，
            // DPI 选用 anchor monitor 的 effective DPI 是为了让 Image Stretch="None" 时
            // DIP × 屏幕 DPI = 物理像素，绘制走 NearestNeighbor 1:1
            var monitorForSource = MonitorQuery.FromRect(
                captured.AnchorRect.Left, captured.AnchorRect.Top,
                captured.AnchorRect.Right, captured.AnchorRect.Bottom);
            imageSource = System.Windows.Media.Imaging.BitmapSource.Create(
                captured.Image.Width,
                captured.Image.Height,
                monitorForSource.DpiX,
                monitorForSource.DpiY,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                captured.Image.Pixels,
                captured.Image.Stride);
            imageSource.Freeze();
        }
        catch (Exception ex)
        {
            _log.Warn("build bitmapsource for screenshot preview failed", ("err", ex.Message));
            return;
        }
        var afterBuildMs = timing?.ElapsedMilliseconds ?? 0;

        // 给 ScreenshotPreviewWindow 用的 PNG 字节延迟取值器：直接复用 OcrImage 内部已经做好的 Lazy 路径。
        // 用户点"复制 / 保存 / 无标注钉图"时才真正编码，且第二次取同一张图走缓存零成本
        Func<byte[]> pngBytesProvider = captured.Image.GetPngBytes;

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var inUiMs = timing?.ElapsedMilliseconds ?? 0;
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
                imageSource,
                pngBytesProvider,
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
                },
                onPinRequested: (pinAnchor, finalPngBytes) =>
                {
                    // 钉到桌面：原位创建 PinnedScreenshotWindow 接替预览窗的位置；
                    // 预览窗会在自身 CommandPin 内自行 Close，这里不重复处理。
                    // finalPngBytes 由 ScreenshotPreviewWindow 异步合成 —— 用户加过的标注（矩形 / 文字 / 马赛克 等）
                    // 会一并烧进钉图位图；无标注场景下 finalPngBytes 来自 OcrImage Lazy 编码，二次走缓存零成本
                    if (_pinnedManager is null) return;
                    var bytesToPin = finalPngBytes is { Length: > 0 } ? finalPngBytes : pngBytesProvider();
                    _pinnedManager.Pin(bytesToPin, monitor.DpiX, monitor.DpiY, pinAnchor);
                });
            _screenshotWindow = win;
            win.Closed += (_, _) => { if (ReferenceEquals(_screenshotWindow, win)) _screenshotWindow = null; };

            var afterCtorMs = timing?.ElapsedMilliseconds ?? 0;

            if (closeAfterLoad is not null)
            {
                // 关键：Loaded 触发时机 = WPF 完成布局 + PlaceWindowAtAnchorPhysical 已把窗口定位到目标
                // 物理像素，此时新窗已显示在屏幕上目标位置。在这里关闭旧窗，用户视觉感受是"原地无缝切换"
                win.Loaded += (_, _) =>
                {
                    try { closeAfterLoad.Close(); } catch { }
                };
            }
            if (timing is not null)
            {
                // 在 Loaded 触发时输出整段链路：encode = PNG 编码、dispatchEnter = 后台 → UI 线程切换、
                // ctor = ScreenshotPreviewWindow 构造 + 测量、loaded = Show -> Loaded 的渲染等待。
                // totalMs 是从 Coordinator 收到 RegionSelected 到预览窗 Loaded 的整段时间，
                // 与 OcrSelectionWindow 输出的 "mouseup -> bg dispatch" 拼起来即为整条用户感知链路
                win.Loaded += (_, _) =>
                {
                    var loadedMs = timing.ElapsedMilliseconds;
                    _log.Info("screenshot timing preview",
                        ("buildSrcMs", afterBuildMs - beforeBuildMs),
                        ("dispatchEnterMs", inUiMs - afterBuildMs),
                        ("ctorMs", afterCtorMs - inUiMs),
                        ("loadedMs", loadedMs - afterCtorMs),
                        ("totalMs", loadedMs));
                };
            }
            win.Show();
            win.Activate();
        });
    }
}
