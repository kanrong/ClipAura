using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using WpfPoint = System.Windows.Point;
using WinRectangle = System.Drawing.Rectangle;

namespace PopClip.App.UI;

internal partial class OcrResultWindow : Window
{

    // ============== 右键菜单事件 ==============

    private void OnCopySelected(object sender, RoutedEventArgs e) => CommandCopySelected();
    private void OnCopyAll(object sender, RoutedEventArgs e) => CommandCopyAll();
    private void OnCloseClicked(object sender, RoutedEventArgs e) => CommandClose();
    private void OnSwitchToQuick(object sender, RoutedEventArgs e) => CommandSwitchToQuick();
    private void OnTranslateSelected(object sender, RoutedEventArgs e) => CommandTranslateMenu(selectedOnly: true);
    private void OnTranslateAll(object sender, RoutedEventArgs e) => CommandTranslateMenu(selectedOnly: false);

    /// <summary>右键菜单"翻译全部 / 翻译选中"入口。
    /// 与工具栏 toggle 走相同的 ShowTranslationAsync，自动复用缓存。
    /// selectedOnly=true 但用户未选任何段时给 toast 提示</summary>
    private void CommandTranslateMenu(bool selectedOnly)
    {
        if (_aiText is null || !_aiText.CanRun)
        {
            ShowToast("请先在设置启用 AI 并配置 API Key");
            return;
        }
        if (_translating) { ShowToast("翻译进行中，请稍候"); return; }

        List<int> indices;
        if (selectedOnly)
        {
            indices = new();
            for (int i = 0; i < _selected.Length; i++) if (_selected[i]) indices.Add(i);
            if (indices.Count == 0)
            {
                ShowToast("没有选中任何文本（点高亮 / 拖框先选）");
                return;
            }
        }
        else
        {
            indices = Enumerable.Range(0, _result.Blocks.Count).ToList();
        }
        _ = ShowTranslationAsync(indices);
    }

    private void OnClearSelectionMenu(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        RefreshAllStates();
        UpdateStatusBar();
    }

    // ============== Command 方法（工具条 / 右键菜单 / 键盘共用入口）==============

