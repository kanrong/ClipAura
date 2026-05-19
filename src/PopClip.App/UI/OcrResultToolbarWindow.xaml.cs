using System.Windows;
using System.Windows.Input;
using MahApps.Metro.IconPacks;

namespace PopClip.App.UI;

/// <summary>OCR 结果窗的"独立工具条窗口"：
/// 与主结果窗解耦，本身就是一个透明无边框 Window，可通过 DragMove 拖到屏幕任意位置（包括跨屏 / 主窗外）。
///
/// 为什么独立窗口而不是 Popup / FrameworkElement：
/// - Popup 受 Owner 屏幕边界限制，无法跨屏移动；
/// - 内嵌在主窗的 Grid 内拖动时被 Window 边界裁掉，与"拖到屏幕任意位置"诉求冲突。
///
/// 所有按钮回调都委托给 host (OcrResultWindow)。状态文案（"已选 N 段..." / "翻译中..."）也由
/// host 通过 SetStatus / SetSelectedEnabled 等方法推送过来 — 状态权威在主窗，工具条只是显示层。
///
/// 视觉重设计要点：
/// - 全图标按钮，按钮 ToolTip 显示完整含义，整体观感比纯文字按钮更紧凑、与主浮窗风格一致；
/// - 取消了左侧"⋮⋮"拖动把柄，由整个 ToolbarBorder 承接拖动 — 按钮自身命中 MouseLeftButtonDown
///   会先吃掉事件，不会触发误拖；非按钮空白区都可拖；
/// - 状态文本默认折叠，仅当 host 传入非空字符串（"已选 3 段" / "翻译中..."）才显示，
///   不再用引导性文案占位污染视觉。</summary>
internal partial class OcrResultToolbarWindow : Window
{
    private readonly OcrResultWindow _host;
    private bool _textPanelVisible;

    public OcrResultToolbarWindow(OcrResultWindow host, bool aiAvailable)
    {
        _host = host;
        InitializeComponent();
        Owner = host;

        // 整个 ToolbarBorder 承担拖动响应。按钮自己消费 MouseLeftButtonDown，
        // 不会冒泡到 Border，所以点按钮不会误触发 DragMove
        ToolbarBorder.MouseLeftButtonDown += OnBorderMouseDown;

        var v = aiAvailable ? Visibility.Visible : Visibility.Collapsed;
        TranslateAllButton.Visibility = v;
        TranslateSelectedButton.Visibility = v;
        TranslateClearButton.Visibility = v;
        TranslateSep.Visibility = v;
        // AI 未启用时显示"启用 AI"引导按钮，占据原翻译组位置 —— 把原本的引导提示文案转化为按钮，
        // 用户点击直接跳到设置 AI 页配置 Provider / API Key，比 Toast / 提示文字更直接
        EnableAiButton.Visibility = aiAvailable ? Visibility.Collapsed : Visibility.Visible;
        // 分隔条仅在按钮组实际有内容时显示，避免空分隔
        TranslateSep.Visibility = Visibility.Visible;
    }

    private void OnBorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 命中目标若是 Button 或图标本身则不拖动 —— 让按钮 Click 正常触发。
        // DragMove 必须在 MouseLeftButtonDown 同步调用，否则 WPF 抛 InvalidOperationException
        try { DragMove(); } catch { /* 拖动过程中窗口被异常关闭时偶发抛异常，吞掉不影响功能 */ }
    }

    // ============== Host 推送的状态更新 ==============

    /// <summary>更新状态文字。空字符串 / 空白 → 整个胶囊折叠让工具条更轻；
    /// 非空时显示在最左侧，紧贴按钮组。
    /// 调用方：OcrResultWindow.UpdateStatusBar / TranslateAsync 等真状态变化点</summary>
    public void SetStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "";
            StatusPill.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusText.Text = text;
            StatusPill.Visibility = Visibility.Visible;
        }
    }

    public void SetCopySelectedEnabled(bool enabled) => CopySelectedButton.IsEnabled = enabled;
    public void SetCopyAllEnabled(bool enabled) => CopyAllButton.IsEnabled = enabled;
    public void SetTranslateSelectedEnabled(bool enabled) => TranslateSelectedButton.IsEnabled = enabled;
    public void SetTranslateAllEnabled(bool enabled) => TranslateAllButton.IsEnabled = enabled;
    public void SetTranslateClearEnabled(bool enabled) => TranslateClearButton.IsEnabled = enabled;

    /// <summary>同步右侧文本面板按钮的视觉态：打开 / 关闭两种图标，方便用户从工具栏看出当前面板状态</summary>
    public void SetTextPanelVisible(bool visible)
    {
        _textPanelVisible = visible;
        ToggleTextPanelIcon.Kind = visible
            ? PackIconMaterialDesignKind.ChromeReaderModeRound
            : PackIconMaterialDesignKind.ViewSidebarRound;
        ToggleTextPanelButton.ToolTip = visible
            ? "隐藏识别文本侧边面板"
            : "显示识别文本侧边面板 · 整段文字可全选 / 复制";
    }

    // ============== 按钮事件转发 ==============

    private void OnCopySelected(object sender, RoutedEventArgs e) => _host.CommandCopySelected();
    private void OnCopyAll(object sender, RoutedEventArgs e) => _host.CommandCopyAll();
    private void OnTranslateSelected(object sender, RoutedEventArgs e) => _host.CommandTranslateSelected();
    private void OnTranslateAll(object sender, RoutedEventArgs e) => _host.CommandTranslateAll();
    private void OnTranslateClear(object sender, RoutedEventArgs e) => _host.CommandTranslateClear();
    private void OnSwitchToQuick(object sender, RoutedEventArgs e) => _host.CommandSwitchToQuick();
    private void OnCloseClicked(object sender, RoutedEventArgs e) => _host.CommandClose();
    private void OnToggleTextPanel(object sender, RoutedEventArgs e) => _host.CommandToggleTextPanel();
    private void OnOpenAiSettings(object sender, RoutedEventArgs e) => _host.CommandOpenAiSettings();
}
