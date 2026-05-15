using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using PopClip.Core.Logging;
using PopClip.Core.Model;

namespace PopClip.Uia;

/// <summary>从前台焦点元素获取选中文本与边界矩形。
/// 优先 TextPattern.GetSelection → ValuePattern.Value → LegacyIAccessible.SelectedText。
/// 全部失败返回 null，由上层走剪贴板兜底。</summary>
public sealed class UiaTextAcquirer
{
    private readonly ILog _log;
    private const int MaxTextLength = 200_000;

    public UiaTextAcquirer(ILog log) => _log = log;

    public bool LastFocusedElementWasPassword { get; private set; }
    public string LastFocusedControlTypeName { get; private set; } = "";
    public bool LastFocusedElementRejectsClipboardFallbackOnDoubleClick { get; private set; }
    public bool LastFocusedElementRejectsClipboardFallbackOnDrag { get; private set; }

    public AcquisitionResult? TryAcquire()
    {
        LastFocusedElementWasPassword = false;
        LastFocusedControlTypeName = "";
        LastFocusedElementRejectsClipboardFallbackOnDoubleClick = false;
        LastFocusedElementRejectsClipboardFallbackOnDrag = false;
        AutomationElement? focused = null;
        try
        {
            focused = AutomationElement.FocusedElement;
        }
        catch (Exception ex)
        {
            _log.Warn("UIA FocusedElement threw", ("err", ex.Message));
            return null;
        }
        if (focused is null) return null;
        LastFocusedControlTypeName = SafeControlTypeName(focused);
        LastFocusedElementRejectsClipboardFallbackOnDoubleClick = RejectsClipboardFallbackOnDoubleClick(focused);
        LastFocusedElementRejectsClipboardFallbackOnDrag = RejectsClipboardFallbackOnDrag(focused);
        LastFocusedElementWasPassword = IsPasswordElement(focused);
        if (LastFocusedElementWasPassword)
        {
            _log.Info("acquisition skipped: focused element is password");
            return null;
        }

        // MVP 阶段仅启用 TextPattern。LegacyIAccessiblePattern 仅在 COM 客户端可用，
        // 后续如需扩展可在此分支后追加 COM Interop 路径
        if (TryTextPattern(focused, out var r1) && r1 is not null) return r1;
        return null;
    }

    private bool TryTextPattern(AutomationElement element, out AcquisitionResult? result)
    {
        result = null;
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var raw)
                || raw is not TextPattern textPattern)
            {
                return false;
            }

            TextPatternRange[] ranges;
            try { ranges = textPattern.GetSelection(); }
            catch (InvalidOperationException) { return false; }

            if (ranges.Length == 0) return false;

            var primary = ranges[0];
            var text = primary.GetText(MaxTextLength) ?? "";
            if (string.IsNullOrEmpty(text)) return false;

            var rect = ComputeBoundingRect(primary);
            var editable = IsEditable(element);
            result = new AcquisitionResult(text, rect, AcquisitionSource.UiaTextPattern, editable, element);
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug("TextPattern fetch failed", ("err", ex.Message));
            return false;
        }
    }

    private static SelectionRect ComputeBoundingRect(TextPatternRange range)
    {
        var rects = range.GetBoundingRectangles();
        if (rects.Length == 0)
        {
            return new SelectionRect(0, 0, 0, 0);
        }
        // 选中区域的"右下角矩形"最适合作为工具栏锚点（多行选择时）
        var last = rects[rects.Length - 1];
        return ToSelectionRect(last);
    }

    private static SelectionRect ToSelectionRect(System.Windows.Rect r)
    {
        return new SelectionRect(
            (int)Math.Round(r.Left),
            (int)Math.Round(r.Top),
            (int)Math.Round(r.Right),
            (int)Math.Round(r.Bottom));
    }

    private static bool IsEditable(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var v)
                && v is ValuePattern vp)
            {
                return !vp.Current.IsReadOnly;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool IsPasswordElement(AutomationElement element)
    {
        try
        {
            if (element.Current.IsPassword) return true;
            return element.Current.ControlType == ControlType.Edit && element.Current.IsPassword;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeControlTypeName(AutomationElement element)
    {
        try { return element.Current.ControlType?.ProgrammaticName ?? ""; }
        catch { return ""; }
    }

    private static bool RejectsClipboardFallbackOnDoubleClick(AutomationElement element)
    {
        try
        {
            var ct = element.Current.ControlType;
            return ct == ControlType.Menu
                || ct == ControlType.MenuBar
                || ct == ControlType.MenuItem
                || ct == ControlType.Tree
                || ct == ControlType.TreeItem
                || ct == ControlType.List
                || ct == ControlType.ListItem
                || ct == ControlType.Tab
                || ct == ControlType.TabItem
                || ct == ControlType.ToolBar
                || ct == ControlType.Button
                || ct == ControlType.CheckBox
                || ct == ControlType.RadioButton
                || ct == ControlType.ComboBox;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>拖动这些控件 = OLE 拖放（拖文件、拖节点、拖标签页），不可能是文本选区。
    /// 比双击列表更克制：仅包含真正会被用户拖动的控件，避免把"拖按钮"这种几乎不存在的边界场景一刀切</summary>
    private static bool RejectsClipboardFallbackOnDrag(AutomationElement element)
    {
        try
        {
            var ct = element.Current.ControlType;
            return ct == ControlType.ListItem
                || ct == ControlType.TreeItem
                || ct == ControlType.TabItem
                || ct == ControlType.List
                || ct == ControlType.Tree;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>UIA 获取成功的载荷，封装供 SessionManager 组装 SelectionContext 使用</summary>
public sealed record AcquisitionResult(
    string Text,
    SelectionRect Rect,
    AcquisitionSource Source,
    bool IsEditable,
    AutomationElement Element);
