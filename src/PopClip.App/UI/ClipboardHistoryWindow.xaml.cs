using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ITextReplacer? _replacer;
    private readonly SelectionContext? _anchorContext;
    private readonly ClipboardPaste? _paste;
    public ObservableCollection<HistoryRow> Rows { get; } = new();

    public ClipboardHistoryWindow(
        ClipboardHistoryService history,
        IClipboardWriter writer,
        ITextReplacer? replacer,
        SelectionContext? anchorContext,
        ClipboardPaste? paste)
    {
        _history = history;
        _writer = writer;
        _replacer = replacer;
        _anchorContext = anchorContext;
        _paste = paste;
        InitializeComponent();
        HistoryList.ItemsSource = Rows;
        Loaded += (_, _) =>
        {
            Refresh();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        };
    }

    private void Refresh()
    {
        var query = SearchBox.Text?.Trim();
        Rows.Clear();
        foreach (var e in _history.List(120, query))
        {
            Rows.Add(HistoryRow.From(e));
        }
        if (Rows.Count > 0) HistoryList.SelectedIndex = 0;
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
        => HandleConfirm(modifier: ModifierKeys.None);

    private void OnCopyClicked(object sender, RoutedEventArgs e)
        => HandleConfirm(modifier: ModifierKeys.Control);

    private void OnPasteClicked(object sender, RoutedEventArgs e)
        => HandleConfirm(modifier: ModifierKeys.None);

    private void HandleConfirm(ModifierKeys modifier)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        var copyOnly = modifier.HasFlag(ModifierKeys.Control);
        _writer.SetText(row.Text);

        if (!copyOnly && _anchorContext is not null && _replacer is not null)
        {
            _ = ReplaceAndCloseAsync(row.Text);
            return;
        }

        if (!copyOnly && _paste is not null && _anchorContext is not null)
        {
            // 没有 UIA replacer 也尝试 Ctrl+V 落地
            _ = Task.Run(() =>
            {
                try { _paste.PasteCurrent(_anchorContext.Foreground.Hwnd); }
                catch { /* ignore */ }
            });
        }
        Close();
    }

    private async Task ReplaceAndCloseAsync(string text)
    {
        try
        {
            if (_anchorContext is not null && _replacer is not null)
            {
                await _replacer.TryReplaceAsync(_anchorContext, text, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch { /* swallow */ }
        finally { Close(); }
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
