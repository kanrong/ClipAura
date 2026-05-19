using System.Windows;
using System.Windows.Input;

namespace PopClip.App.UI;

/// <summary>OCR 结果窗的右侧 / 旁挂"识别文本面板"：把整段识别文字一次性铺开展示。
///
/// 设计动机：
/// Interactive 模式以原图作背景叠 polygon，单段选择固然精修，但用户想"通读整段"或"复制大段"时
/// 必须不断 hover/Click 不同 polygon 才能凑出全文，类似 quick 模式那种"一眼看到所有文字"的优势丢了。
/// 这个面板就是把 Quick 模式的"全文气泡"以独立可拖窗口的形式挂在结果窗旁边 —
/// 既保留 Interactive 的视觉对照，又提供 Quick 的整段阅读 / 复制。
///
/// 实现要点：
/// - 透明无边框 Window，Owner=OcrResultWindow，主窗关闭一并关闭；
/// - 顶部 HeaderBorder 承担拖动（DragMove），与 OcrResultToolbarWindow 一致；
/// - TextBox 用只读 + AcceptsReturn=true，支持选中 / Ctrl+A / Ctrl+C / 右键菜单；
/// - 状态权威在主窗，本窗仅显示：主窗通过 SetContent / SetMeta 推送数据。</summary>
internal partial class OcrResultTextPanelWindow : Window
{
    private readonly Action _onCopyAll;
    private readonly Action _onClose;

    public OcrResultTextPanelWindow(Action onCopyAll, Action onClose)
    {
        _onCopyAll = onCopyAll;
        _onClose = onClose;
        InitializeComponent();

        HeaderBorder.MouseLeftButtonDown += OnHeaderMouseDown;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* 拖动期间窗口被异常关闭时偶发抛异常，吞掉 */ }
    }

    /// <summary>把整段文本写入 TextBox。空字符串时显示占位提示，让面板不至于完全空白</summary>
    public void SetContent(string text)
    {
        ContentBox.Text = string.IsNullOrWhiteSpace(text) ? "(未识别到文本)" : text;
        try { ContentBox.ScrollToHome(); } catch { /* 控件尚未挂入视觉树时 ignore */ }
    }

    /// <summary>右上角的小字元信息（"12 段 · 348 字"）</summary>
    public void SetMeta(string meta) => HeaderMeta.Text = meta ?? "";

    private void OnCopyAll(object sender, RoutedEventArgs e) => _onCopyAll();
    private void OnClose(object sender, RoutedEventArgs e) => _onClose();
}
