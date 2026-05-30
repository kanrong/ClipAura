using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PopClip.App.Services;
using PopClip.Core.Actions;
using PopClip.Core.Model;
using PopClip.Uia.Clipboard;

namespace PopClip.App.UI;

/// <summary>剪贴板历史搜索 / 选取面板。
/// 操作语义：
///   Enter           → 用所选条目替换调用上下文（若有），否则写剪贴板；窗口关闭
///   Ctrl+Enter       → 只复制到系统剪贴板，不替换选区
///   Del              → 删除条目
///   P                → 钉选 / 取消钉选
/// </summary>
internal partial class ClipboardHistoryWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ClipboardHistoryService _history;
    private readonly IClipboardWriter _writer;
    private readonly ClipboardAccess _clipboardAccess;
    private readonly ITextReplacer? _replacer;
    private readonly SelectionContext? _anchorContext;
    private readonly ClipboardPaste? _paste;
    /// <summary>无选区上下文（托盘/快捷键唤起）时，"粘贴目标窗口"的实时取值器。
    /// 之所以是回调而非固定句柄：历史窗口可常驻不关，用户会在它与目标窗口间来回切换，
    /// 必须在点击"粘贴"的那一刻才取最近一个外部前台窗口，而不是打开窗口时定格。返回 0 表示未知</summary>
    private readonly Func<nint>? _pasteTargetProvider;
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
        Loaded += (_, _) =>
        {
            Refresh();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        };
    }

    private bool IsImageTab => TabImageRadio?.IsChecked == true;

    private void Refresh()
    {
        if (IsImageTab) RefreshImages();
        else RefreshTexts();
    }

    private void RefreshTexts()
    {
        var query = SearchBox.Text?.Trim();
        Rows.Clear();
        foreach (var e in _history.List(120, query))
        {
            Rows.Add(HistoryRow.From(e));
        }
        if (Rows.Count > 0) HistoryList.SelectedIndex = 0;
    }

    private void RefreshImages()
    {
        ImageRows.Clear();
        // ListImages includeBlob=true 时会同步把 PNG 字节加载出来用于缩略图。
        // 对于 30 张 1~3MB 图片来说总量在 30~90 MB，一次性加载略大；
        // 用 OnDemand 解码避免占住内存：每个 BitmapImage 只解到 DecodePixelWidth=120 的低分版本
        foreach (var e in _history.ListImages(60, includeBlob: true))
        {
            ImageRows.Add(ImageHistoryRow.From(e));
        }
        if (ImageRows.Count > 0) ImageList.SelectedIndex = 0;
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (HistoryList is null || ImageList is null) return;
        var img = IsImageTab;
        HistoryList.Visibility = img ? Visibility.Collapsed : Visibility.Visible;
        ImageList.Visibility = img ? Visibility.Visible : Visibility.Collapsed;
        // 切到图片 Tab 时清掉文本搜索 query：图片 Tab 暂未支持搜索，避免上次输入误导
        if (img) SearchBox.Text = "";
        Refresh();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (e.Key == Key.Down)
        {
            e.Handled = true;
            HistoryList.Focus();
            if (HistoryList.SelectedIndex < 0 && Rows.Count > 0) HistoryList.SelectedIndex = 0;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            HandleConfirm(modifier: Keyboard.Modifiers);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                HandleConfirm(modifier: Keyboard.Modifiers);
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
                    Refresh();
                }
                break;
            case Key.P:
                if (HistoryList.SelectedItem is HistoryRow toggle)
                {
                    e.Handled = true;
                    _history.TogglePinned(toggle.Id);
                    Refresh();
                }
                break;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HandleConfirm(modifier: ModifierKeys.None);
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (IsImageTab) HandleImageCopy();
        else HandleConfirm(modifier: ModifierKeys.Control);
    }

    private void OnPasteClicked(object sender, RoutedEventArgs e)
    {
        if (IsImageTab) HandleImageCopy();
        else HandleConfirm(modifier: ModifierKeys.None);
    }

    private void OnImageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HandleImageCopy();
    }

    private void OnImageListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                HandleImageCopy();
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
                    RefreshImages();
                }
                break;
            case Key.P:
                if (ImageList.SelectedItem is ImageHistoryRow toggle)
                {
                    e.Handled = true;
                    _history.TogglePinnedImage(toggle.Id);
                    RefreshImages();
                }
                break;
        }
    }

    /// <summary>把所选图片复制到系统剪贴板。
    /// 不像文本支持"粘贴到目标进程"——图片粘贴语义随目标应用差异极大（聊天工具粘附件、Office 嵌入位图、
    /// 文件管理器变成保存对话框），统一只复制到剪贴板让用户自己 Ctrl+V 决定怎么用</summary>
    private void HandleImageCopy()
    {
        if (ImageList.SelectedItem is not ImageHistoryRow row) return;
        var bytes = _history.GetImageBlob(row.Id);
        if (bytes is null || bytes.Length == 0) return;
        try
        {
            // 让监听路径忽略这次回环写入，避免把刚刚选中的图又入库一次形成"翻新"
            _history.NoteSelfWrittenImage(bytes);
            _clipboardAccess.SetImagePngBytes(bytes);
            Close();
        }
        catch
        {
            // 写剪贴板失败的兜底：保持窗口打开，让用户重试
        }
    }

    private void HandleConfirm(ModifierKeys modifier)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;

        // Ctrl+Enter：用户明确只要"复制到剪贴板"，不粘贴
        if (modifier.HasFlag(ModifierKeys.Control))
        {
            _writer.SetText(row.Text);
            Close();
            return;
        }

        // 浮窗触发：有选区上下文 → 复制并尝试替换原选区，替换失败再退回粘贴到原窗口
        if (_anchorContext is not null)
        {
            Close();
            _ = Task.Run(async () =>
            {
                try
                {
                    _writer.SetText(row.Text);
                    if (_replacer is not null)
                    {
                        var replaced = await _replacer.TryReplaceAsync(_anchorContext, row.Text, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (replaced) return;
                    }

                    _paste?.PasteCurrent(_anchorContext.Foreground.Hwnd);
                }
                catch { /* ignore */ }
            });
            return;
        }

        // 托盘/快捷键触发：在点击的此刻实时取"切到本历史窗口之前的那个外部前台窗口"作为目标，
        // 支持"历史窗口常驻 → 切到目标窗口 → 切回历史窗口 → 粘贴"。必须在 Close 之前取：
        // 关窗会改变前台窗口，而此刻历史窗口还在前台、最近外部窗口仍是用户想粘贴的目标。
        // PasteCurrent 内部 SetForegroundWindow 把焦点切回目标，解决"历史窗口占着焦点粘不进去"
        var targetHwnd = _pasteTargetProvider?.Invoke() ?? 0;
        if (targetHwnd != 0 && _paste is not null)
        {
            Close();
            _ = Task.Run(() =>
            {
                try
                {
                    _writer.SetText(row.Text);
                    _paste.PasteCurrent(targetHwnd);
                }
                catch { /* ignore */ }
            });
            return;
        }

        // 既无选区也取不到目标窗口：退化为仅复制到剪贴板
        _writer.SetText(row.Text);
        Close();
    }
}

