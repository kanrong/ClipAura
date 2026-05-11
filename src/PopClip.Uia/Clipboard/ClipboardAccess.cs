using System.Runtime.InteropServices;
using System.Windows;
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
