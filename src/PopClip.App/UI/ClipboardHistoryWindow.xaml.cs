using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PopClip.App.Services;
using PopClip.Core.Actions;
using PopClip.Core.Model;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using PopClip.Uia.Clipboard;

namespace PopClip.App.UI;

/// <summary>剪贴板历史选择器，贴在光标旁。
///   Enter            → 粘贴到目标窗口并关闭
///   Shift+Enter      → 粘贴后窗口留下，方便连贴
///   Ctrl+Enter       → 只写入系统剪贴板
///   1-9              → 直选对应条目（搜索框有字时不拦截，留给查询）
///   Ctrl+Tab         → 文本 / 图片
///   Del / P          → 删除 / 钉选
/// 点到窗口外会关闭。</summary>
internal partial class ClipboardHistoryWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ClipboardHistoryService _history;
    private readonly IClipboardWriter _writer;
    private readonly ClipboardAccess _clipboardAccess;
    private readonly ITextReplacer? _replacer;
    private readonly SelectionContext? _anchorContext;
    private readonly ClipboardPaste? _paste;
    private readonly Func<nint>? _pasteTargetProvider;
    private bool _ignoreDeactivate;
    private bool _closing;

    public ObservableCollection<HistoryRow> Rows { get; } = new();
    public ObservableCollection<ImageHistoryRow> ImageRows { get; } = new();

    public ClipboardHistoryWindow(
        ClipboardHistoryService history,
        IClipboardWriter writer,
        ITextReplacer? replacer,
        SelectionContext? anchorContext,
        ClipboardPaste? paste,
        ClipboardAccess clipboardAccess,
        Func<nint>? pasteTargetProvider = null)
    {
        _history = history;
        _writer = writer;
        _replacer = replacer;
        _anchorContext = anchorContext;
        _paste = paste;
        _clipboardAccess = clipboardAccess;
        _pasteTargetProvider = pasteTargetProvider;
        InitializeComponent();
        HistoryList.ItemsSource = Rows;
        ImageList.ItemsSource = ImageRows;
        _history.Mutated += OnHistoryMutated;
        Deactivated += OnDeactivated;
        Closed += (_, _) => _history.Mutated -= OnHistoryMutated;
        Loaded += (_, _) =>
        {
            Refresh();
            RepositionToCursor();
            FocusSearch();
        };
    }

    private bool IsImageTab => TabImageRadio?.IsChecked == true;

    public void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    /// <summary>按光标所在屏放置，优先右下；放不下则翻到左上，并夹进工作区。</summary>
    public void RepositionToCursor()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
        {
            pt = new NativeMethods.POINT { X = 0, Y = 0 };
        }

        var monitor = MonitorQuery.FromPoint(pt.X, pt.Y);
        var dipW = ActualWidth > 1 ? ActualWidth : Width;
        var dipH = ActualHeight > 1 ? ActualHeight : Height;
        var wPx = Math.Max(1, (int)Math.Round(dipW * monitor.DpiX / 96.0));
        var hPx = Math.Max(1, (int)Math.Round(dipH * monitor.DpiY / 96.0));
        const int gap = 16;
        var x = pt.X + gap;
        var y = pt.Y + gap;
        if (x + wPx > monitor.WorkRight) x = pt.X - wPx - gap;
        if (y + hPx > monitor.WorkBottom) y = pt.Y - hPx - gap;
        x = Math.Clamp(x, monitor.WorkLeft, Math.Max(monitor.WorkLeft, monitor.WorkRight - wPx));
        y = Math.Clamp(y, monitor.WorkTop, Math.Max(monitor.WorkTop, monitor.WorkBottom - hPx));

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            x, y, wPx, hPx,
            NativeMethods.SWP_SHOWWINDOW);
    }

    private void OnHistoryMutated()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        Refresh();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_ignoreDeactivate || _closing) return;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void Refresh()
    {
        if (IsImageTab) RefreshImages();
        else RefreshTexts();
    }

    private void RefreshTexts()
    {
        var query = SearchBox.Text?.Trim();
        Rows.Clear();
        var i = 0;
        foreach (var entry in _history.List(120, query))
        {
            Rows.Add(HistoryRow.From(entry, i < 9 ? (i + 1).ToString() : ""));
            i++;
        }
        if (Rows.Count > 0) HistoryList.SelectedIndex = 0;
    }

    private void RefreshImages()
    {
        ImageRows.Clear();
        var query = SearchBox.Text?.Trim();
        var i = 0;
        foreach (var entry in _history.ListImages(60, includeBlob: true, query))
        {
            ImageRows.Add(ImageHistoryRow.From(entry, i < 9 ? (i + 1).ToString() : ""));
            i++;
        }
        if (ImageRows.Count > 0) ImageList.SelectedIndex = 0;
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (HistoryList is null || ImageList is null) return;
        var img = IsImageTab;
        HistoryList.Visibility = img ? Visibility.Collapsed : Visibility.Visible;
        ImageList.Visibility = img ? Visibility.Visible : Visibility.Collapsed;
        if (SearchBox is not null)
        {
            SearchBox.PlaceholderText = img
                ? "按 OCR 文本搜索 · Esc 关闭"
                : "搜索 · 1-9 直选 · Esc 关闭";
        }
        Refresh();
    }

    private void ToggleTab()
    {
        if (TabImageRadio is null || TabTextRadio is null) return;
        if (IsImageTab) TabTextRadio.IsChecked = true;
        else TabImageRadio.IsChecked = true;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ToggleTab();
            return;
        }

        if (TryDigitIndex(e.Key, out var index)
            && (Keyboard.Modifiers & ~(ModifierKeys.Shift | ModifierKeys.Control)) == 0)
        {
            var typingQuery = SearchBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(SearchBox.Text);
            if (!typingQuery)
            {
                e.Handled = true;
                PickIndex(index);
            }
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            e.Handled = true;
            FocusActiveList();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmSelected(Keyboard.Modifiers);
        }
    }

    private void FocusActiveList()
    {
        if (IsImageTab)
        {
            ImageList.Focus();
            if (ImageList.SelectedIndex < 0 && ImageRows.Count > 0) ImageList.SelectedIndex = 0;
        }
        else
        {
            HistoryList.Focus();
            if (HistoryList.SelectedIndex < 0 && Rows.Count > 0) HistoryList.SelectedIndex = 0;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ConfirmSelected(Keyboard.Modifiers);
                break;
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Delete:
                if (HistoryList.SelectedItem is HistoryRow del)
                {
                    e.Handled = true;
                    _history.Delete(del.Id);
                }
                break;
            case Key.P:
                if (HistoryList.SelectedItem is HistoryRow toggle)
                {
                    e.Handled = true;
                    _history.TogglePinned(toggle.Id);
                }
                break;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ConfirmSelected(ModifierKeys.None);
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
        => ConfirmSelected(ModifierKeys.Control);

    private void OnPasteClicked(object sender, RoutedEventArgs e)
        => ConfirmSelected(ModifierKeys.None);

    private void OnImageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ConfirmSelected(ModifierKeys.None);
    }

    private void OnImageListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ConfirmSelected(Keyboard.Modifiers);
                break;
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Delete:
                if (ImageList.SelectedItem is ImageHistoryRow del)
                {
                    e.Handled = true;
                    _history.DeleteImage(del.Id);
                }
                break;
            case Key.P:
                if (ImageList.SelectedItem is ImageHistoryRow toggle)
                {
                    e.Handled = true;
                    _history.TogglePinnedImage(toggle.Id);
                }
                break;
        }
    }

    private void PickIndex(int zeroBased)
    {
        if (IsImageTab)
        {
            if ((uint)zeroBased >= (uint)ImageRows.Count) return;
            ImageList.SelectedIndex = zeroBased;
        }
        else
        {
            if ((uint)zeroBased >= (uint)Rows.Count) return;
            HistoryList.SelectedIndex = zeroBased;
        }
        ConfirmSelected(Keyboard.Modifiers);
    }

    private void ConfirmSelected(ModifierKeys modifier)
    {
        if (IsImageTab) ConfirmImage(modifier);
        else ConfirmText(modifier);
    }

    private void ConfirmText(ModifierKeys modifier)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        var copyOnly = modifier.HasFlag(ModifierKeys.Control);
        var stay = modifier.HasFlag(ModifierKeys.Shift);

        if (copyOnly)
        {
            _writer.SetText(row.Text);
            if (!stay) Close();
            return;
        }

        var target = ResolvePasteTarget();
        BeginPaste(stay);
        _ = Task.Run(async () =>
        {
            try
            {
                _writer.SetText(row.Text);
                if (_anchorContext is not null && _replacer is not null)
                {
                    var replaced = await _replacer.TryReplaceAsync(_anchorContext, row.Text, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (replaced)
                    {
                        EndPaste(stay);
                        return;
                    }
                }

                if (target != 0) _paste?.PasteCurrent(target);
            }
            catch { /* ignore */ }
            EndPaste(stay);
        });
    }

    private void ConfirmImage(ModifierKeys modifier)
    {
        if (ImageList.SelectedItem is not ImageHistoryRow row) return;
        var bytes = _history.GetImageBlob(row.Id);
        if (bytes is null || bytes.Length == 0) return;

        var copyOnly = modifier.HasFlag(ModifierKeys.Control);
        var stay = modifier.HasFlag(ModifierKeys.Shift);
        var target = ResolvePasteTarget();

        try
        {
            _history.NoteSelfWrittenImage(bytes);
            _clipboardAccess.SetImagePngBytes(bytes);
        }
        catch
        {
            return;
        }

        if (copyOnly || target == 0 || _paste is null)
        {
            if (!stay) Close();
            return;
        }

        BeginPaste(stay);
        _ = Task.Run(() =>
        {
            try { _paste.PasteCurrent(target); }
            catch { /* ignore */ }
            EndPaste(stay);
        });
    }

    private nint ResolvePasteTarget()
    {
        if (_anchorContext is { Foreground.Hwnd: var hwnd } && hwnd != 0) return hwnd;
        return _pasteTargetProvider?.Invoke() ?? 0;
    }

    private void BeginPaste(bool stay)
    {
        _ignoreDeactivate = true;
        if (!stay) Close();
    }

    private void EndPaste(bool stay)
    {
        if (!stay) return;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Activate();
                FocusSearch();
                // SetForegroundWindow 触发的 Deactivated 可能排在 Activate 之后，稍等再恢复
                await Task.Delay(250);
            }
            finally
            {
                _ignoreDeactivate = false;
            }
        });
    }

    private static bool TryDigitIndex(Key key, out int index)
    {
        if (key is >= Key.D1 and <= Key.D9)
        {
            index = key - Key.D1;
            return true;
        }
        if (key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            index = key - Key.NumPad1;
            return true;
        }
        index = -1;
        return false;
    }
}