    /// <summary>复制选中段落到剪贴板。toastSink 为 null 时反馈走主窗中央 toast；
    /// 非 null 时（如工具栏按钮调用时传自己的 ShowLocalToast）反馈贴近触发点</summary>
    public void CommandCopySelected(Action<string>? toastSink = null)
    {
        var sink = toastSink ?? ShowToast;
        var text = JoinSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            sink("没有选中任何文本");
            return;
        }
        CopyTextSilently(text);
        sink($"已复制 {SelectedCount()} 段 / {text.Length} 字");
    }

    public void CommandCopyAll(Action<string>? toastSink = null)
    {
        var sink = toastSink ?? ShowToast;
        var text = EffectiveFullText();
        if (string.IsNullOrEmpty(text)) text = JoinAllText();
        if (string.IsNullOrEmpty(text))
        {
            sink("没有识别到内容");
            return;
        }
        CopyTextSilently(text);
        sink($"已复制全部 / {text.Length} 字");
    }

    public void CommandCopyScreenshot(Action<string>? toastSink = null)
    {
        var sink = toastSink ?? ShowToast;
        try
        {
            _clipboard.SetImagePngBytes(_screenshot.GetPngBytes());
            sink("截图已复制");
        }
        catch (Exception ex)
        {
            _log.Warn("ocr screenshot copy failed", ("err", ex.Message));
            sink("复制截图失败：" + ex.Message);
        }
    }

    /// <summary>"Quick 输出"按钮：把当次 OCR 结果按 Quick 模式重新走一遍（剪贴板 + 浮窗气泡 + toast），
    /// 然后关掉结果窗。不修改 settings.OcrResultMode —— 长期偏好应通过设置面板切换，
    /// 本按钮只是一次性"我这次想要 Quick 风格的输出"的临时通道</summary>
    public void CommandSwitchToQuick()
    {
        var text = EffectiveFullText();
        if (_quickFallback is null)
        {
            // 兜底：没传回调（理论上不会发生）— 至少把全文复制走，避免按钮按了什么都不做
            if (!string.IsNullOrEmpty(text)) CopyTextSilently(text);
            ShowToast("已复制全部");
            Dispatcher.BeginInvoke(new Action(() => CloseSelf("switch-quick-fallback")), DispatcherPriority.Background);
            return;
        }
        _log.Info("ocr result quick-output triggered");

        // 先关窗，再触发 Quick 渲染。
        // 顺序很重要：Quick 渲染会触发浮窗显示，如果结果窗还在 topmost、又遮在浮窗上方，用户就看不到气泡
        var fallback = _quickFallback;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CloseSelf("quick-output");
            try { fallback(text); }
            catch (Exception ex) { _log.Warn("quick fallback failed", ("err", ex.Message)); }
        }), DispatcherPriority.Background);
    }

    /// <summary>工具栏"切到截图预览"按钮入口。
    /// 关闭结果窗后由 Coordinator 用同一张截图打开 ScreenshotPreviewWindow，
    /// 让用户能继续做"复制图片 / 保存为 PNG / 再次 OCR"等截图侧操作。
    ///
    /// 切换流程改造：
    /// 1) 把当前结果图在屏上的物理像素 rect 传给回调，让 Coordinator 用同一个位置打开截图预览，
    ///    保留用户对结果窗的拖动 / 跨屏位置惯性；
    /// 2) 本窗不在这里 Close —— 由 Coordinator 在新预览窗 Loaded 之后再统一关闭，
    ///    让"高亮 OCR 图 → 原始截图"切换视觉上几乎无缝衔接（同位置叠两层短暂共存）</summary>
    public void CommandSwitchToScreenshot()
    {
        if (_switchToScreenshot is null) return;
        _log.Info("ocr result switch-to-screenshot triggered");
        var anchor = GetCurrentImagePhysicalRect();
        try { _switchToScreenshot(anchor); }
        catch (Exception ex) { _log.Warn("switch-to-screenshot failed", ("err", ex.Message)); }
    }

    /// <summary>工具栏"开始新识别"按钮入口：交给 Coordinator 关闭本窗 + 拉起新的选区状态机。
    /// 先 Hide 本窗 + 工具栏让屏幕保持干净，再触发 reshoot 回调；
    /// 与"快捷键 / 托盘"路径不同，这条路径需要主动隐藏旧结果窗，避免 OCR 选区蒙层下还看到旧的高亮图</summary>
    public void CommandReshootOcr()
    {
        if (_reshootOcr is null) return;
        _log.Info("ocr result reshoot triggered");
        try
        {
            if (_toolbarWindow is not null) { try { _toolbarWindow.Hide(); } catch { } }
            if (_textPanelWindow is not null) { try { _textPanelWindow.Hide(); } catch { } }
            Hide();
            _reshootOcr();
        }
        catch (Exception ex) { _log.Warn("reshoot ocr failed", ("err", ex.Message)); }
    }

    /// <summary>工具栏"钉到桌面"按钮入口：把当前 OCR 结果底层的截图复制一份到 PinnedScreenshotWindow。
    /// 与"切到截图预览"的差别：钉图不在乎 OCR 结果，只复用截图像素；OCR 结果窗本身保留不动，
    /// 让用户能继续在结果窗交互而钉图独立漂浮在桌面。
    /// 重复点击会创建多个钉图实例，符合"工作台多张截图"使用直觉</summary>
    public void CommandPinToScreen()
    {
        if (_pinScreenshot is null) return;
        var anchor = GetCurrentImagePhysicalRect();
        try { _pinScreenshot(anchor); }
        catch (Exception ex) { _log.Warn("pin-to-screen failed", ("err", ex.Message)); }
    }

    /// <summary>读取当前结果图在屏幕上的物理像素位置。
    /// Window 内部从外到内的 DIP 层次：_shadowMargin（阴影留白）+ _borderWidth（主题边框）→ 图像区域。
    /// 用 GetWindowRect 取窗口物理矩形 + _outerInsetWidth 物理像素偏移，得到图像区域左上像素坐标；
    /// 宽高直接复用最初的 anchor 尺寸（图像内容未变形）。
    ///
    /// 用于"切到截图预览"按钮：让新预览窗在用户拖动 / 跨屏挪动后的当前位置打开，而不是闪回最初的框选位置</summary>
    private WinRectangle GetCurrentImagePhysicalRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return _anchorPhysical;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return _anchorPhysical;

        int insetPxX = (int)Math.Round(_outerInsetWidth * _anchorDpiX / 96.0);
        int insetPxY = (int)Math.Round(_outerInsetWidth * _anchorDpiY / 96.0);
        int x = rect.Left + insetPxX;
        int y = rect.Top + insetPxY;
        return new WinRectangle(x, y, _anchorPhysical.Width, _anchorPhysical.Height);
    }

    /// <summary>工具栏"启用 AI"按钮（仅当 AI 未启用时显示）：
    /// 直接跳到设置面板的 AI 页让用户填 API Key，比 Toast 引导更直接。
    /// 没有 openSettings 回调时降级为 Toast 提示（理论上不会发生，AppHost 已注入）</summary>
    public void CommandOpenAiSettings()
    {
        if (_openSettings is null)
        {
            ShowToast("请先在设置启用 AI 并配置 API Key");
            return;
        }
        try { _openSettings("AI"); }
        catch (Exception ex) { _log.Warn("open ai settings failed", ("err", ex.Message)); }
    }
}