public sealed record HistoryRow(long Id, string Text, string Preview, string TimeLabel, string MetaLabel)
{
    public static HistoryRow From(ClipboardEntry e)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(e.Text, @"\s+", " ").Trim();
        var preview = collapsed.Length > 120 ? collapsed[..120] + "…" : collapsed;
        var local = e.CreatedAtUtc.ToLocalTime();
        var meta = (e.Pinned ? "📌 钉选 · " : "") + $"{e.Text.Length} 字符";
        return new HistoryRow(e.Id, e.Text, preview, local.ToString("MM-dd HH:mm"), meta);
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
    string MetaLabel)
{
    public static ImageHistoryRow From(ClipboardImageEntry e)
    {
        BitmapImage? thumb = null;
        if (e.PngBlob is { Length: > 0 })
        {
            try
            {
                thumb = new BitmapImage();
                thumb.BeginInit();
                // CacheOption=OnLoad 让 BitmapImage 不持有 stream 引用，stream 关闭后图像仍可见；
                // DecodePixelWidth=120 让解码出的位图固定宽度 120 像素，缩略图显示用足够，
                // 大幅节省内存（4K 截图 8MP → 14400px²，缩略后 ~14kpx²）
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
        var meta = (e.Pinned ? "📌 钉选 · " : "")
            + (string.IsNullOrEmpty(e.SourceProcess) ? "" : e.SourceProcess + " · ")
            + (string.IsNullOrEmpty(ocr) ? "" : ocr);
        return new ImageHistoryRow(e.Id, thumb, header, local.ToString("MM-dd HH:mm"), meta);
    }

    private static string FormatSize(int bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
    }
}
