using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MahApps.Metro.IconPacks;
using PopClip.Hooks.Interop;

namespace PopClip.App.UI;

/// <summary>OCR 结果窗的右侧 / 旁挂"识别文本面板"：把整段识别文字一次性铺开展示。
///
/// 设计动机：
/// Interactive 模式以原图作背景叠 polygon，单段选择固然精修，但用户想"通读整段"或"复制大段"时
/// 必须不断 hover/Click 不同 polygon 才能凑出全文。这个面板把 Quick 模式的"全文气泡"
/// 以独立可拖窗口的形式挂在结果窗旁边 — 既保留 Interactive 的视觉对照，又提供 Quick 的整段阅读 / 复制。
///
/// 实现要点：
/// - 透明无边框 Window，Owner=OcrResultWindow，主窗关闭一并关闭；
/// - 顶部 HeaderBorder 承担拖动（DragMove），右下角 ResizeGrip 承担拖拉改大小（NCHITTEST 模拟）；
/// - TextBox 用与 AiBubbleWindow 同款只读模板，Quick / Interactive 两条路径下阅读体验视觉统一；
/// - 翻译按钮内置，缓存机制独立于主结果窗：译文仅显示在面板内（不在原图上叠加），
///   再次点击切回原文、第二次切回译文走缓存不重发请求。</summary>
internal partial class OcrResultTextPanelWindow : Window
{
    private readonly Action _onCopyAll;
    private readonly Action _onClose;

    /// <summary>翻译按钮按下时执行实际翻译逻辑。
    /// 入参：当前应该翻译的源文本（panel 当前展示的 body）；
    /// 返回：译文。null/空 → 视为翻译失败，按钮回到原文态</summary>
    private readonly Func<string, Task<string?>>? _translateAsync;

    private string _originalText = "";
    private string? _cachedTranslation;
    private string _cachedTranslationSource = "";
    private bool _showingTranslation;
    private bool _translating;
    private DispatcherTimer? _localToastTimer;

    public OcrResultTextPanelWindow(Action onCopyAll, Action onClose, Func<string, Task<string?>>? translateAsync = null)
    {
        _onCopyAll = onCopyAll;
        _onClose = onClose;
        _translateAsync = translateAsync;
        InitializeComponent();

        HeaderBorder.MouseLeftButtonDown += OnHeaderMouseDown;
        ResizeGrip.MouseLeftButtonDown += OnResizeGripMouseDown;

        TranslateButton.Visibility = _translateAsync is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 命中标题栏右侧按钮时不要拖窗：按钮自身吃 MouseLeftButtonDown 后不会冒泡到这里，
        // 但用户在按钮边缘按下时偶尔事件会冒上来 — 这里再做一次防御性检查
        if (e.OriginalSource is System.Windows.DependencyObject d && FindAncestor<System.Windows.Controls.Button>(d) is not null)
            return;
        // 不用 Window.DragMove() —— 走 SC_MOVE 受 Aero Snap 影响，拖到屏幕上沿会弹回工作区
        WindowDragHelper.BeginDrag(this);
    }

