using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PopClip.Core.Logging;

namespace PopClip.App.Ocr.Providers;

/// <summary>WeChat OCR provider：通过 swigger/wechat-ocr 编译的 wcocr.dll 调用本机微信带的 OCR 后端。
///
/// 安装步骤（用户视角）：
/// 1. 从 https://github.com/swigger/wechat-ocr/releases 下载 wcocr.dll（64-bit），放到
///    {ClipAura}\plugins\ocr\wechat\wcocr.dll；
/// 2. 已安装微信 3.x / 4.0 并启动过一次（首启动会下载 WeChatOCR 插件到 AppData）；
/// 3. 可选：如果探测失败（少见），手动创建 plugins\ocr\wechat\paths.json 显式指定路径。
///
/// 优点：精度业界领先（腾讯 OCR），完全免费免授权；
/// 缺点：依赖本机微信安装；wcocr.dll 需要用户自己编译（项目本身不能也不应该再分发腾讯二进制）；
///       首次调用约 1-2 秒冷启动（spawn 子进程）；OCR 子进程驻留约 80 MB 内存。
///
/// Priority=80：低于 RapidOcr（默认）但高于 ChineseLite。
/// "自动"模式下：如果用户装了微信且放了 wcocr.dll，会被自动选中（精度优于 RapidOcr）。</summary>
internal sealed class WeChatOcrProvider : IOcrProvider
{
    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private bool _hasInvokedNative; // 决定 Dispose 时是否调 stop_ocr

    /// <summary>探测一次的结果缓存：null 表示"还没探测过"。</summary>
    private WeChatPaths? _cachedPaths;
    private string? _cachedError;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public WeChatOcrProvider(ILog log)
    {
        _log = log;
        // 构造期就把 P/Invoke 解析器装好，让 wcocr.dll 从 plugins/ocr/wechat/ 加载而不是 exe 同目录。
        // 即使此时 dll 还不存在也无所谓，resolver 只在首次 P/Invoke 调用时执行
        WeChatNative.InstallResolver(PluginDir);
    }

    public string Id => OcrProviderIds.WeChat;
    public string DisplayName => "WeChat OCR";
    public int Priority => 80;

