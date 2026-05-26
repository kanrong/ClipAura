using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PopClip.App.UI.Annotation;

namespace PopClip.App.UI;

/// <summary>截图预览的"独立工具条窗口"：与主预览窗解耦，本身就是一个透明无边框 Window，
/// 可通过 DragMove 拖到屏幕任意位置（包括跨屏 / 主窗外）。
///
/// 为什么独立窗口而不是嵌在主窗 Grid 内：
/// - 内嵌时窄高截图（如侧栏 / 长条 OCR 区域）会让工具条宽度反推 Grid 列宽，
///   把 Frame Border 撑得比截图大，视觉错位；
/// - 内嵌的 Overlay 模式遮挡截图原内容；外接 Top/Bottom 又会被截图边界裁切。
/// 独立 Window 一次性解决撑图 / 跨屏 / 拖动诉求，与 OcrResultToolbarWindow 走同一套路径。
///
/// 跟随预览框拖动：当预览主窗 DragMove 时，host 调用 SyncPositionToHost 让工具条按相对偏移
/// 同步移动，避免出现"图被拖远了，工具条还停在原地"的视觉断层。
///
/// 状态文案（DimensionText）由 host 推送过来；按钮回调全部委托给 host（CommandCopy/Save/Ocr/Close）。
/// 工具条只是显示层，所有数据 / 副作用都在主窗内。
///
/// 标注语义 v2（与 v1 区别）：
/// - 高频绘制工具（矩形 / 椭圆 / 箭头 / 画笔 / 文字 / 马赛克 / 实心遮挡）放在主行第一行，
///   点任一按钮即自动进入标注模式；
/// - 直线收纳到第二行高级面板（使用频次低）；
/// - "编辑"按钮的语义为"展开高级选项"（直线 / 颜色 / 粗细 / Undo / Redo / 清空 / 完成），
///   与"标注模式是否激活"解耦 —— 标注模式由点击工具按钮自动进入，由"完成 / ESC"退出</summary>
internal partial class ScreenshotPreviewToolbarWindow : Window
{
    private readonly ScreenshotPreviewWindow _host;
    private DispatcherTimer? _localToastTimer;

    public ScreenshotPreviewToolbarWindow(ScreenshotPreviewWindow host, string dimensionText)
    {
        _host = host;
        InitializeComponent();
        Owner = host;
        DimensionText.Text = dimensionText;

        // 整个 ToolbarBorder 都是拖动响应区：按钮自己消费 MouseLeftButtonDown，
        // 不会冒泡到 Border，所以点按钮不会误触发拖动。
        // 不用 Window.DragMove() —— 它走 SC_MOVE，受 Aero Snap 影响把工具条弹回工作区
        ToolbarBorder.MouseLeftButtonDown += OnBorderMouseDown;

        // 标注工具按钮的双击切换"实心 / 空心"：当前简化为单击切换，矩形 / 椭圆按钮的语义额外支持"长按 / 双击"
        ToolRectButton.MouseDoubleClick += (_, _) => ToggleFilledMode();
        ToolEllipseButton.MouseDoubleClick += (_, _) => ToggleFilledMode();

        SyncAnnotationOptionsUI();
    }

    private void ToggleFilledMode()
    {
        var opts = _host.AnnotationOptions;
        opts.Filled = !opts.Filled;
        SyncAnnotationOptionsUI();
    }

