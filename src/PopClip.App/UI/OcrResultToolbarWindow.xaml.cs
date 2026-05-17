using System.Windows;
using System.Windows.Input;

namespace PopClip.App.UI;

/// <summary>OCR 结果窗的"独立工具条窗口"：
/// 与主结果窗解耦，本身就是一个透明无边框 Window，可通过 DragMove 拖到屏幕任意位置（包括跨屏 / 主窗外）。
///
/// 为什么独立窗口而不是 Popup / FrameworkElement：
/// - Popup 受 Owner 屏幕边界限制，无法跨屏移动；
/// - 内嵌在主窗的 Grid 内拖动时被 Window 边界裁掉，与"拖到屏幕任意位置"诉求冲突。
///
/// 所有按钮回调都委托给 host (OcrResultWindow)。状态文案（"已选 N 段..." / "翻译中..." / "已识别"）也由
/// host 通过 SetStatus / SetSelectedEnabled 等方法推送过来 — 状态权威在主窗，工具条只是显示层。
/// 关闭语义：工具条 Closed 不影响 host；host 关闭时一并 Close()</summary>
internal partial class OcrResultToolbarWindow : Window
{
    private readonly OcrResultWindow _host;

    public OcrResultToolbarWindow(OcrResultWindow host, bool aiAvailable)
    {
        _host = host;
        InitializeComponent();
        Owner = host;

        // grip 区域响应拖动：MouseLeftButtonDown 立即 DragMove。
        // 不在按钮区域响应：按钮控件自身吃掉 MouseLeftButtonDown，事件不会冒到 Border 上
        ToolbarGrip.MouseLeftButtonDown += OnGripMouseDown;

        var v = aiAvailable ? Visibility.Visible : Visibility.Collapsed;
        TranslateAllButton.Visibility = v;
        TranslateSelectedButton.Visibility = v;
        TranslateClearButton.Visibility = v;
        TranslateSep.Visibility = v;
    }

    private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // DragMove 必须在 MouseLeftButtonDown 事件中同步调用，否则 WPF 抛 InvalidOperationException
        try { DragMove(); } catch { /* 拖动过程中窗口被异常关闭时偶发抛异常，吞掉不影响功能 */ }
    }

    // ============== Host 推送的状态更新 ==============

    /// <summary>更新状态文字（"已选 N 段..." / "未识别到文本" / "翻译中..."）。
    /// 主窗 UpdateStatusBar 调用本方法保持工具条与主窗一致</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    public void SetCopySelectedEnabled(bool enabled) => CopySelectedButton.IsEnabled = enabled;
    public void SetCopyAllEnabled(bool enabled) => CopyAllButton.IsEnabled = enabled;
    public void SetOrganizeEnabled(bool enabled) => OrganizeParagraphsButton.IsEnabled = enabled;
    public void SetTranslateSelectedEnabled(bool enabled) => TranslateSelectedButton.IsEnabled = enabled;
    public void SetTranslateAllEnabled(bool enabled) => TranslateAllButton.IsEnabled = enabled;
    public void SetTranslateClearEnabled(bool enabled) => TranslateClearButton.IsEnabled = enabled;

    // ============== 按钮事件转发 ==============

    private void OnCopySelected(object sender, RoutedEventArgs e) => _host.CommandCopySelected();
    private void OnCopyAll(object sender, RoutedEventArgs e) => _host.CommandCopyAll();
    private void OnOrganizeParagraphs(object sender, RoutedEventArgs e) => _host.CommandOrganizeParagraphs();
    private void OnTranslateSelected(object sender, RoutedEventArgs e) => _host.CommandTranslateSelected();
    private void OnTranslateAll(object sender, RoutedEventArgs e) => _host.CommandTranslateAll();
    private void OnTranslateClear(object sender, RoutedEventArgs e) => _host.CommandTranslateClear();
    private void OnSwitchToQuick(object sender, RoutedEventArgs e) => _host.CommandSwitchToQuick();
    private void OnCloseClicked(object sender, RoutedEventArgs e) => _host.CommandClose();
}
