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

    /// <summary>打开右侧识别文本面板（不存在则新建，已存在则显示并刷新）。
    /// initial=true 时按"主窗右侧"策略放置；后续切换显隐时尊重用户拖动后的位置。
    ///
    /// 创建顺序与 PositionToolbarInitially 保持一致：先 Show 让面板测出 ActualWidth/Height，
    /// 再用 Win32 SetWindowPos 把面板按物理像素绝对定位到目标位置，规避 PerMonitor V2 下
    /// Window.Left/Top setter 用主屏 DPI 解读 DIP 的跨屏跳屏问题</summary>
    public void OpenTextPanel(bool initial, bool persistPreference = true)
    {
        if (_textPanelWindow is null)
        {
            // 面板复制走的是"当前显示内容"，自然涵盖原文 / 译文两种态。
            // 翻译回调只在 AI 启用时传入；面板根据回调是否为 null 决定是否显示翻译按钮
            Func<string, Task<string?>>? translateAsync = (_aiText is not null && _aiText.CanRun)
                ? async (source) =>
                {
                    if (string.IsNullOrWhiteSpace(source)) return null;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    try
                    {
                        var list = await _aiText!.TranslateBatchAsync(new[] { source }, cts.Token).ConfigureAwait(true);
                        return list.Count > 0 ? (list[0] ?? "").Trim() : null;
                    }
                    catch (OperationCanceledException)
                    {
                        ShowToast("翻译超时（60 秒）");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("panel translate failed", ("err", ex.Message));
                        ShowToast("翻译失败：" + ex.Message);
                        return null;
                    }
                }
                : null;

            _textPanelWindow = new OcrResultTextPanelWindow(
                onCopyAll: () =>
                {
                    var text = _textPanelWindow?.CurrentDisplayedText ?? "";
                    if (string.IsNullOrEmpty(text))
                    {
                        text = EffectiveFullText();
                        if (string.IsNullOrEmpty(text)) text = JoinAllText();
                    }
                    if (!string.IsNullOrEmpty(text)) CopyTextSilently(text);
                    // 反馈走面板自己的 ShowLocalToast — 比飞回主结果窗中央 toast 更贴近用户操作点
                    var msg = string.IsNullOrEmpty(text) ? "没有识别到内容" : $"已复制 / {text.Length} 字";
                    _textPanelWindow?.ShowLocalToast(msg);
                },
                onClose: () =>
                {
                    // 用户点关闭：直接收起面板，保留窗口对象方便下次切换显示时复用位置 / 内容
                    HideTextPanel();
                },
                translateAsync: translateAsync);
            _textPanelWindow.Owner = this;
            // 关键：刚 new 出来时不要在 OS 默认 (0,0) 闪现，先挪屏外再 Show。
            // 默认 Width 加宽到 480 让一行能容纳约 30~40 个中文 / 60~80 个 ASCII，
            // 整段段落不被频繁折行，与 OCR Quick 气泡的阅读宽度保持一致
            _textPanelWindow.Left = -32000;
            _textPanelWindow.Top = -32000;
            _textPanelWindow.Width = 480;
            _textPanelWindow.Height = Math.Max(160, Math.Min(560, ActualHeight));
            _textPanelWindow.Show();
            UpdateTextPanelContent();
            PositionTextPanelInitially();
        }
        else
        {
            UpdateTextPanelContent();
            _textPanelWindow.Show();
            if (initial) PositionTextPanelInitially();
        }

        _toolbarWindow?.SetTextPanelVisible(true);
        PersistTextPanelPreference(visible: true, persistPreference);
    }

    public void HideTextPanel(bool persistPreference = true)
    {
        try { _textPanelWindow?.Hide(); } catch { }
        _toolbarWindow?.SetTextPanelVisible(false);
        PersistTextPanelPreference(visible: false, persistPreference);
    }

    private void PersistTextPanelPreference(bool visible, bool persistPreference)
    {
        if (!persistPreference || _settings.OcrTextPanelVisible == visible) return;
        _settings.OcrTextPanelVisible = visible;
        try { _saveSettings?.Invoke(); }
        catch (Exception ex) { _log.Warn("save ocr text panel preference failed", ("err", ex.Message)); }
    }

    /// <summary>把文本面板放到主窗右侧；右侧空间不够时落到左侧；左侧也不够时落到主窗下方。
    /// 与 PositionToolbarInitially 同一策略：用 anchor monitor 的物理像素绝对定位，
    /// 跨屏不会跳屏，多 DPI 下尺寸视觉一致</summary>
    private void PositionTextPanelInitially()
    {
        if (_textPanelWindow is null) return;
        var tp = _textPanelWindow;

        var tpHwnd = new WindowInteropHelper(tp).Handle;
        if (tpHwnd == IntPtr.Zero) return;

        var tpWDip = tp.Width > 0 ? tp.Width : 320;
        var tpHDip = tp.Height > 0 ? tp.Height : 360;
        int tpWPx = (int)Math.Ceiling(tpWDip * _anchorDpiX / 96.0);
        int tpHPx = (int)Math.Ceiling(tpHDip * _anchorDpiY / 96.0);

        int insetPxX = (int)Math.Round(_outerInsetWidth * _anchorDpiX / 96.0);
        int insetPxY = (int)Math.Round(_outerInsetWidth * _anchorDpiY / 96.0);
        int mainLeftPx = _anchorPhysical.Left - insetPxX;
        int mainTopPx = _anchorPhysical.Top - insetPxY;
        int mainWidthPx = _anchorPhysical.Width + insetPxX * 2;
        int mainHeightPx = _anchorPhysical.Height + insetPxY * 2;
        int mainRightPx = mainLeftPx + mainWidthPx;
        int mainBottomPx = mainTopPx + mainHeightPx;

        var monitor = MonitorQuery.FromRect(
            _anchorPhysical.Left, _anchorPhysical.Top,
            _anchorPhysical.Right, _anchorPhysical.Bottom);
        int gapPx = (int)Math.Round(8 * _anchorDpiX / 96.0);

        int leftPx;
        int topPx;
        // 优先右侧：截图右边 + gap
        if (mainRightPx + gapPx + tpWPx <= monitor.WorkRight)
        {
            leftPx = mainRightPx + gapPx;
            topPx = mainTopPx;
        }
        // 其次左侧
        else if (mainLeftPx - gapPx - tpWPx >= monitor.WorkLeft)
        {
            leftPx = mainLeftPx - gapPx - tpWPx;
            topPx = mainTopPx;
        }
        // 都不够：放主窗下方左对齐
        else
        {
            leftPx = mainLeftPx;
            topPx = mainBottomPx + gapPx;
        }

        // 边界保护：避免被推出 anchor monitor 工作区
        if (leftPx < monitor.WorkLeft) leftPx = monitor.WorkLeft;
        if (leftPx + tpWPx > monitor.WorkRight) leftPx = Math.Max(monitor.WorkLeft, monitor.WorkRight - tpWPx);
        if (topPx < monitor.WorkTop) topPx = monitor.WorkTop;
        if (topPx + tpHPx > monitor.WorkBottom) topPx = Math.Max(monitor.WorkTop, monitor.WorkBottom - tpHPx);

        NativeMethods.SetWindowPos(
            tpHwnd,
            NativeMethods.HWND_TOPMOST,
            leftPx, topPx, Math.Max(1, tpWPx), Math.Max(1, tpHPx),
            NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>用当前 _result + 选中状态 + 译文 overlay 刷新文本面板内容。
    /// 内容选择：有选中 → 显示选中段落；无选中 → 显示整段识别文本。
    /// 顶部元信息显示"段数 / 字数"，让用户对识别结果体量一目了然</summary>
    /// <summary>同步预览面板的"源文本"。一直传原文，不传 Effective（带主窗 polygon overlay 译文的）—
    /// 否则工具栏切到译文模式时面板正文会被主窗 polygon overlay 污染。
    /// 预览面板拥有自己的翻译按钮和缓存，原文 / 译文显示状态由面板自身维护，与工具栏完全独立。
    /// SetContent 内部会自动判断 _originalText 是否变化、是否需要让缓存失效</summary>
    private void UpdateTextPanelContent()
    {
        if (_textPanelWindow is null) return;

        // loading 态下还没有任何 block，面板内显示"识别中…"占位。
        // 避免空白面板让用户误以为窗口卡死
        if (_isLoading)
        {
            _textPanelWindow.SetContent("识别中…");
            _textPanelWindow.SetMeta("OCR 正在处理截图");
            return;
        }

        int sel = SelectedCount();
        string body;
        string meta;
        if (sel > 0)
        {
            body = JoinSelectedOriginalText();
            meta = $"已选 {sel}/{_result.Blocks.Count} 段 · {body.Length} 字";
        }
        else
        {
            body = OriginalFullText();
            meta = $"{_result.Blocks.Count} 段 · {body.Length} 字";
        }
        _textPanelWindow.SetContent(body);
        _textPanelWindow.SetMeta(meta);
    }

    /// <summary>工具栏按钮触发：在显隐两态之间切换。
    /// 隐藏状态包括"窗口未创建"和"已 Hide"，都通过 OpenTextPanel(initial=false) 重新显示</summary>
    public void CommandToggleTextPanel()
    {
        if (_textPanelWindow is null || _textPanelWindow.Visibility != Visibility.Visible)
        {
            OpenTextPanel(initial: _textPanelWindow is null);
        }
        else
        {
            HideTextPanel();
        }
    }
}