    private void OnBorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        WindowDragHelper.BeginDrag(this);
    }

    /// <summary>工具条自己的轻量 toast：用于复制 / 保存等动作的反馈，
    /// 让消息出现在按钮正下方而不是飘到主窗中央。1.8 秒后自动隐藏</summary>
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

    private void OnCopyClicked(object sender, RoutedEventArgs e) => _host.CommandCopy(ShowLocalToast);
    private void OnSaveClicked(object sender, RoutedEventArgs e) => _host.CommandSave(ShowLocalToast);
    private void OnOcrClicked(object sender, RoutedEventArgs e) => _host.CommandOcr();
    private void OnPinClicked(object sender, RoutedEventArgs e) => _host.CommandPin();
    private void OnReshootClicked(object sender, RoutedEventArgs e) => _host.CommandReshoot();
    private void OnCloseClicked(object sender, RoutedEventArgs e) => _host.CommandClose();

    /// <summary>由 host 调用：当 Coordinator 没注入 onReshootRequested 回调时（兜底场景），
    /// 把按钮置灰避免点击无反应</summary>
    public void SetReshootEnabled(bool enabled) => ReshootButton.IsEnabled = enabled;

    /// <summary>由 host 调用：当 Coordinator 没注入 PinnedScreenshotManager 时把按钮置灰</summary>
    public void SetPinEnabled(bool enabled) => PinButton.IsEnabled = enabled;

    // ============= 标注模式 =============

    /// <summary>"编辑"按钮：仅 toggle 第二行高级面板（颜色 / 粗细 / Undo / Redo / 清空 / 完成）的可见性。
    /// 不影响标注模式 —— 用户点任意绘制工具按钮就能直接画</summary>
    private void OnEditAnnotationClicked(object sender, RoutedEventArgs e)
    {
        bool willShow = AdvancedRow.Visibility != Visibility.Visible;
        AdvancedRow.Visibility = willShow ? Visibility.Visible : Visibility.Collapsed;
        SyncAnnotationOptionsUI();
    }

    /// <summary>"完成"按钮：取消当前工具激活（退出标注模式），同时把高级面板收回去</summary>
    private void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        _host.SetAnnotationMode(false);
        AdvancedRow.Visibility = Visibility.Collapsed;
        SyncAnnotationOptionsUI();
    }

    private void OnUndoClicked(object sender, RoutedEventArgs e) => _host.AnnotationStore.Undo();
    private void OnRedoClicked(object sender, RoutedEventArgs e) => _host.AnnotationStore.Redo();
    private void OnClearClicked(object sender, RoutedEventArgs e) => _host.AnnotationStore.Clear();

    /// <summary>绘制工具按钮（矩形 / 椭圆 / 直线 / 箭头 / 涂鸦 / 文本 / 马赛克 / 模糊 / 遮挡）。
    /// 点任意一个都会自动进入标注模式（让 AnnotationCanvas 拦截输入并响应绘制）；
    /// 同一个按钮再点一次：退出标注模式，回到可拖窗状态</summary>
    private void OnToolButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var tag = b.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;
        if (!Enum.TryParse<AnnotationKind>(tag, out var kind)) return;

        var opts = _host.AnnotationOptions;
        bool sameTool = _host.AnnotationMode
            && (opts.Kind == kind
                || (kind == AnnotationKind.Rectangle && opts.Kind == AnnotationKind.FilledRectangle)
                || (kind == AnnotationKind.Ellipse && opts.Kind == AnnotationKind.FilledEllipse));
        if (sameTool)
        {
            // 再点一次相同工具 → 退出标注模式
            _host.SetAnnotationMode(false);
        }
        else
        {
            opts.Kind = kind;
            _host.SetAnnotationMode(true);
        }
        SyncAnnotationOptionsUI();
    }

    private void OnColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (b.Tag is not string hex) return;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            _host.AnnotationOptions.Color = c;
            SyncAnnotationOptionsUI();
        }
        catch { }
    }

    private void OnThicknessClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (b.Tag is not string s) return;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            _host.AnnotationOptions.StrokeThickness = v;
            SyncAnnotationOptionsUI();
        }
    }

    /// <summary>由 host 在 SetAnnotationMode 切换时调用，仅刷新视觉强调。
    /// 注意：v2 起本方法不再控制"高级面板"可见性 —— 高级面板由"编辑"按钮独立 toggle</summary>
    public void SetAnnotationMode(bool enabled)
    {
        SyncAnnotationOptionsUI();
    }

    /// <summary>把当前 ToolOptions + 标注模式状态同步到工具按钮 / 颜色 / 粗细按钮的视觉强调。
    /// 工具按钮的"选中"只有"标注模式开启 + Kind 匹配"才显示，避免没激活时还残留高亮</summary>
    private void SyncAnnotationOptionsUI()
    {
        var opts = _host.AnnotationOptions;
        bool mode = _host.AnnotationMode;

        SetSelected(ToolRectButton, mode && opts.Kind is AnnotationKind.Rectangle or AnnotationKind.FilledRectangle);
        SetSelected(ToolEllipseButton, mode && opts.Kind is AnnotationKind.Ellipse or AnnotationKind.FilledEllipse);
        SetSelected(ToolLineButton, mode && opts.Kind == AnnotationKind.Line);
        SetSelected(ToolArrowButton, mode && opts.Kind == AnnotationKind.Arrow);
        SetSelected(ToolFreehandButton, mode && opts.Kind == AnnotationKind.Freehand);
        SetSelected(ToolTextButton, mode && opts.Kind == AnnotationKind.Text);
        SetSelected(ToolMosaicButton, mode && opts.Kind == AnnotationKind.Mosaic);
        SetSelected(ToolMaskButton, mode && opts.Kind == AnnotationKind.Mask);

        // "编辑"按钮：展开时给它一个 selected 视觉，与第一行视觉规则一致
        SetSelected(EditAnnotationButton, AdvancedRow.Visibility == Visibility.Visible);

        SetSelected(ThinButton, Math.Abs(opts.StrokeThickness - 1) < 0.01);
        SetSelected(MidButton, Math.Abs(opts.StrokeThickness - 2) < 0.01);
        SetSelected(ThickButton, Math.Abs(opts.StrokeThickness - 4) < 0.01);

        SetSelected(ColorRedButton, ColorEquals(opts.Color, "#FFE53935"));
        SetSelected(ColorYellowButton, ColorEquals(opts.Color, "#FFFDD835"));
        SetSelected(ColorBlueButton, ColorEquals(opts.Color, "#FF1E88E5"));
        SetSelected(ColorGreenButton, ColorEquals(opts.Color, "#FF43A047"));
        SetSelected(ColorBlackButton, ColorEquals(opts.Color, "#FF000000"));
        SetSelected(ColorWhiteButton, ColorEquals(opts.Color, "#FFFFFFFF"));

        ToolRectButton.ToolTip = "矩形（双击切换 " + (opts.Filled ? "实心" : "空心") + "）";
        ToolEllipseButton.ToolTip = "椭圆（双击切换 " + (opts.Filled ? "实心" : "空心") + "）";
    }

    private static void SetSelected(Button b, bool selected)
    {
        if (selected)
        {
            b.SetResourceReference(Button.BackgroundProperty, "ToolbarAccentSoft");
        }
        else
        {
            b.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private static bool ColorEquals(Color c, string hex)
    {
        try
        {
            var t = (Color)ColorConverter.ConvertFromString(hex);
            return t.A == c.A && t.R == c.R && t.G == c.G && t.B == c.B;
        }
        catch { return false; }
    }
}