    /// <summary>右下角 grip 按下：调用 Win32 ReleaseCapture + SendMessage(WM_NCLBUTTONDOWN, HTBOTTOMRIGHT)
    /// 让 OS 接管 resize，比 Thumb 手动改 Width/Height 流畅、自动遵守 MinWidth/MinHeight。
    /// AllowsTransparency=True 窗口的 OS resize 没有视觉边缘提示，所以我们显式在 grip 区域提供光标 / 图标</summary>
    private void OnResizeGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTBOTTOMRIGHT, IntPtr.Zero);
        e.Handled = true;
    }

    /// <summary>把"源文本"（原文）同步给面板。
    /// 设计语义：传入的总是原文 — 面板内部根据自己的翻译切换按钮决定 ContentBox 显示原文还是译文，
    /// 这样主窗工具栏切换译文 / 选区改变都不会污染面板的翻译显示状态。
    ///
    /// 行为分支：
    /// 1. 新原文 == 旧原文：保持当前显示状态（若正在显示译文则继续显示译文），仅同步缓存基线。
    /// 2. 新原文 != 旧原文：缓存失效，强制回到原文显示（用户改了选区/重新 OCR 等场景）</summary>
    public void SetContent(string text)
    {
        var newText = text ?? "";
        var changed = newText != _originalText;
        _originalText = newText;

        if (changed)
        {
            _showingTranslation = false;
            _cachedTranslation = null;
            UpdateTranslateButtonVisual();
        }

        // 选取要写入 TextBox 的字符串：译文模式且缓存命中 → 译文；否则原文（空时显示占位）
        string body;
        if (_showingTranslation && !string.IsNullOrEmpty(_cachedTranslation))
        {
            body = _cachedTranslation;
        }
        else
        {
            body = string.IsNullOrWhiteSpace(_originalText) ? "(未识别到文本)" : _originalText;
        }
        if (ContentBox.Text != body) ContentBox.Text = body;
        try { ContentBox.ScrollToHome(); } catch { /* 控件尚未挂入视觉树时 ignore */ }
    }

    /// <summary>右上角的小字元信息（"12 段 · 348 字"）</summary>
    public void SetMeta(string meta) => HeaderMeta.Text = meta ?? "";

    private void OnCopyAll(object sender, RoutedEventArgs e) => _onCopyAll();
    private void OnClose(object sender, RoutedEventArgs e) => _onClose();

    /// <summary>面板自己的轻量 toast。复制等反馈贴近面板而非飞回主结果窗，
    /// 与 OcrResultToolbarWindow.ShowLocalToast 同款思路。1.8s 后自动隐藏</summary>
    public void ShowLocalToast(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        LocalToastText.Text = text;
        LocalToastBorder.Visibility = Visibility.Visible;
        if (_localToastTimer is null)
        {
            _localToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            _localToastTimer.Tick += (_, _) =>
            {
                _localToastTimer!.Stop();
                LocalToastBorder.Visibility = Visibility.Collapsed;
            };
        }
        _localToastTimer.Stop();
        _localToastTimer.Start();
    }

    /// <summary>翻译切换按钮。状态机：
    /// 原文 → 调用翻译 → 译文（缓存）→ 原文 → 译文（直接走缓存）……
    /// 仅当 SetContent 传入的源文本变化时，缓存失效。
    /// 与主窗的 polygon 译文 overlay 完全独立 — 这里的翻译只显示在面板内</summary>
    private async void OnToggleTranslation(object sender, RoutedEventArgs e)
    {
        if (_translateAsync is null) return;
        if (_translating) return;

        if (_showingTranslation)
        {
            ContentBox.Text = _originalText;
            _showingTranslation = false;
            UpdateTranslateButtonVisual();
            try { ContentBox.ScrollToHome(); } catch { }
            return;
        }

        // 缓存命中：直接切到译文
        if (_cachedTranslation is not null && _cachedTranslationSource == _originalText)
        {
            ContentBox.Text = _cachedTranslation;
            _showingTranslation = true;
            UpdateTranslateButtonVisual();
            try { ContentBox.ScrollToHome(); } catch { }
            return;
        }

        // 走 AI 翻译
        var source = _originalText;
        if (string.IsNullOrWhiteSpace(source)) return;

        _translating = true;
        var prevTip = TranslateButton.ToolTip;
        TranslateButton.ToolTip = "翻译中…";
        TranslateButton.IsEnabled = false;
        // 在标题右侧亮出"翻译中…"。比纯改 ToolTip 直观，与 AiBubbleWindow.StatusText 同款做法
        HeaderStatus.Text = "翻译中…";
        HeaderStatus.Visibility = Visibility.Visible;
        try
        {
            var translated = await _translateAsync(source).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(translated))
            {
                HeaderStatus.Text = "翻译失败";
                return;
            }
            _cachedTranslation = translated;
            _cachedTranslationSource = source;
            ContentBox.Text = translated;
            _showingTranslation = true;
            UpdateTranslateButtonVisual();
            try { ContentBox.ScrollToHome(); } catch { }
            HeaderStatus.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 翻译失败时保持原文显示；外层调用方已通过 toast 反馈
            HeaderStatus.Text = "翻译失败";
        }
        finally
        {
            _translating = false;
            TranslateButton.IsEnabled = true;
            TranslateButton.ToolTip = prevTip;
            // 失败提示在 1.8s 后自动收起，避免长期占用 header 空间
            if (HeaderStatus.Visibility == Visibility.Visible)
            {
                _ = AutoHideHeaderStatusAsync();
            }
        }
    }

    private async Task AutoHideHeaderStatusAsync()
    {
        await Task.Delay(1800).ConfigureAwait(true);
        if (!_translating) HeaderStatus.Visibility = Visibility.Collapsed;
    }

    private void UpdateTranslateButtonVisual()
    {
        TranslateIcon.Kind = _showingTranslation
            ? PackIconMaterialDesignKind.TextFieldsRound
            : PackIconMaterialDesignKind.TranslateRound;
        TranslateButton.ToolTip = _showingTranslation
            ? "切回原文显示"
            : "翻译识别文本 · 仅在本面板显示译文";
    }

    /// <summary>面板拥有的当前显示文本。用于 host 接管"复制全部"时拿到面板真实展示的内容
    /// （可能是原文或译文，取决于翻译按钮的当前态）</summary>
    public string CurrentDisplayedText => ContentBox.Text;

    private static T? FindAncestor<T>(System.Windows.DependencyObject start) where T : System.Windows.DependencyObject
    {
        var cur = start;
        while (cur is not null)
        {
            if (cur is T t) return t;
            cur = cur is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(cur)
                : System.Windows.LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }
}
