using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PopClip.App.Services;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using WinRectangle = System.Drawing.Rectangle;

namespace PopClip.App.UI;

internal partial class ScreenshotPreviewWindow : Window
{
    private readonly ILog _log;
    private readonly byte[] _pngBytes;
    private readonly WinRectangle _anchorPhysical;
    private readonly uint _anchorDpiX;
    private readonly uint _anchorDpiY;
    private readonly ClipboardWriter _clipboard;
    private readonly Action _onOcrRequested;
    private readonly DispatcherTimer _toastTimer;

    public ScreenshotPreviewWindow(
        ILog log,
        byte[] pngBytes,
        WinRectangle anchorPhysical,
        uint anchorDpiX,
        uint anchorDpiY,
        ClipboardWriter clipboard,
        Action onOcrRequested)
    {
        _log = log;
        _pngBytes = pngBytes;
        _anchorPhysical = anchorPhysical;
        _anchorDpiX = anchorDpiX == 0 ? 96 : anchorDpiX;
        _anchorDpiY = anchorDpiY == 0 ? 96 : anchorDpiY;
        _clipboard = clipboard;
        _onOcrRequested = onOcrRequested;

        InitializeComponent();

        Left = -32000;
        Top = -32000;
        var dipScaleX = _anchorDpiX / 96.0;
        var dipScaleY = _anchorDpiY / 96.0;
        Width = Math.Max(160, _anchorPhysical.Width / dipScaleX);
        Height = Math.Max(96, _anchorPhysical.Height / dipScaleY);
        PreviewImage.Source = LoadBitmap(_pngBytes);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

        Loaded += OnLoaded;
        Closed += (_, _) => _toastTimer.Stop();
        KeyDown += OnKeyDown;
        Frame.MouseLeftButtonDown += OnFrameMouseLeftButtonDown;
    }

    private static BitmapImage LoadBitmap(byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            _anchorPhysical.Left,
            _anchorPhysical.Top,
            Math.Max(1, _anchorPhysical.Width),
            Math.Max(1, _anchorPhysical.Height),
            NativeMethods.SWP_NOACTIVATE);
    }

    private void OnFrameMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _clipboard.SetImagePngBytes(_pngBytes);
            ShowToast("截图已复制");
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot copy failed", ("err", ex.Message));
            ShowToast("复制失败：" + ex.Message);
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"ClipAura-Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true,
            DefaultExt = ".png",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _pngBytes);
            ShowToast("截图已保存");
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot save failed", ("err", ex.Message));
            ShowToast("保存失败：" + ex.Message);
        }
    }

    private void OnOcrClicked(object sender, RoutedEventArgs e)
    {
        Close();
        try { _onOcrRequested(); }
        catch (Exception ex) { _log.Warn("screenshot ocr request failed", ("err", ex.Message)); }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