    private string PluginDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "ocr", "wechat");

    private string WcocrDllPath => Path.Combine(PluginDir, WeChatNative.DllName);

    public bool IsAvailable
    {
        get
        {
            if (_disposed) return false;
            if (!File.Exists(WcocrDllPath)) return false;
            ResolvePaths();
            return _cachedPaths is not null;
        }
    }

    public bool IsEngineReady => _hasInvokedNative;

    /// <summary>两个独立条件分开诊断，让用户清楚区分 wcocr.dll 与 wxocr.dll：
    /// - wcocr.dll: C# 调用层，必须用户从 swigger releases 自己放（plugins/ocr/wechat/）
    /// - wxocr.dll: 微信自带的 OCR 后端，ClipAura 自动从 %APPDATA% 探测，不用管
    ///
    /// 它们的命名只差一个字母（c vs x）特别容易混淆，所以即使 wcocr.dll 缺失，
    /// 也仍然报告 wxocr.dll 的探测结果，让用户知道这一步已经替他做完了</summary>
    public string? UnavailableReason
    {
        get
        {
            if (_disposed) return "provider 已释放";

            // 两个条件独立探测一次，结果分别汇报
            ResolvePaths();
            var wcocrOk = File.Exists(WcocrDllPath);
            var pathsOk = _cachedPaths is not null;
            if (wcocrOk && pathsOk) return null;

            var sb = new System.Text.StringBuilder();
            if (!wcocrOk)
            {
                sb.AppendLine("✗ 缺少 wcocr.dll (C# 调用层，必须由你提供)");
                sb.AppendLine($"  从 https://github.com/swigger/wechat-ocr/releases 下载 64-bit wcocr.dll，放到:");
                sb.Append("  ").AppendLine(WcocrDllPath);
            }
            else
            {
                sb.AppendLine("✓ wcocr.dll 已就位");
            }

            if (!pathsOk)
            {
                sb.AppendLine();
                sb.AppendLine("✗ 找不到本机微信 (wxocr.dll / wechatDir):");
                sb.AppendLine(_cachedError ?? "未知原因");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("✓ wxocr.dll 已自动探测 (无需拷贝):");
                sb.Append("  ").AppendLine(_cachedPaths!.OcrExe);
                sb.AppendLine("✓ wechatDir 已自动探测:");
                sb.Append("  ").AppendLine(_cachedPaths.WechatDir);
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>路径探测做轻量缓存（10 秒过期）：每次 IsAvailable / UnavailableReason 都不重新打开
    /// 注册表 + 扫 Program Files；但又允许用户中途装了微信后大约 10 秒内被感知到。
    ///
    /// 探测结果变化时（之前不可用现在可用 / 之前可用现在不可用 / 命中目录变了）写一行 Info 日志，
    /// 让用户在日志里能直接看到当前选中的 ocrExe 与 wechatDir，避免设置 UI 没打开时排查困难</summary>
    private void ResolvePaths()
    {
        var now = DateTime.UtcNow;
        if (_cachedAtUtc != DateTime.MinValue && (now - _cachedAtUtc).TotalSeconds < 10)
            return;
        var prev = _cachedPaths;
        _cachedPaths = WeChatPathProbe.Probe(PluginDir, out _cachedError);
        _cachedAtUtc = now;

        // 仅在结果变化时记一行，避免反复同步刷日志
        if (!Equals(prev, _cachedPaths))
        {
            if (_cachedPaths is not null)
                _log.Info("ocr wechat paths resolved",
                    ("source", _cachedPaths.Source),
                    ("ocrExe", _cachedPaths.OcrExe),
                    ("wechatDir", _cachedPaths.WechatDir));
            else if (_cachedError is not null)
                _log.Info("ocr wechat paths unresolved", ("reason", _cachedError));
        }
    }

    /// <summary>WeChat OCR 没有"加载模型"的预热概念，wcocr.dll 内部按需 spawn 子进程。
    /// 这里故意留空：构造一张最小 PNG 提前调一次会污染日志且不稳定，
    /// 让用户承担第一次 OCR 的 ~1.5 秒冷启动反而更可控。</summary>
    public void PrewarmInBackground() { /* no-op by design */ }

    public async Task<OcrResult> RecognizeAsync(byte[] pngBytes, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
        if (pngBytes is null || pngBytes.Length == 0) return OcrResult.Empty;
        if (!IsAvailable)
            throw new InvalidOperationException($"WeChat OCR 不可用：{UnavailableReason}");
        ct.ThrowIfCancellationRequested();

        var paths = _cachedPaths!; // IsAvailable=true 保证非 null

        // 从 PNG 头部读出尺寸（不解码像素），用于在 JSON 缺失 width/height 字段时给 OcrResult 填一个回退值。
        // PNG IHDR 在文件偏移 [16..23]，big-endian uint32 width / height
        var (pngW, pngH) = TryReadPngSize(pngBytes);

        var tempPng = Path.Combine(Path.GetTempPath(), $"clipaura_wcocr_{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(tempPng, pngBytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 同步阻塞 native 调用挂到线程池，让出 UI 线程；超时由调用方 ct 控制。
                // 注意：调用方取消后 native 仍在跑（wcocr 不支持中断），所以 _gate 直到 native 返回才会被释放
                var runTask = Task.Run(() => InvokeNative(paths, tempPng, pngW, pngH), CancellationToken.None);

                var winner = await Task.WhenAny(runTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                if (winner != runTask)
                {
                    _log.Warn("ocr wechat timed out, native call still running",
                        ("id", Id), ("tempPng", tempPng));
                    // 让 runTask 在后台跑完并 Release 锁
                    _ = runTask.ContinueWith(_ => { try { _gate.Release(); } catch { } },
                        TaskScheduler.Default);
                    throw new OperationCanceledException(ct);
                }
                try { _gate.Release(); } catch (ObjectDisposedException) { }
                return await runTask.ConfigureAwait(false);
            }
            catch
            {
                try { _gate.Release(); } catch { }
                throw;
            }
        }
        finally
        {
            try { File.Delete(tempPng); } catch { /* 防 antivirus 锁文件 */ }
        }
    }

    private OcrResult InvokeNative(WeChatPaths paths, string tempPng, int fallbackWidth, int fallbackHeight)
    {
        var sw = Stopwatch.StartNew();
        var holder = new ResultHolder();
        WeChatNative.SetResultDelegate cb = holder.SetResult;
        try
        {
            // wcocr 内部要求 imgfn 是 UTF-8 byte[] 且带 \0 结尾
            var imgFnBytes = Encoding.UTF8.GetBytes(tempPng + "\0");
            bool ok;
            try
            {
                ok = WeChatNative.wechat_ocr(paths.OcrExe, paths.WechatDir, imgFnBytes, cb);
            }
            catch (DllNotFoundException ex)
            {
                // wcocr.dll 本身找不到，或它的某个依赖 (msvcp140 / vcruntime / protobuf-lite 等) 缺失。
                // HRESULT 0x8007007E = ERROR_MOD_NOT_FOUND；不区分缺哪个，统一指引用户检查 VC++ Redist
                throw new InvalidOperationException(
                    $"加载 wcocr.dll 失败 (或其依赖缺失)。请确认：(1) {Path.Combine(PluginDir, WeChatNative.DllName)} " +
                    $"是 64-bit 版本；(2) 已安装 Microsoft Visual C++ 2015-2022 Redistributable (x64)；" +
                    $"(3) wcocr.dll 同目录如有 protobuf-lite.dll / 其它 .dll 也一起放好。原始错误：{ex.Message}", ex);
            }
            catch (BadImageFormatException ex)
            {
                // 32-bit wcocr.dll 被 64-bit ClipAura 加载，或反之
                throw new InvalidOperationException(
                    $"wcocr.dll 位数不匹配 (ClipAura 是 64-bit)。请重新下载 64-bit 版本放到 " +
                    $"{Path.Combine(PluginDir, WeChatNative.DllName)}。原始错误：{ex.Message}", ex);
            }
            _hasInvokedNative = true;
            sw.Stop();
            if (!ok)
            {
                _log.Warn("ocr wechat returned false",
                    ("ms", sw.ElapsedMilliseconds), ("ocrExe", paths.OcrExe), ("wechatDir", paths.WechatDir));
                throw new InvalidOperationException(
                    $"wechat_ocr 返回 false (ocrExe={paths.OcrExe}, wechatDir={paths.WechatDir})。" +
                    $"请确认微信版本与 wcocr.dll 匹配、微信至少打开过一次");
            }
            var parsed = ParseJsonResult(holder.JsonResult, fallbackWidth, fallbackHeight);
            _log.Debug("ocr wechat run",
                ("ms", sw.ElapsedMilliseconds),
                ("len", parsed.FullText.Length),
                ("blocks", parsed.Blocks.Count),
                ("source", paths.Source));
            return parsed;
        }
        finally
        {
            // 保 delegate 不被 GC 提前回收：native 回调写入悬空地址 = crash
            GC.KeepAlive(cb);
            GC.KeepAlive(holder);
        }
    }

    /// <summary>解析 wcocr.dll 返回的 JSON，输出含位置的 OcrResult。
    ///
    /// 典型响应（不同 wcocr 版本字段名略有差异）：
    /// {
    ///   "errcode": 0, "width": 1920, "height": 1080,
    ///   "ocr_response": [
    ///     // 形式 A: 平铺 left/top/right/bottom
    ///     {"left":100,"top":50,"right":200,"bottom":80,"rate":0.95,"text":"Hello"},
    ///     // 形式 B: 嵌套 rect 对象
    ///     {"rect":{"left":100,"top":50,"right":200,"bottom":80},"rate":0.95,"text":"Hello"},
    ///     // 形式 C: 多边形 pos (4 点)
    ///     {"pos":[[x1,y1],[x2,y2],[x3,y3],[x4,y4]],"text":"Hello"}
    ///   ]
    /// }
    ///
    /// 解析策略：先按"找到含 text 字段的对象 = 一个 block"递归收集；
    /// 每个 block 尝试从同对象内提取 rect 或 pos，缺失时整张图为框（fullbox fallback）。
    /// 这样保证：(1) 即使 wcocr 升级字段名也不会丢文本；(2) 老版本无位置时仍能跑通 OcrResult。</summary>
    private static OcrResult ParseJsonResult(string json, int fallbackWidth, int fallbackHeight)
    {
        if (string.IsNullOrWhiteSpace(json)) return OcrResult.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 顶层 width/height 用于结果窗坐标缩放；缺失则用 PNG IHDR 的宽高
            int w = TryGetInt(root, "width") ?? fallbackWidth;
            int h = TryGetInt(root, "height") ?? fallbackHeight;

            var blocks = new List<OcrTextBlock>();
            CollectBlocks(root, blocks, w, h);

            var fullText = string.Join('\n', blocks.Select(b => b.Text)).Trim();
            return new OcrResult(blocks, fullText, w, h);
        }
        catch (JsonException)
        {
            // 不是合法 JSON：把整串当一个 block，用整张图框作为占位 — 比丢失内容好
            var trimmed = json.Trim();
            if (trimmed.Length == 0) return OcrResult.Empty;
            int w = fallbackWidth > 0 ? fallbackWidth : 1;
            int h = fallbackHeight > 0 ? fallbackHeight : 1;
            var box = OcrPolygon.FromRect(0, 0, w, h);
            return new OcrResult(new[] { new OcrTextBlock(trimmed, box, 1f) }, trimmed, w, h);
        }
    }

    /// <summary>递归遍历 JSON，把每个"含 text/txt 字段且文本非空"的对象视为一个 block。
    /// 在该对象内同时找位置字段，失败时回退到整张图框。</summary>
    private static void CollectBlocks(JsonElement el, List<OcrTextBlock> sink, int imgW, int imgH)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var text = TryGetString(el, "text") ?? TryGetString(el, "txt");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var box = ExtractPolygon(el, imgW, imgH);
                    var conf = TryGetFloat(el, "rate") ?? TryGetFloat(el, "score") ?? TryGetFloat(el, "confidence") ?? 1f;
                    sink.Add(new OcrTextBlock(text.Trim(), box, conf));
                    // 同对象不再下钻：text 字段所在层级即一个 block
                    return;
                }
                foreach (var prop in el.EnumerateObject())
                    CollectBlocks(prop.Value, sink, imgW, imgH);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    CollectBlocks(item, sink, imgW, imgH);
                break;
        }
    }

    /// <summary>从 block 对象提取 polygon，优先级：
    /// (1) pos 四点数组（最精确，倾斜文字真四边形）
    /// (2) rect 子对象 + left/top/right/bottom
    /// (3) 平铺 left/top/right/bottom
    /// (4) 整张图（绝对回退；至少能在结果窗显示但等于"全图选中"）</summary>
    private static OcrPolygon ExtractPolygon(JsonElement obj, int imgW, int imgH)
    {
        // (1) pos: [[x,y]*4]
        if (obj.TryGetProperty("pos", out var pos) && pos.ValueKind == JsonValueKind.Array)
        {
            var pts = new (float X, float Y)[4];
            int i = 0;
            foreach (var p in pos.EnumerateArray())
            {
                if (i >= 4) break;
                if (p.ValueKind == JsonValueKind.Array)
                {
                    int j = 0; float x = 0, y = 0;
                    foreach (var v in p.EnumerateArray())
                    {
                        if (j == 0) x = (float)v.GetDouble();
                        else if (j == 1) y = (float)v.GetDouble();
                        j++;
                    }
                    pts[i++] = (x, y);
                }
            }
            if (i == 4)
                return new OcrPolygon(pts[0].X, pts[0].Y, pts[1].X, pts[1].Y, pts[2].X, pts[2].Y, pts[3].X, pts[3].Y);
        }

        // (2) rect: { left, top, right, bottom }
        if (obj.TryGetProperty("rect", out var rect) && rect.ValueKind == JsonValueKind.Object)
        {
            var l = TryGetFloat(rect, "left");
            var t = TryGetFloat(rect, "top");
            var r = TryGetFloat(rect, "right");
            var b = TryGetFloat(rect, "bottom");
            if (l.HasValue && t.HasValue && r.HasValue && b.HasValue)
                return OcrPolygon.FromRect(l.Value, t.Value, r.Value, b.Value);
        }

        // (3) 平铺 left/top/right/bottom
        var fl = TryGetFloat(obj, "left");
        var ft = TryGetFloat(obj, "top");
        var fr = TryGetFloat(obj, "right");
        var fb = TryGetFloat(obj, "bottom");
        if (fl.HasValue && ft.HasValue && fr.HasValue && fb.HasValue)
            return OcrPolygon.FromRect(fl.Value, ft.Value, fr.Value, fb.Value);

        // (4) 不知道位置：整张图
        return OcrPolygon.FromRect(0, 0, imgW, imgH);
    }

    private static string? TryGetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static float? TryGetFloat(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return (float)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && float.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    /// <summary>从 PNG 文件头读出 width/height，不解码像素。
    /// PNG 签名 8 字节 + IHDR 长度 4 字节 + "IHDR" 4 字节 + width 4 字节 BE + height 4 字节 BE。
    /// 即 offset 16..19 = width, 20..23 = height。
    /// 不抛异常：失败返回 (0,0)，让上层用 polygon 兜底的整图框。</summary>
    private static (int W, int H) TryReadPngSize(byte[] png)
    {
        if (png is null || png.Length < 24) return (0, 0);
        // 没必要校验签名：长度够 24 字节就直接读 IHDR
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        if (w <= 0 || h <= 0 || w > 65536 || h > 65536) return (0, 0);
        return (w, h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 只在确实跑过 wechat_ocr 时才 stop：避免没用过的进程里反而触发 wcocr.dll 加载
        if (_hasInvokedNative)
        {
            try { WeChatNative.stop_ocr(); }
            catch (Exception ex) { _log.Debug("ocr wechat stop swallowed", ("err", ex.Message)); }
        }
        try { _gate.Dispose(); } catch { }
    }

    /// <summary>容器类持有 SetResultDelegate 回调返回的 JSON。
    /// 单独建类是为了让 GC.KeepAlive 显式保活回调与持有者，避免 native 跨边界写入时被回收。</summary>
    private sealed class ResultHolder
    {
        public string JsonResult { get; private set; } = "";

        public void SetResult(IntPtr resultUtf8CStr)
        {
            if (resultUtf8CStr == IntPtr.Zero) { JsonResult = ""; return; }
            // 数完 \0 拷出 UTF-8 字节，再解码字符串
            int length = 0;
            while (System.Runtime.InteropServices.Marshal.ReadByte(resultUtf8CStr, length) != 0)
                length++;
            if (length == 0) { JsonResult = ""; return; }
            var buf = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(resultUtf8CStr, buf, 0, length);
            JsonResult = Encoding.UTF8.GetString(buf);
        }
    }
}
