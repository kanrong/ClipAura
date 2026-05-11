using System.Windows;
using WpfClipboard = System.Windows.Clipboard;

namespace PopClip.Uia.Clipboard;

/// <summary>剪贴板的内存快照。MVP 仅备份常见格式：UnicodeText / Html / Rtf / FileDrop。
/// 复杂二进制格式（CF_DIB、自定义对象引用等）暂不保留，能覆盖 95%+ 用户场景。
/// CaptureOnSta / RestoreOnSta 必须在 STA 线程内执行，由 ClipboardAccess 统一调度</summary>
public sealed class ClipboardSnapshot
{
    public string? UnicodeText { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    public string[]? FileDrop { get; init; }

    internal static ClipboardSnapshot CaptureOnSta()
    {
        try
        {
            var data = WpfClipboard.GetDataObject();
            if (data is null) return new ClipboardSnapshot();

            return new ClipboardSnapshot
            {
                UnicodeText = data.GetDataPresent(DataFormats.UnicodeText)
                    ? data.GetData(DataFormats.UnicodeText) as string
                    : data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null,
                Html = data.GetDataPresent(DataFormats.Html) ? data.GetData(DataFormats.Html) as string : null,
                Rtf = data.GetDataPresent(DataFormats.Rtf) ? data.GetData(DataFormats.Rtf) as string : null,
                FileDrop = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null,
            };
        }
        catch
        {
            return new ClipboardSnapshot();
        }
    }

    internal void RestoreOnSta()
    {
        try
        {
            var data = new DataObject();
            var any = false;
            if (UnicodeText is not null) { data.SetData(DataFormats.UnicodeText, UnicodeText); any = true; }
            if (Html is not null) { data.SetData(DataFormats.Html, Html); any = true; }
            if (Rtf is not null) { data.SetData(DataFormats.Rtf, Rtf); any = true; }
            if (FileDrop is not null) { data.SetData(DataFormats.FileDrop, FileDrop); any = true; }

            if (any)
            {
                WpfClipboard.SetDataObject(data, copy: true);
            }
            else
            {
                WpfClipboard.Clear();
            }
        }
        catch
        {
            // 恢复失败不要再抛
        }
    }
}
