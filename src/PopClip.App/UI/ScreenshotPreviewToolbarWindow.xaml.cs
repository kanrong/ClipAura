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
/// 状态文案（DimensionText）由 host 推送过来；按钮回调全部委托给 host（CommandCopy/Save/Ocr/Close）。
/// 工具条只是显示层，所有数据 / 副作用都在主窗内</summary>
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
        AnnotationBarBorder.MouseLeftButtonDown += OnBorderMouseDown;

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

    /// <summary>由 host 在隐私扫描完成 / 一键打码后调用，控制按钮可点状态</summary>
    public void SetPrivacyMosaicEnabled(bool enabled) => PrivacyMosaicButton.IsEnabled = enabled;

    private void OnPrivacyMosaicClicked(object sender, RoutedEventArgs e)
    {
        // host 持有 AppSettings 引用？这里走 host 接口而不是直接读 settings：
        // 让 host 决定 blockSize（来自 settings.PrivacyMosaicBlockSize）保持单一职责
        _host.RequestApplyPrivacyMosaic();
    }

    // ============= 标注模式 =============

    private void OnEditAnnotationClicked(object sender, RoutedEventArgs e)
    {
        // 进入 / 退出标注模式由 host 统一管理，host 会回调 SetAnnotationMode 让 UI 同步
        _host.SetAnnotationMode(!_host.AnnotationMode);
    }

    private void OnDoneClicked(object sender, RoutedEventArgs e) => _host.SetAnnotationMode(false);

    private void OnUndoClicked(object sender, RoutedEventArgs e) => _host.AnnotationStore.Undo();
    private void OnRedoClicked(object sender, RoutedEventArgs e) => _host.AnnotationStore.Redo();
    private void OnClearClicked(object sender, RoutedEventArgs e) => _host.AnnotationStore.Clear();

    private void OnToolButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var tag = b.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;
        if (Enum.TryParse<AnnotationKind>(tag, out var kind))
        {
            _host.AnnotationOptions.Kind = kind;
            SyncAnnotationOptionsUI();
        }
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

    /// <summary>由 host 在 SetAnnotationMode 切换时调用，更新 UI 显隐 + 焦点。
    /// 退出标注模式时清掉所有"当前选中"的视觉强调，避免下次进入时旧高亮残留</summary>
    public void SetAnnotationMode(bool enabled)
    {
        AnnotationBarBorder.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SyncAnnotationOptionsUI();
    }

    /// <summary>把当前 ToolOptions 同步到工具按钮的视觉强调（背景色 / Opacity）。
    /// v1 视觉简化：选中按钮 Background 改为 ToolbarAccentSoft，避免单独写 IsChecked 状态机</summary>
    private void SyncAnnotationOptionsUI()
    {
        var opts = _host.AnnotationOptions;
        SetSelected(ToolRectButton, opts.Kind is AnnotationKind.Rectangle or AnnotationKind.FilledRectangle);
        SetSelected(ToolEllipseButton, opts.Kind is AnnotationKind.Ellipse or AnnotationKind.FilledEllipse);
        SetSelected(ToolLineButton, opts.Kind == AnnotationKind.Line);
        SetSelected(ToolArrowButton, opts.Kind == AnnotationKind.Arrow);
        SetSelected(ToolFreehandButton, opts.Kind == AnnotationKind.Freehand);
        SetSelected(ToolTextButton, opts.Kind == AnnotationKind.Text);
        SetSelected(ToolMosaicButton, opts.Kind == AnnotationKind.Mosaic);
        SetSelected(ToolBlurButton, opts.Kind == AnnotationKind.Blur);
        SetSelected(ToolMaskButton, opts.Kind == AnnotationKind.Mask);

        SetSelected(ThinButton, Math.Abs(opts.StrokeThickness - 1) < 0.01);
        SetSelected(MidButton, Math.Abs(opts.StrokeThickness - 2) < 0.01);
        SetSelected(ThickButton, Math.Abs(opts.StrokeThickness - 4) < 0.01);

        SetSelected(ColorRedButton, ColorEquals(opts.Color, "#FFE53935"));
        SetSelected(ColorYellowButton, ColorEquals(opts.Color, "#FFFDD835"));
        SetSelected(ColorBlueButton, ColorEquals(opts.Color, "#FF1E88E5"));
        SetSelected(ColorGreenButton, ColorEquals(opts.Color, "#FF43A047"));
        SetSelected(ColorBlackButton, ColorEquals(opts.Color, "#FF000000"));
        SetSelected(ColorWhiteButton, ColorEquals(opts.Color, "#FFFFFFFF"));

        // Filled / 空心模式仅对矩形 / 椭圆有效，矩形按钮 ToolTip 加状态字
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