public sealed record HistoryRow(long Id, string Text, string Preview, string TimeLabel, string MetaLabel, string IndexLabel)
{
    public static HistoryRow From(ClipboardEntry e, string indexLabel = "")
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(e.Text, @"\s+", " ").Trim();
        var preview = collapsed.Length > 120 ? collapsed[..120] + "…" : collapsed;
        var local = e.CreatedAtUtc.ToLocalTime();
        var src = DisplayProcess(e.SourceProcess);
        var meta = (e.Pinned ? "钉选 · " : "")
            + (src.Length > 0 ? src + " · " : "")
            + $"{e.Text.Length} 字符";
        return new HistoryRow(e.Id, e.Text, preview, local.ToString("MM-dd HH:mm"), meta, indexLabel);
    }

    internal static string DisplayProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";
        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
    }
}

/// <summary>剪贴板图片历史的一行。Thumbnail 是低分辨率的 BitmapImage（DecodePixelWidth=120），
/// 避免每个 ListBoxItem 持有完整原图导致内存爆炸。
/// 复制操作走 ClipboardHistoryService.GetImageBlob 拉一次完整 PNG 字节</summary>
public sealed record ImageHistoryRow(
    long Id,
    BitmapImage? Thumbnail,
    string Header,
    string TimeLabel,
    string MetaLabel,
    string IndexLabel)
{
    public static ImageHistoryRow From(ClipboardImageEntry e, string indexLabel = "")
    {
        BitmapImage? thumb = null;
        if (e.PngBlob is { Length: > 0 })
        {
            try
            {
                thumb = new BitmapImage();
                thumb.BeginInit();
                thumb.CacheOption = BitmapCacheOption.OnLoad;
                thumb.DecodePixelWidth = 120;
                thumb.StreamSource = new MemoryStream(e.PngBlob, writable: false);
                thumb.EndInit();
                thumb.Freeze();
            }
            catch
            {
                thumb = null;
            }
        }
        var local = e.CreatedAtUtc.ToLocalTime();
        var size = FormatSize(e.Bytes);
        var header = e.Width > 0 && e.Height > 0
            ? $"{e.Width} × {e.Height} · {size}"
            : size;
        var ocr = string.IsNullOrWhiteSpace(e.OcrText)
            ? ""
            : "OCR: " + (e.OcrText!.Length > 60 ? e.OcrText[..60] + "…" : e.OcrText);
        var src = HistoryRow.DisplayProcess(e.SourceProcess);
        var meta = (e.Pinned ? "钉选 · " : "")
            + (src.Length > 0 ? src + " · " : "")
            + ocr;
        return new ImageHistoryRow(e.Id, thumb, header, local.ToString("MM-dd HH:mm"), meta.TrimEnd(' ', '·'), indexLabel);
    }

    private static string FormatSize(int bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
    }
}
