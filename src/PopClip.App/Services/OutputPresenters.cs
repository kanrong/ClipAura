using System;
using System.Threading.Tasks;
using System.Windows;
using PopClip.App.UI;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Services;

/// <summary>把"独立结果窗口"模式落到 SmartResultWindow 上。
/// 仅负责窗口生命周期，自身不持有结果文本</summary>
internal sealed class SmartResultDialogPresenter : IResultDialogPresenter
{
    public void Show(string title, string referenceText, string resultText)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var window = new SmartResultWindow(title, referenceText, resultText);
            window.Show();
            window.Activate();
        });
    }
}

/// <summary>把"轻量气泡"模式落到 AiBubbleWindow 上。
/// 智能动作的结果通常已就绪（非流式），故 ShowStatic 调一次 ShowAt 紧跟 SetCompleted，
/// 让气泡直接处于"已完成"状态，省去"请求中…" 闪烁</summary>
internal sealed class FloatingToolbarBubblePresenter : IInlineBubblePresenter
{
    private readonly ILog _log;
    private readonly FloatingToolbar _toolbar;

    public FloatingToolbarBubblePresenter(ILog log, FloatingToolbar toolbar)
    {
        _log = log;
        _toolbar = toolbar;
    }

    public void ShowStatic(string title, string text, bool canReplace, Func<string, Task>? onReplace = null)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var anchor = _toolbar.GetCurrentBubbleAnchor()
                ?? new BubbleAnchor(
                    SystemParameters.PrimaryScreenWidth / 2,
                    SystemParameters.PrimaryScreenHeight / 2,
                    double.PositiveInfinity,
                    0.0);
            ShowStaticCore(title, text, anchor, canReplace, onReplace, translateAsync: null,
                preferredMinWidth: null, preferredMaxWidth: null);
        });
    }

    /// <summary>用调用方提供的 anchor 显示静态气泡，常用于没有浮窗可锚定的独立流程
    /// （如 OCR Quick 模式：截图框就是 anchor，浮窗根本不会出现）。
    /// caller 负责把 OCR 截图框等物理像素信息换算到 DIP，避免本方法依赖具体 anchor 来源。
    /// translateAsync 非空时气泡会在按钮栏显示"翻译"按钮，按缓存切换语义。
    ///
    /// preferredMinWidth / preferredMaxWidth：让调用方按内容形态指定气泡宽度区间。
    /// OCR Quick 路径会传入更大的区间（如 480~640），让整段识别文本有足够行宽不频繁折行</summary>
    public void ShowStaticAt(
        string title,
        string text,
        BubbleAnchor anchor,
        bool canReplace = false,
        Func<string, Task>? onReplace = null,
        Func<string, Task<string?>>? translateAsync = null,
        double? preferredMinWidth = null,
        double? preferredMaxWidth = null)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
            ShowStaticCore(title, text, anchor, canReplace, onReplace, translateAsync, preferredMinWidth, preferredMaxWidth));
    }

    private void ShowStaticCore(
        string title,
        string text,
        BubbleAnchor anchor,
        bool canReplace,
        Func<string, Task>? onReplace,
        Func<string, Task<string?>>? translateAsync,
        double? preferredMinWidth,
        double? preferredMaxWidth)
    {
        // Pin 态复用：保留窗口本体 / 位置 / Pin 视觉状态，只刷新标题 + 内容；
        // 未 Pin 时维持旧行为：关掉旧气泡，按 anchor 重新定位
        var bubble = AiBubbleWindow.Current is { IsPinned: true } pinned
            ? pinned
            : NewBubble();

        bubble.ShowAt(
            title,
            model: "",
            canReplace: canReplace && onReplace is not null,
            onInsert: onReplace,
            onReplace: onReplace,
            onOpenInChat: null,
            anchorCenterX: anchor.CenterX,
            anchorTopY: anchor.TopY,
            monitorBottomY: anchor.MonitorBottomDip,
            monitorTopY: anchor.MonitorTopDip,
            preferredMinWidth: preferredMinWidth,
            preferredMaxWidth: preferredMaxWidth);
        bubble.SetCompleted(text ?? "", model: "", elapsed: TimeSpan.Zero, promptTok: 0, compTok: 0);
        bubble.ScrollBodyToTop();
        // 翻译能力注入：放在 SetCompleted 之后是因为按钮 IsEnabled 依赖 _accumText.HasValue
        bubble.ConfigureTranslate(translateAsync);
    }

    private AiBubbleWindow NewBubble()
    {
        AiBubbleWindow.DismissCurrent();
        return new AiBubbleWindow(_log);
    }
}
