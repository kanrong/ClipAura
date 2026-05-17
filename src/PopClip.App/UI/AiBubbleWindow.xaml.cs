using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PopClip.Core.Logging;
using PopClip.Hooks.Window;

namespace PopClip.App.UI;

/// <summary>AI 气泡的屏幕锚点，单位 DIP。
/// CenterX/TopY 一般来源于 FloatingToolbar 的下沿水平居中；
/// MonitorBottomDip / MonitorTopDip 用于"下方放不下时翻到上方"的边界判断</summary>
public readonly record struct BubbleAnchor(double CenterX, double TopY, double MonitorBottomDip, double MonitorTopDip);

/// <summary>浮动工具栏旁的 AI 流式气泡窗。
/// 与 ToolbarToastWindow 的差异：toast 仅承载一段静态文字，气泡承载流式正文 + 多操作按钮，
/// 适合"翻译/解释"等需要让用户在结果上做"插入/替换/复制/打开完整对话"二次决策的场景。
///
/// 关键约束：
/// - WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，不抢焦点、不出现在 Alt+Tab；
/// - 同一时刻仅有一个气泡（Current 静态字段），新气泡显示前关掉旧气泡，避免叠成一摞；
/// - ESC 关闭：因窗口不获取焦点，键盘 ESC 由 AppHost 通过 InputWatcher.GlobalKeyHandler
///   分发到 TryHandleEscape，这里只判定可见性并关闭。</summary>
internal partial class AiBubbleWindow : Window
{
    /// <summary>当前显示中的气泡。同一刻只允许一个：新内联请求触发时，旧气泡先关。
    /// 静态访问让 AppHost / 全局键盘分发不必持有具体实例的引用</summary>
    public static AiBubbleWindow? Current { get; private set; }

    /// <summary>当前实例是否处于 Pin 态。Presenter 在新内容到达时据此决定：
    /// 复用现有气泡（保留位置 / Pin 状态）还是关掉旧气泡新建</summary>
    public bool IsPinned => _isPinned;

    private readonly ILog _log;
    private string _model = "";
    private string _accumText = "";
    private Func<string, Task>? _onInsert;
    private Func<string, Task>? _onReplace;
    private Action<string>? _onOpenInChat;
    private bool _streamingDone;
    /// <summary>缓存的窗口句柄。
    /// 必须缓存而不是每次访问 WindowInteropHelper.Handle：
    /// ContainsScreenPoint 由 SelectionSessionManager.InputPumpAsync 在 ThreadPool 线程调用，
    /// 任何对 WPF DependencyObject 的属性读取都会抛 InvalidOperationException 让 InputPump 整体崩溃</summary>
    private nint _hwnd;
    /// <summary>窗口可见性的非 WPF 副本。
    /// 全局鼠标钩子线程要查 bubble 是否可见来决定"点击在 bubble 内/外"，
    /// 但访问 IsVisible 是 DependencyObject 操作，必须只能 UI 线程；
    /// 用 volatile bool 把"已显示/已关闭"状态镜像到字段，跨线程读取安全</summary>
    private volatile bool _isShown;
    /// <summary>是否处于"固定（Pin）"态。Pinned=true 时禁用 click-outside / mouse-leave 自动 dismiss，
    /// 让用户能在不被打扰的情况下与气泡交互（多次复制 / 阅读长文 / 切到旁边的窗口贴入数据）。
    /// ESC 仍能关，因为 ESC 是用户明确意图。volatile 是因为 IsCurrentPinned 在鼠标钩子线程被读到</summary>
    private volatile bool _isPinned;

    public AiBubbleWindow(ILog log)
    {
        _log = log;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosedInternal;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd != 0) WindowStyleHelper.ApplyNoActivateToolWindow(_hwnd);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _isShown = (bool)e.NewValue;

