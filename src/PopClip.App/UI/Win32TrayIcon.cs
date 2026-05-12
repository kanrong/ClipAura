using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.UI;

/// <summary>纯 Win32 实现的系统托盘图标。
/// 用一个 message-only window 接收 Shell_NotifyIcon 的回调消息，
/// 不依赖 System.Windows.Forms.NotifyIcon。
///
/// 工作流：
/// 1. 注册一个隐藏窗口类并创建 HWND_MESSAGE 窗口接收 WM_TRAYCALLBACK
/// 2. Shell_NotifyIcon(NIM_ADD) 注册托盘项；用 NOTIFYICON_VERSION_4 让点击行为更现代
/// 3. WndProc 里把鼠标消息翻译成 LeftClick / RightClick / DoubleClick 事件给上层用
/// 4. Notify 走 NIM_MODIFY + NIF_INFO 弹气泡通知</summary>
internal sealed class Win32TrayIcon : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_USER + 1;
    private const uint Id = 1;

    private readonly ILog _log;
    private readonly NativeMethods.WndProcDelegate _wndProcDelegate;
    private readonly string _className;
    private string _tooltip;
    private nint _hwnd;
    private nint _hIcon;
    private bool _added;

    /// <summary>左键单击或键盘选中（NIN_SELECT/NIN_KEYSELECT）</summary>
    public event Action? LeftClicked;

    /// <summary>右键点击（菜单键 + Shift+F10 等都会归一到 WM_CONTEXTMENU）</summary>
    public event Action<int, int>? RightClicked;

    /// <summary>左键双击</summary>
    public event Action? DoubleClicked;

    public Win32TrayIcon(ILog log, string tooltip)
    {
        _log = log;
        _tooltip = tooltip ?? string.Empty;
        _wndProcDelegate = WndProc;
        _className = "ClipAuraTrayMsg_" + Guid.NewGuid().ToString("N");
    }

    /// <summary>创建消息窗口并安装托盘项。必须在 UI 线程调用</summary>
    public void Install()
    {
        EnsureMessageWindow();
        _hIcon = LoadTrayIcon();

        var data = BuildData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
        {
            _log.Warn("Shell_NotifyIcon NIM_ADD failed", ("err", Marshal.GetLastWin32Error()));
            return;
        }

        var versionData = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = Id,
            uVersionOrTimeout = NativeMethods.NOTIFYICON_VERSION_4,
            szTip = "",
            szInfo = "",
            szInfoTitle = "",
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref versionData);

        _added = true;
        _log.Info("tray icon installed");
    }

    public void SetTooltip(string text)
    {
        _tooltip = text ?? string.Empty;
        if (!_added) return;
        var data = BuildData(NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>弹气泡 / Toast 通知。Windows 10+ 会自动转为通知中心条目</summary>
    public void ShowBalloon(string title, string body, bool isError = false)
    {
        if (!_added) return;
        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = Id,
            uFlags = NativeMethods.NIF_INFO,
            szTip = _tooltip,
            szInfo = body ?? string.Empty,
            szInfoTitle = title ?? "ClipAura",
            dwInfoFlags = isError ? NativeMethods.NIIF_ERROR : NativeMethods.NIIF_INFO,
        };
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data))
        {
            _log.Warn("tray balloon failed", ("err", Marshal.GetLastWin32Error()));
        }
    }

    private NativeMethods.NOTIFYICONDATAW BuildData(uint flags)
    {
        return new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = Id,
            uFlags = flags,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _hIcon,
            szTip = string.IsNullOrEmpty(_tooltip) ? "ClipAura" : _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private void EnsureMessageWindow()
    {
        if (_hwnd != 0) return;

        var hInstance = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = _className,
            lpszMenuName = string.Empty,
        };
        var atom = NativeMethods.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            _log.Warn("RegisterClassEx failed", ("err", Marshal.GetLastWin32Error()));
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            _className,
            "ClipAuraTrayMsg",
            0,
            0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE,
            0,
            hInstance,
            0);
        if (_hwnd == 0)
        {
            _log.Error("CreateWindowEx (message-only) failed", new InvalidOperationException(
                "Win32 error " + Marshal.GetLastWin32Error()));
        }
    }

    /// <summary>NOTIFYICON_VERSION_4 模式下，lParam 低字编码事件、高字编码 x/y（屏幕坐标）</summary>
    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            var evt = (uint)((long)lParam & 0xFFFF);
            // V4 时高位编码屏幕坐标；旧版本回退到 GetCursorPos
            var (x, y) = ReadEventPoint(wParam);
            try
            {
                switch (evt)
                {
                    case NativeMethods.NIN_SELECT:
                    case NativeMethods.NIN_KEYSELECT:
                    case (uint)NativeMethods.WM_LBUTTONUP_TRAY:
                        LeftClicked?.Invoke();
                        break;
                    case (uint)NativeMethods.WM_LBUTTONDBLCLK:
                        DoubleClicked?.Invoke();
                        break;
                    case (uint)NativeMethods.WM_RBUTTONUP_TRAY:
                    case (uint)NativeMethods.WM_CONTEXTMENU:
                        RightClicked?.Invoke(x, y);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("tray dispatch failed", ("err", ex.Message));
            }
            return 0;
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static (int X, int Y) ReadEventPoint(nint wParam)
    {
        // NOTIFYICON_VERSION_4 下 wParam 直接给 (X, Y)（16/16 拆分）
        var raw = (long)wParam;
        var x = (short)(raw & 0xFFFF);
        var y = (short)((raw >> 16) & 0xFFFF);
        if (x == 0 && y == 0 && NativeMethods.GetCursorPos(out var pt))
        {
            return (pt.X, pt.Y);
        }
        return (x, y);
    }

    /// <summary>优先从 EXE 自身嵌入的 .ico（ApplicationIcon）加载；失败则回退到 pack uri 资源里的 AppIcon.ico</summary>
    private nint LoadTrayIcon()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var hIcon = NativeMethods.LoadImage(0, exe, NativeMethods.IMAGE_ICON, 16, 16, NativeMethods.LR_LOADFROMFILE);
                if (hIcon != 0) return hIcon;
            }
        }
        catch (Exception ex)
        {
            _log.Debug("LoadImage from exe failed", ("err", ex.Message));
        }

        try
        {
            var resource = WpfApplication.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"));
            if (resource is not null)
            {
                var temp = Path.Combine(Path.GetTempPath(), "ClipAuraTray.ico");
                using (var stream = resource.Stream)
                using (var file = File.Create(temp))
                {
                    stream.CopyTo(file);
                }
                var hIcon = NativeMethods.LoadImage(0, temp, NativeMethods.IMAGE_ICON, 16, 16, NativeMethods.LR_LOADFROMFILE);
                if (hIcon != 0) return hIcon;
            }
        }
        catch (Exception ex)
        {
            _log.Debug("LoadImage from pack stream failed", ("err", ex.Message));
        }

        return 0;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = Id,
                szTip = "",
                szInfo = "",
                szInfoTitle = "",
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }
        if (_hIcon != 0)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = 0;
        }
        if (_hwnd != 0)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
