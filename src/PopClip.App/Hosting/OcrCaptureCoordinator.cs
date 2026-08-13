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

    /// <summary>截图自动保存到磁盘的辅助器。
    /// 与"另存为"对话框路径独立，由 ScreenshotAutoSaveEnabled 开关驱动；
    /// 失败时返回 ScreenshotAutoSaveResult.Failure 并由 Coordinator 决定如何提示</summary>
    private readonly ScreenshotAutoSaver _autoSaver;

    /// <summary>钉图管理器：把已截好的图像固定到桌面。可空 —— 宿主未注入时退到旧行为。
    /// 由 ScreenshotPreviewToolbarWindow / OcrResultToolbarWindow 的"钉到桌面"按钮触发</summary>
    private readonly PinnedScreenshotManager? _pinnedManager;

    /// <summary>剪贴板历史服务（M6）。可空：宿主未注入时不写图片入库；
    /// 截图复制 / 钉图 / 自动保存均会调 AppendImage 让历史窗"图片"标签页可回看</summary>
    private readonly ClipboardHistoryService? _clipHistory;

    /// <summary>"打开设置面板并定位到某个分页"的跨层回调。
    /// 由 AppHost 注入，参数为页面 tag（如 "AI" / "Ocr"），方便结果窗里的
    /// "启用 AI / 调整 OCR" 类引导按钮直达对应设置位置。可空：宿主未传入时按钮仅在 Toast 提示</summary>
    private readonly Action<string>? _openSettings;

    private OcrSelectionWindow? _currentWindow;
    private OcrResultWindow? _resultWindow;
    private ScreenshotPreviewWindow? _screenshotWindow;
    private ScreenshotCountdownWindow? _countdownWindow;

    private enum CapturePurpose
    {
        Ocr,
        Screenshot,
    }

    /// <summary>一次截图采集的结果。PngBytes 不直接持有 —— 改走 OcrImage.GetPngBytes 的 Lazy 路径，
    /// 让 OCR-only / 用户未触发任何"需要 PNG 的动作"时彻底跳过 PNG 编码。
    /// 任何消费方需要字节流时统一调 Image.GetPngBytes() 即可，二次调用走缓存零成本</summary>
    private sealed record CapturedImage(
        OcrImage Image,
        SelectionRect AnchorRect,
        string Source);

    public OcrCaptureCoordinator(
        ILog log,
        OcrProviderRegistry registry,
        SelectionSessionManager session,
        ClipboardWriter clipboard,
        ClipboardAccess clipboardAccess,
        FloatingToolbar toolbar,
        AppSettings settings,
        AiTextService aiText,
        PinnedScreenshotManager? pinnedManager = null,
        FloatingToolbarBubblePresenter? bubble = null,
        Action<string>? openSettings = null,
        Action? saveSettings = null,
        ClipboardHistoryService? clipHistory = null)
    {
        _log = log;
        _registry = registry;
        _session = session;
        _clipboard = clipboard;
        _clipboardAccess = clipboardAccess;
        _toolbar = toolbar;
        _settings = settings;
        _aiText = aiText;
        _pinnedManager = pinnedManager;
        _bubble = bubble;
        _openSettings = openSettings;
        _saveSettings = saveSettings;
        _clipHistory = clipHistory;
        _autoSaver = new ScreenshotAutoSaver(log, settings, saveSettings);
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

    /// <summary>延时截图：先弹倒计时浮层，归零后再进入选区蒙层。
    /// delaySec ≤ 0 等价于直接调 TriggerScreenshot</summary>
    public void TriggerScreenshotDelayed(int delaySec)
    {
        if (delaySec <= 0)
        {
            TriggerScreenshot();
            return;
        }
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            // 同一时刻只允许一个倒计时（避免叠层 + ESC 取消时不知道关哪个）
            if (_countdownWindow is not null)
            {
                try { _countdownWindow.Activate(); } catch { }
                return;
            }
            var win = new ScreenshotCountdownWindow(
                delaySec,
                onElapsed: () =>
                {
                    _countdownWindow = null;
                    TriggerScreenshot();
                },
                onCancelled: () =>
                {
                    _countdownWindow = null;
                    _log.Info("delayed screenshot cancelled by user");
                });
            _countdownWindow = win;
            win.Show();
        });
    }

    public void TriggerClipboardImage(SelectionRect anchorRect)
    {
        var provider = PickActiveOrNotify();
        if (provider is null) return;
        provider.PrewarmInBackground();
        _ = Task.Run(() => RunClipboardImageAsync(anchorRect));
    }

    /// <summary>钉图窗双击触发的 OCR：用钉图缓存的 PNG 字节解码后跑一次 RecognizeImageAsync。
    /// PinnedScreenshotManager.DoubleClickOcrHandler 会回调到这里。
    /// 与剪贴板图片 OCR 类似，但 anchor 来自钉图当前在屏幕上的物理位置，
    /// 让 OCR 结果窗在钉图原位弹出</summary>
    public void RecognizePinnedScreenshotAsync(PinnedScreenshotWindow window, Rectangle anchor)
    {
        var pngBytes = window.PngBytes;
        if (pngBytes is null || pngBytes.Length == 0) return;
        var anchorRect = new SelectionRect(anchor.Left, anchor.Top, anchor.Right, anchor.Bottom);
        _ = Task.Run(async () =>
        {
            try
            {
                using var ms = new MemoryStream(pngBytes);
                using var decoded = new Bitmap(ms);
                using var bitmap = new Bitmap(decoded);
                var image = CreateOcrImage(bitmap, pngFactory: () => pngBytes);
                await RecognizeImageAsync(image, anchorRect, "pinned-screenshot").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("pinned screenshot ocr failed", ("err", ex.Message));
            }
        });
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
        // 性能埋点：sw 从"RegionSelected 抵达 Coordinator"开始计时，
        // 与 OcrSelectionWindow 的"mouseup -> bg dispatch"埋点拼起来覆盖整条"松手 → 预览出现"链路
        var sw = Stopwatch.StartNew();
        try
        {
            // 等 DWM 消化选区窗 Hide/Close 对桌面合成的影响，再截屏。
            //
            // 严格模式由 purpose 决定：OCR 路径必须严格，否则截到半透明蒙层会让 OCR 输出乱码；
            // Screenshot 路径放宽 —— OcrSelectionWindow.FinalizeSelection 里已经 Hide + Dispatcher.Background
            // 等过一帧 DWM 合成，这里再 DwmFlush 是冗余的双重保险，对 OCR 必要、对截图预览没价值（偶尔
            // 截到一点蒙层用户能立刻发现并重截）。砍掉这一段在 1080p 上能省 ~16ms 的"松手 → 预览出现"延迟
            var strictFlush = purpose == CapturePurpose.Ocr;
            await WaitForSelectionOverlayToDisappearAsync(strictFlush).ConfigureAwait(false);
            var afterFlushMs = sw.ElapsedMilliseconds;

            // RegionSelected 回调在 UI 线程触发；WaitForSelectionOverlayToDisappearAsync 命中
            // DwmFlush==0 立即成功路径时 async 链是同步完成的，await 不会真正 yield，
            // 后续若直接 CaptureRegion 会在 UI 线程跑 CopyFromScreen + LockBits + Marshal.Copy，
            // 全屏截图能阻塞 100~300ms。强制 Task.Run 入池让 UI 线程在等待截图期间仍能处理消息
            var captured = await Task.Run(() => CaptureRegion(physical)).ConfigureAwait(false);
            var afterCaptureMs = sw.ElapsedMilliseconds;

            _log.Info("screenshot timing capture",
                ("flushMs", afterFlushMs),
                ("captureMs", afterCaptureMs - afterFlushMs),
                ("size", $"{physical.Width}x{physical.Height}"),
                ("purpose", purpose.ToString()));

            if (purpose == CapturePurpose.Ocr)
            {
                await RecognizeImageAsync(captured.Image, captured.AnchorRect, captured.Source, closeAfterLoad: null).ConfigureAwait(false);
            }
            else
            {
                await HandleScreenshotCaptureAsync(captured, sw).ConfigureAwait(false);
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

        // 这里不再立刻 image.GetPngBytes()：OcrImage 的 Lazy<byte[]> 在真正需要 PNG 的消费方
        // （剪贴板 / 自动保存 / 预览窗 / 历史入库）首次取值时编码并缓存，避免 OCR-only 路径
        // 多一次 16~64 MB 的全图 PNG 编码
        var image = CreateOcrImage(bitmap);
        var anchorRect = new SelectionRect(physical.Left, physical.Top, physical.Right, physical.Bottom);
        return new CapturedImage(image, anchorRect, $"{physical.Width}x{physical.Height}");
    }

    /// <summary>等待选区蒙层从屏幕合成里彻底消失。
    /// strict=true：OCR 路径走 DwmFlush 严格等待；DwmFlush 失败退回 32ms 短延迟兜底。
    /// strict=false：截图预览路径跳过 DwmFlush —— OcrSelectionWindow 已经 Hide + Dispatcher.Background
    /// 等过一帧合成，这里不再叠等。截图预览偶尔截到蒙层一角用户也能看出来重截，OCR 则必须严格</summary>
    private static async Task WaitForSelectionOverlayToDisappearAsync(bool strict)
    {
        if (!strict) return;
        var hr = NativeMethods.DwmFlush();
        if (hr == 0) return;

        await Task.Delay(32).ConfigureAwait(false);
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
}