    private void OnClosedInternal(object? sender, EventArgs e)
    {
        _isShown = false;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>显示气泡。anchorCenterX / anchorTopY 通常取浮窗下沿水平居中点；
    /// monitorBottomY 用于"下方放不下时翻到上方"判断，调用方传入当前 anchor 所在屏幕的工作区底沿即可。
    ///
    /// 复用语义：当前窗口已是 Pin 态时（presenter 复用同一个实例承载新内容），
    /// 跳过位置重算 + 保留 Pin 状态，让用户体验是"原位刷新"而非"关掉重弹"</summary>
    public void ShowAt(
        string title,
        string model,
        bool canReplace,
        Func<string, Task>? onInsert,
        Func<string, Task>? onReplace,
        Action<string>? onOpenInChat,
        double anchorCenterX,
        double anchorTopY,
        double monitorBottomY,
        double monitorTopY)
    {
        // 记录 Pin 态后再清正文：清正文不应改 Pin 状态，
        // 否则用户用 Pin 的"刷新"语义会失效
        var keepPinned = _isPinned;

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "AI" : title;
        _model = model ?? "";
        MetaText.Text = _model;
        StatusText.Text = "请求中…";
        _accumText = "";
        BodyText.Text = "";
        _streamingDone = false;
        _onInsert = onInsert;
        _onReplace = onReplace;
        _onOpenInChat = onOpenInChat;
        if (!keepPinned)
        {
            _isPinned = false;
        }
        ApplyPinVisualState();
        InsertButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        OpenChatButton.IsEnabled = false;
        ReplaceButton.Visibility = canReplace ? Visibility.Visible : Visibility.Collapsed;
        InsertButton.Visibility = onInsert is not null ? Visibility.Visible : Visibility.Collapsed;
        OpenChatButton.Visibility = onOpenInChat is not null ? Visibility.Visible : Visibility.Collapsed;

        // 先把窗口贴到屏幕外测量真实尺寸，再瞬移到目标坐标，避免用户看到 (0,0) → 目标 的 1 帧闪动
        SizeToContent = SizeToContent.WidthAndHeight;
        if (!IsVisible)
        {
            Left = -32000;
            Top = -32000;
            base.Show();
        }
        UpdateLayout();

        if (keepPinned && IsVisible)
        {
            // Pin 态复用：不动 Left/Top，让气泡停在用户拖动后的位置 / 当前位置；
            // SizeToContent 仍会让窗口宽高跟着新内容伸缩，但锚点不变
            Current = this;
            return;
        }

        var w = ActualWidth > 0 ? ActualWidth : 360;
        var h = ActualHeight > 0 ? ActualHeight : 200;
        var left = anchorCenterX - w / 2;
        var top = anchorTopY;

        if (top + h > monitorBottomY - 4)
        {
            // 下方放不下：翻到 anchor 上方，让"翻译"按钮还能看到原浮窗
            top = anchorTopY - h - 16;
        }
        if (top < monitorTopY + 4) top = monitorTopY + 4;
        Left = left;
        Top = top;

        Current = this;
    }

    /// <summary>流式 delta 拼接到正文末尾。在 streaming 完成后被调用会被忽略，
    /// 避免完成后误把延迟到达的 delta 追加到 final 之后</summary>
    public void AppendDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta) || _streamingDone) return;
        _accumText += delta;
        BodyText.AppendText(delta);
        BodyText.ScrollToEnd();
        BodyScroll.ScrollToEnd();
        StatusText.Text = "接收中…";
    }

    public void SetCompleted(string finalText, string model, TimeSpan elapsed, int promptTok, int compTok)
    {
        _streamingDone = true;
        // 流式 delta 偶有丢字风险，且 OpenAI 兼容服务在最后帧才返回 trim 后的完整文本；
        // 完成时若 final 与累积值不一致，以 final 为准重写 BodyText
        if (!string.IsNullOrEmpty(finalText) && finalText != _accumText)
        {
            _accumText = finalText;
            BodyText.Text = finalText;
            BodyText.ScrollToEnd();
            BodyScroll.ScrollToEnd();
        }
        if (!string.IsNullOrWhiteSpace(model)) _model = model;

        StatusText.Text = "已完成";
        var meta = _model;
        meta += string.IsNullOrEmpty(meta) ? $"{elapsed.TotalSeconds:0.0}s" : $" · {elapsed.TotalSeconds:0.0}s";
        if (promptTok > 0 || compTok > 0) meta += $" · {promptTok}→{compTok} tok";
        MetaText.Text = meta;

        var has = !string.IsNullOrWhiteSpace(_accumText);
        InsertButton.IsEnabled = has && _onInsert is not null;
        ReplaceButton.IsEnabled = has && _onReplace is not null;
        CopyButton.IsEnabled = has;
        OpenChatButton.IsEnabled = has && _onOpenInChat is not null;
    }

    public void ScrollBodyToTop()
    {
        BodyText.CaretIndex = 0;
        BodyText.ScrollToHome();
        BodyScroll.ScrollToTop();
    }

    public void SetFailed(string message)
    {
        _streamingDone = true;
        StatusText.Text = "请求失败";
        BodyText.Text = message;
        InsertButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        CopyButton.IsEnabled = !string.IsNullOrEmpty(message);
        OpenChatButton.IsEnabled = false;
    }

    public void SetCancelled()
    {
        _streamingDone = true;
        StatusText.Text = "已停止";
        var has = !string.IsNullOrWhiteSpace(_accumText);
        InsertButton.IsEnabled = has && _onInsert is not null;
        ReplaceButton.IsEnabled = has && _onReplace is not null;
        CopyButton.IsEnabled = has;
        OpenChatButton.IsEnabled = has && _onOpenInChat is not null;
    }

    /// <summary>由 AppHost 通过全局键盘分发调用：气泡可见且键为 ESC 时关闭并 return true，
    /// 让该按键不再向其它 handler 传递</summary>
    public static bool TryHandleEscape()
    {
        var bubble = Current;
        if (bubble is null || !bubble.IsVisible) return false;
        bubble.Dispatcher.Invoke(bubble.Close);
        return true;
    }

    /// <summary>判断屏幕坐标是否落在气泡矩形内，用于"气泡外点击关闭"判定。
    /// 由 ThreadPool 线程的全局鼠标钩子调用，全程不访问任何 WPF DependencyObject —
    /// 改用 _hwnd 缓存 + volatile bool _isShown 副本，跨线程安全</summary>
    public static bool ContainsScreenPoint(int x, int y)
    {
        var bubble = Current;
        if (bubble is null || !bubble._isShown) return false;
        var hwnd = bubble._hwnd;
        if (hwnd == 0) return false;
        if (!Hooks.Interop.NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    /// <summary>对外暴露"当前气泡是否已被用户固定"，给 SelectionSessionManager 决定
    /// 是否跳过 click-outside / mouse-leave 等自动 dismiss。
    /// 与 ContainsScreenPoint 一样使用 volatile 副本，跨线程安全</summary>
    public static bool IsCurrentPinned => Current?._isPinned == true;

    /// <summary>DismissCurrent 一律强制关闭，不再受 Pin 状态影响 — 调用方（如新动作触发、ESC）
    /// 已经表达了"我要换内容/我要关掉"的明确意图。Pin 只用于挡住"自动"消失</summary>
    public static void DismissCurrent()
    {
        var bubble = Current;
        if (bubble is null) return;
        bubble.Dispatcher.Invoke(bubble.Close);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>整条 Header 充当标题栏：按下鼠标左键就 DragMove。
    /// 必须只在 ButtonState=Pressed 时调用一次，否则 WPF 会抛 InvalidOperationException</summary>
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        // 点中 Header 内的子按钮（Pin / 关闭）时不要拖动，避免按钮 Click 事件被吞
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException ex)
        {
            // WS_EX_NOACTIVATE 窗口偶发"窗口未捕获鼠标"导致 DragMove 抛异常，
            // 这种情况下放弃单次拖动即可，不影响后续交互。
            // Debug 级别记录便于定位 IDE 输出窗口的 first-chance exception 来源
            _log.Debug("bubble dragmove swallowed", ("ex", ex.GetType().Name), ("msg", ex.Message));
        }
    }

    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        ApplyPinVisualState();
    }

    private void ApplyPinVisualState()
    {
        if (PinButton is null) return;
        PinButton.Opacity = _isPinned ? 1.0 : 0.7;
        PinButton.Background = _isPinned
            ? (Brush?)TryFindResource("ToolbarAccentSoft") ?? Brushes.Transparent
            : Brushes.Transparent;
        PinButton.ToolTip = _isPinned
            ? "已固定（再次点击取消固定）"
            : "将气泡固定在最前，避免外部点击 / 鼠标离开自动关闭";
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var cur = start;
        while (cur is not null)
        {
            if (cur is T t) return t;
            cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }

    private async void OnInsertClicked(object sender, RoutedEventArgs e)
    {
        if (_onInsert is null || string.IsNullOrEmpty(_accumText)) return;
        try
        {
            await _onInsert(_accumText).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn("ai bubble insert failed", ("err", ex.Message));
        }
        Close();
    }

    private async void OnReplaceClicked(object sender, RoutedEventArgs e)
    {
        if (_onReplace is null || string.IsNullOrEmpty(_accumText)) return;
        try
        {
            await _onReplace(_accumText).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn("ai bubble replace failed", ("err", ex.Message));
        }
        Close();
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_accumText)) return;
        try
        {
            Clipboard.SetText(_accumText);
            StatusText.Text = "已复制 ✓";
        }
        catch (Exception ex)
        {
            _log.Warn("ai bubble copy failed", ("err", ex.Message));
        }
    }

    private void OnOpenChatClicked(object sender, RoutedEventArgs e)
    {
        if (_onOpenInChat is null) return;
        try
        {
            _onOpenInChat(_accumText);
        }
        catch (Exception ex)
        {
            _log.Warn("ai bubble open-in-chat failed", ("err", ex.Message));
        }
        Close();
    }
}
