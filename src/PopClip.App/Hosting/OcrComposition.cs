using System.IO;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Ocr.Providers;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Uia.Clipboard;

namespace PopClip.App.Hosting;

/// <summary>OCR / 截图 / 钉图这一组服务。Provider 按需加载，构造期不碰 native。</summary>
internal sealed class OcrComposition : IDisposable
{
    public OcrProviderRegistry Registry { get; }
    public PinnedScreenshotManager Pinned { get; }
    public OcrCaptureCoordinator Coordinator { get; }

    private OcrComposition(
        OcrProviderRegistry registry,
        PinnedScreenshotManager pinned,
        OcrCaptureCoordinator coordinator)
    {
        Registry = registry;
        Pinned = pinned;
        Coordinator = coordinator;
    }

    public static OcrComposition Create(
        ILog log,
        AppSettings settings,
        SelectionSessionManager session,
        ClipboardWriter clipboardWriter,
        ClipboardAccess clipboardAccess,
        FloatingToolbar toolbar,
        AiTextService aiText,
        FloatingToolbarBubblePresenter bubble,
        ClipboardHistoryService? clipHistory,
        Action<string> openSettings,
        Action saveSettings)
    {
        var pluginRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
        var rapidProviders = OcrPluginLoader.LoadAll(log, pluginRoot);
        var wechatProvider = new WeChatOcrProvider(log);
        var registry = new OcrProviderRegistry(
            log,
            preferredIdReader: () => settings.OcrProviderId,
            providers: rapidProviders.Concat(new IOcrProvider[] { wechatProvider }));

        // 钉图管理器先于 Coordinator 构造：Coordinator 在"截图预览 → 钉到桌面"
        // 与"钉图双击 → OCR"两条路径上都会用到 manager。DoubleClickOcrHandler
        // 在 Coordinator 构造完成后再回填，因为 OCR 调度逻辑在 Coordinator 里
        var pinned = new PinnedScreenshotManager(log, clipboardWriter, clipHistory);
        var coordinator = new OcrCaptureCoordinator(
            log, registry, session, clipboardWriter, clipboardAccess, toolbar,
            settings, aiText,
            pinnedManager: pinned,
            bubble: bubble,
            openSettings: openSettings,
            saveSettings: saveSettings,
            clipHistory: clipHistory);
        pinned.DoubleClickOcrHandler = (window, anchor)
            => coordinator.RecognizePinnedScreenshotAsync(window, anchor);

        return new OcrComposition(registry, pinned, coordinator);
    }

    public void Dispose() => Registry.Dispose();
}
