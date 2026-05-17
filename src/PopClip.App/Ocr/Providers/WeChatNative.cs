using System.IO;
using System.Runtime.InteropServices;

namespace PopClip.App.Ocr.Providers;

/// <summary>swigger/wechat-ocr 编译出的 wcocr.dll 的 P/Invoke 签名。
///
/// 签名直接对齐 swigger 仓库 c_sharp/Program.cs（Apache-2.0），关键约定：
/// - imgfn 传 UTF-8 字节数组（末尾 \0）：wcocr.dll 内部按 UTF-8 解析路径，
///   传 wchar_t* 会乱码（中文用户的 %TEMP% 路径里有汉字时尤其明显）。
/// - SetResultDelegate 必须由调用方持有引用直到 wechat_ocr 返回，否则 GC 会回收 delegate
///   导致 native 回调写入悬空地址。我们在 WeChatOcrProvider.RecognizeAsync 里用 GC.KeepAlive 保活。
///
/// 沿用经典 DllImport 而非 LibraryImport：LibraryImport 需要 AllowUnsafeBlocks=true，
/// 但本项目其他地方都不开 unsafe，为单一 P/Invoke 打开全局 unsafe 不划算。</summary>
internal static class WeChatNative
{
    public const string DllName = "wcocr.dll";

    /// <summary>wcocr.dll 回调签名：native 通过它把 UTF-8 JSON 结果指针交回 C# 端。
    /// 调用结束前 delegate 必须保活，回调里不能抛异常（会跨 native 边界 crash）。</summary>
    public delegate void SetResultDelegate(IntPtr resultUtf8CStr);

    private static int _resolverInstalled;

    /// <summary>把 wcocr.dll 的 P/Invoke 重定向到指定 plugin 目录加载。
    ///
    /// 必要性：默认 DllImport 只在 ClipAura.exe 所在目录 + 系统目录 + PATH 找 dll，
    /// 不会去子目录 plugins/ocr/wechat/ 找；不注册 resolver 直接调 wechat_ocr 会抛
    /// DllNotFoundException(0x8007007E)。
    ///
    /// 附带解决依赖问题：NativeLibrary.Load(absolutePath) 内部用
    /// LoadLibraryEx(LOAD_WITH_ALTERED_SEARCH_PATH)，会把 wcocr.dll 所在目录加进依赖搜索路径，
    /// 所以同目录下的其它 dll（如 protobuf-lite / vcruntime 影子拷贝）也能被找到。
    ///
    /// 幂等：Interlocked.Exchange 保证多线程并发首次调用只生效一次。
    /// 重复 SetDllImportResolver 会抛 InvalidOperationException，所以必须防重入。
    ///
    /// 注册时机：在第一次 P/Invoke wechat_ocr / stop_ocr 之前即可，不需要在 assembly 加载初期。
    /// 当前由 WeChatOcrProvider 构造函数调用，远早于用户触发首次 OCR。</summary>
    public static void InstallResolver(string pluginDir)
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;
        var fullPath = Path.Combine(pluginDir, DllName);
        NativeLibrary.SetDllImportResolver(typeof(WeChatNative).Assembly, (name, asm, sp) =>
        {
            // 仅干预 wcocr.dll；其它 P/Invoke 走 CLR 默认解析
            if (!string.Equals(name, DllName, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            return NativeLibrary.TryLoad(fullPath, out var handle) ? handle : IntPtr.Zero;
        });
    }

    /// <summary>对单张图执行 OCR。
    /// 第一次调用时 wcocr.dll 会 spawn WeChatOCR.exe（或 wxocr.dll 自管的子进程）做 IPC，
    /// 冷启动大约 1-2 秒；后续调用复用驻留进程，约 100-300 ms。
    ///
    /// 同步阻塞调用：native 在收到 callback 之后才 return，所以调用方应该在 Task.Run 里跑。</summary>
    /// <param name="ocrExe">WeChat 3.x: WeChatOCR.exe 全路径；WeChat 4.0: wxocr.dll 全路径</param>
    /// <param name="wechatDir">微信安装目录（含 WeChat.exe / Weixin.exe 的版本子目录）</param>
    /// <param name="imgFnUtf8">图片绝对路径的 UTF-8 字节数组，末尾必须含 \0</param>
    /// <param name="setRes">结果回调；返回前一直被 wcocr 持有</param>
    /// <returns>调用是否成功（注意：成功不代表识别到文字，只代表流程未崩）</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool wechat_ocr(
        [MarshalAs(UnmanagedType.LPWStr)] string ocrExe,
        [MarshalAs(UnmanagedType.LPWStr)] string wechatDir,
        byte[] imgFnUtf8,
        SetResultDelegate setRes);

    /// <summary>终止 wcocr.dll spawn 出来的 WeChatOCR / wxocr 子进程。
    /// 应用退出前调一次，避免子进程残留。</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void stop_ocr();
}
