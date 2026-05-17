using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using WpfClipboard = System.Windows.Clipboard;

namespace PopClip.Uia.Clipboard;

/// <summary>所有剪贴板读写都通过本类执行，由 ClipboardThread 保证在 STA 上运行。
/// 直接使用 System.Windows.Clipboard 时偶发的 ExternalException(剪贴板被占用) 在此做有界重试</summary>
public sealed class ClipboardAccess
{
    private readonly ClipboardThread _thread;
    private const int RetryCount = 3;
    private const int RetryDelayMs = 12;

    public ClipboardAccess(ClipboardThread thread) => _thread = thread;

    public string? GetText() => _thread.Invoke(GetTextOnSta);

    /// <summary>仅检查剪贴板是否包含文本，不实际复制内容；
    /// 用于 PasteAction.CanRun 等高频判断点，避免每次浮窗弹出都把潜在的大段剪贴板正文搬到本进程</summary>
    public bool HasText() => _thread.Invoke(HasTextOnSta);

    /// <summary>仅检查剪贴板是否包含图片，不实际搬运位图数据。</summary>
    public bool HasImage() => _thread.Invoke(HasImageOnSta);

    /// <summary>把剪贴板图片编码成 PNG 字节后跨线程返回，避免 BitmapSource 跨 Dispatcher 使用。</summary>
    public byte[]? GetImagePngBytes() => _thread.Invoke(GetImagePngBytesOnSta);

    public void SetText(string text) => _thread.Invoke(() => SetTextOnSta(text));

    public void Clear() => _thread.Invoke(() =>
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try { WpfClipboard.Clear(); return; }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
        }
    });

    public ClipboardSnapshot Capture() => _thread.Invoke(ClipboardSnapshot.CaptureOnSta);

    public void Restore(ClipboardSnapshot snapshot) => _thread.Invoke(snapshot.RestoreOnSta);

    private static string? GetTextOnSta()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                return WpfClipboard.ContainsText() ? WpfClipboard.GetText() : null;
            }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return null; }
        }
        return null;
    }

    private static bool HasTextOnSta()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try { return WpfClipboard.ContainsText(); }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return false; }
        }
        return false;
    }

    private static bool HasImageOnSta()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try { return WpfClipboard.ContainsImage(); }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return false; }
        }
        return false;
    }

    private static byte[]? GetImagePngBytesOnSta()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                if (!WpfClipboard.ContainsImage()) return null;
                var image = WpfClipboard.GetImage();
                if (image is null) return null;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return null; }
        }
        return null;
    }

    private static void SetTextOnSta(string text)
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                WpfClipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
        }
    }
}
