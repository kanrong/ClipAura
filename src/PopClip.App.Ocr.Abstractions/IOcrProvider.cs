namespace PopClip.App.Ocr;

/// <summary>多 OCR 后端的统一抽象。
///
/// 设计目标：
/// - "文件级伪插件"：每个 provider 的运行时依赖（native dll / 模型）按 plugins/ocr/{id}/ 目录是否
///   存在来决定 IsAvailable。用户复制文件即可启用，无需重启甚至无需重装；
/// - 启动时所有 provider 都被实例化、自检 IsAvailable，但绝不在构造里加载 native（保持启动期 < 100 ms）；
/// - 真正的 native 加载发生在 PrewarmInBackground 或第一次 RecognizeAsync 触发的 EnsureEngine 中；
/// - 输入用 PNG bytes 而非 System.Drawing.Bitmap：让接口跨 GUI / CLI 通用，调用方负责一次 Bitmap.Save。
///
/// 物理分发：本接口编译进 PopClip.App.Ocr.Abstractions.dll，主程序与所有 OCR plugin 都引用同一份，
/// 这样 plugin 加载后主程序能跨 AssemblyLoadContext 直接 cast 成 IOcrProvider。</summary>
public interface IOcrProvider : IDisposable
{
    /// <summary>provider 唯一 id，写到 settings 里持久化；不要含空格 / 特殊字符。
    /// 建议与 plugins/ocr/{id}/ 目录名保持一致，便于排查。</summary>
    string Id { get; }

    /// <summary>用户可见名称（设置 UI 显示）。可以中英混合，但要简洁。</summary>
    string DisplayName { get; }

    /// <summary>"自动"模式下的排序键：数字越大越优先被选为活跃 provider。
    /// 内置约定：RapidOCR=100（默认开箱即用、精度高），WeChat=80。
    /// 用户在 settings 中显式选了 provider 时这个值不起作用。</summary>
    int Priority { get; }

    /// <summary>是否处于可用状态：所有必需文件齐全 + 上一次 EnsureEngine 没有失败。
    /// 实现要保证轻量（< 10 ms 文件 IO），允许每次询问都重新求值。</summary>
    bool IsAvailable { get; }

    /// <summary>是否已完成 lazy 初始化。预热前是 false，预热或第一次识别后变 true。
    /// 用于 UI 提示"首次识别会慢一点（冷启动加载模型中）"。</summary>
    bool IsEngineReady { get; }

    /// <summary>当 IsAvailable=false 时给出人类可读原因（设置 UI / 错误对话框直接显示）。
    /// 文案建议包含：(1) 缺哪个文件 / 哪个路径；(2) 期望文件来自哪里；(3) 如何修复。
    /// IsAvailable=true 时可以返回 null。</summary>
    string? UnavailableReason { get; }

    /// <summary>触发后台冷启动：用户按下 OCR 热键时立刻调用，让 native 加载和用户拉框并行进行。
    /// 实现必须 fire-and-forget，不允许抛异常（内部 swallow + 写 debug 日志）。
    /// 已就绪 / 已知不可用 时直接 return，不应该重复加载。</summary>
    void PrewarmInBackground();

    /// <summary>执行 OCR 识别。
    ///
    /// 入参：
    /// - pngBytes：PNG 编码的图片数据；调用方负责把 Bitmap / SKBitmap / 文件流转 PNG bytes。
    ///   选 PNG 而非 raw 像素的原因：通用、自带尺寸信息、对各 provider（含写临时文件的 WeChat）都最方便。
    /// - ct：调用方可在超时 / 用户取消时取消等待；provider 内部如果 native 不可中断，
    ///   应该让 native 继续跑完但不再持有任何 UI / disposable 资源（参考 RapidOcrProvider 的 ContinueWith 模式）。
    ///
    /// 返回：
    /// - 成功识别出的文字（多行用 \n 分隔）；
    /// - 没识别到内容返回 ""；
    /// - 严重失败（IsAvailable=false / native 异常）抛 InvalidOperationException，不返回 fallback 字符串。</summary>
    Task<string> RecognizeAsync(byte[] pngBytes, CancellationToken ct);
}
