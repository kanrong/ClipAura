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
            // Pin 态复用：保留窗口本体 / 位置 / Pin 视觉状态，只刷新标题 + 内容；
            // 未 Pin 时维持旧行为：关掉旧气泡，按 anchor 重新定位
            var bubble = AiBubbleWindow.Current is { IsPinned: true } pinned
                ? pinned
                : NewBubble();

            var anchor = _toolbar?.GetCurrentBubbleAnchor();
            var (cx, ty, mb, mt) = anchor.HasValue
                ? (anchor.Value.CenterX, anchor.Value.TopY, anchor.Value.MonitorBottomDip, anchor.Value.MonitorTopDip)
                : (SystemParameters.PrimaryScreenWidth / 2, SystemParameters.PrimaryScreenHeight / 2, double.PositiveInfinity, 0.0);

            bubble.ShowAt(
                title,
                model: "",
                canReplace: canReplace && onReplace is not null,
                onInsert: onReplace,
                onReplace: onReplace,
                onOpenInChat: null,
                anchorCenterX: cx,
                anchorTopY: ty,
                monitorBottomY: mb,
                monitorTopY: mt);
            bubble.SetCompleted(text ?? "", model: "", elapsed: TimeSpan.Zero, promptTok: 0, compTok: 0);
            bubble.ScrollBodyToTop();
        });
    }

    private AiBubbleWindow NewBubble()
    {
        AiBubbleWindow.DismissCurrent();
        return new AiBubbleWindow(_log);
    }
}
