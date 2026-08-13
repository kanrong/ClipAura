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

    private string JoinSelectedOriginalText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            if (!_selected[i]) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(_result.Blocks[i].Text);
        }
        return sb.ToString();
    }

    private void BuildPolygons()
    {
        OverlayCanvas.Children.Clear();
        _polygons.Clear();
        if (_result.SourceWidth <= 0 || _result.SourceHeight <= 0) return;

        var (innerW, innerH) = GetInnerSize();
        double sx = innerW / _result.SourceWidth;
        double sy = innerH / _result.SourceHeight;

        var fill = (Brush?)TryFindResource("ToolbarAccentSoft") ?? Brushes.SteelBlue;

        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            var b = _result.Blocks[i];
            var polygon = new Polygon
            {
                Points = new PointCollection
                {
                    new WpfPoint(b.Box.X1 * sx, b.Box.Y1 * sy),
                    new WpfPoint(b.Box.X2 * sx, b.Box.Y2 * sy),
                    new WpfPoint(b.Box.X3 * sx, b.Box.Y3 * sy),
                    new WpfPoint(b.Box.X4 * sx, b.Box.Y4 * sy),
                },
                Fill = fill,
                // 默认态比旧的 0.18 略加深，让"哪些行被识别成 block"一眼看清；
                // 与 Hover (0.45) / Selected (0.7) 的对比仍足够明显，不会与 hover 视觉混淆
                Opacity = 0.32,
                Stroke = Brushes.Transparent,
                StrokeThickness = 1,
                Cursor = Cursors.IBeam,
                Tag = i,
                ToolTip = b.Text.Length > 80 ? b.Text[..80] + "…" : b.Text,
            };
            polygon.MouseEnter += OnPolygonMouseEnter;
            polygon.MouseLeave += OnPolygonMouseLeave;
            polygon.MouseLeftButtonDown += OnPolygonClick;
            OverlayCanvas.Children.Add(polygon);
            _polygons.Add(polygon);
        }
    }

    private enum BoxState { Default, Hover, Selected }

    private void SetState(int index, BoxState state)
    {
        if (index < 0 || index >= _polygons.Count) return;
        var p = _polygons[index];
        switch (state)
        {
            case BoxState.Default:
                p.Opacity = 0.32;
                p.Stroke = Brushes.Transparent;
                p.StrokeThickness = 1;
                break;
            case BoxState.Hover:
                p.Opacity = 0.50;
                p.Stroke = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White;
                p.StrokeThickness = 1;
                break;
            case BoxState.Selected:
                p.Opacity = 0.72;
                p.Stroke = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White;
                p.StrokeThickness = 2;
                break;
        }
    }

    private void RefreshAllStates()
    {
        for (int i = 0; i < _polygons.Count; i++)
        {
            if (_selected[i]) SetState(i, BoxState.Selected);
            else if (i == _hoverIndex) SetState(i, BoxState.Hover);
            else SetState(i, BoxState.Default);
        }
    }

    // ============== Polygon 事件 ==============

    private void OnPolygonMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Polygon p || p.Tag is not int idx) return;
        if (_selected[idx]) return;
        _hoverIndex = idx;
        SetState(idx, BoxState.Hover);
    }

    private void OnPolygonMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Polygon p || p.Tag is not int idx) return;
        if (_hoverIndex == idx) _hoverIndex = -1;
        if (!_selected[idx]) SetState(idx, BoxState.Default);
    }

    private void OnPolygonClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Polygon p || p.Tag is not int idx) return;
        e.Handled = true;

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            ClearSelection();
            _selected[idx] = true;
            RefreshAllStates();
            // 复制时不再弹中央 toast：选中态 polygon 高亮 + 工具栏状态胶囊
            // 已足够表达"复制成功"，再叠加 toast 反而遮挡内容，分散注意力
            CopyTextSilently(_result.Blocks[idx].Text);
        }
        else
        {
            _selected[idx] = !_selected[idx];
            RefreshAllStates();
        }
        UpdateStatusBar();
    }

    // ============== Marquee 框选 ==============

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        // loading 态下不接受任何 marquee 操作：还没有 polygon 可以选中，
        // 启动 marquee 也会让"识别中…"画面上突然冒出一个虚框，体验混乱
        if (_isLoading) return;

        _marqueeStart = e.GetPosition(OverlayCanvas);
        _isDragging = false;
        OverlayCanvas.CaptureMouse();

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            ClearSelection();
            RefreshAllStates();
            UpdateStatusBar();
        }
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeStart is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelMarquee(); return; }

        var cur = e.GetPosition(OverlayCanvas);
        var dx = cur.X - _marqueeStart.Value.X;
        var dy = cur.Y - _marqueeStart.Value.Y;

        if (!_isDragging && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _isDragging = true;

        double l = Math.Min(_marqueeStart.Value.X, cur.X);
        double t = Math.Min(_marqueeStart.Value.Y, cur.Y);
        double w = Math.Abs(dx);
        double h = Math.Abs(dy);
        Canvas.SetLeft(MarqueeRect, l);
        Canvas.SetTop(MarqueeRect, t);
        MarqueeRect.Width = w;
        MarqueeRect.Height = h;
        MarqueeRect.Visibility = Visibility.Visible;

        var rect = new Rect(l, t, w, h);
        bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        UpdateSelectionByMarquee(rect, additive);
    }

    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeStart is null) return;
        OverlayCanvas.ReleaseMouseCapture();
        _marqueeStart = null;
        _isDragging = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        // 框选完成不弹中央 toast：选中 polygon 自身已变深，
        // 工具栏状态胶囊也会同步显示"已选 X/Y"，无需再叠 toast 干扰阅读
        UpdateStatusBar();
    }

    private void OnOverlayMouseLeave(object sender, MouseEventArgs e)
    {
        if (_marqueeStart is not null && e.LeftButton != MouseButtonState.Pressed)
        {
            CancelMarquee();
        }
    }

    private void CancelMarquee()
    {
        _marqueeStart = null;
        _isDragging = false;
        try { OverlayCanvas.ReleaseMouseCapture(); } catch { }
        MarqueeRect.Visibility = Visibility.Collapsed;
    }

    private void UpdateSelectionByMarquee(Rect marquee, bool additive)
    {
        var snapshot = additive ? (bool[])_selected.Clone() : new bool[_polygons.Count];
        for (int i = 0; i < _polygons.Count; i++)
        {
            var bounds = _polygons[i].RenderedGeometry.Bounds;
            if (bounds.IntersectsWith(marquee))
            {
                snapshot[i] = true;
            }
        }
        _selected = snapshot;
        RefreshAllStates();
    }

    // ============== 键盘 ==============

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Escape 在 loading 态也必须有效（用户取消识别），其他快捷键依赖已识别结果故 loading 时屏蔽
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSelf("escape");
            return;
        }
        if (_isLoading) return;

        switch (e.Key)
        {
            case Key.A when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true;
                SelectAll();
                break;
            case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true;
                if (SelectedCount() > 0) CommandCopySelected();
                else CommandCopyAll();
                break;
            case Key.D when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true;
                ClearSelection();
                RefreshAllStates();
                UpdateStatusBar();
                break;
        }
    }
    private void OnSelectAllMenu(object sender, RoutedEventArgs e) => SelectAll();

    // ============== 选区辅助 ==============

    private void SelectAll()
    {
        if (_polygons.Count == 0) return;
        for (int i = 0; i < _selected.Length; i++) _selected[i] = true;
        RefreshAllStates();
        UpdateStatusBar();
        ShowToast($"已全选 {_polygons.Count} 段（Ctrl+C 复制）");
    }

    private void ClearSelection()
    {
        for (int i = 0; i < _selected.Length; i++) _selected[i] = false;
    }

    private int SelectedCount()
    {
        int n = 0;
        for (int i = 0; i < _selected.Length; i++) if (_selected[i]) n++;
        return n;
    }

    /// <summary>拼接选中 block 的文本，优先取译文（如果有 overlay），否则原文 —
    /// 这样"先翻译再复制选中"能自然拿到译文内容</summary>
    private string JoinSelectedText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            if (!_selected[i]) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(GetEffectiveText(i));
        }
        return sb.ToString();
    }

    private string JoinAllText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(GetEffectiveText(i));
        }
        return sb.ToString();
    }

    private string EffectiveFullText()
        => OriginalFullText();

    private string OriginalFullText()
    {
        if (!string.IsNullOrWhiteSpace(_layoutFullText)) return _layoutFullText;
        var text = _result.FullText;
        return string.IsNullOrWhiteSpace(text) ? JoinAllOriginalText() : text.Trim();
    }

    private string JoinAllOriginalText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(_result.Blocks[i].Text);
        }
        return sb.ToString();
    }

    private string GetEffectiveText(int i)
    {
        if (_translationOverlays[i]?.Child is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            return tb.Text;
        return _result.Blocks[i].Text;
    }
}
