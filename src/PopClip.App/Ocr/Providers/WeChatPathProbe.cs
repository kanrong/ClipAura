using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PopClip.App.Ocr.Providers;

/// <summary>找出本机微信的 wechatDir 与 ocrExe 路径。
///
/// 探测优先级：
/// 1. plugins/ocr/wechat/paths.json 显式配置 (推荐用于非标准安装)；
/// 2. WeChat 4.0：%APPDATA%\Tencent\xwechat\XPlugin\plugins\WeChatOcr\{ver}\extracted\wxocr.dll
///    + C:\Program Files (x86)\Tencent\Weixin\ 或 C:\Program Files\Tencent\Weixin\
///      （根目录直接含 Weixin.exe，或者根目录下有版本号子目录含 Weixin.exe / WeChat.exe）；
/// 3. WeChat 3.x：%APPDATA%\Tencent\WeChat\XPlugin\Plugins\WeChatOCR\{ver}\extracted\WeChatOCR.exe
///    + 注册表 HKCU\Software\Tencent\WeChat\InstallPath 或 Program Files (x86)/Program Files 下的 WeChat 目录；
/// 4. 都没找到 → 返回 null，error 字符串包含完整探测 trace，方便用户对照具体路径排查。
///
/// 设计上故意不持久缓存（调用方可以加 ~10s 短缓存）：用户可能装/升级微信、改注册表，
/// 每次访问都重新探测一遍开销 < 10 ms，永远拿最新状态。</summary>
internal sealed record WeChatPaths(string OcrExe, string WechatDir, string Source);

internal static class WeChatPathProbe
{
    public static WeChatPaths? Probe(string pluginDir, out string? error)
    {
        var trace = new List<string>();

        // 1. 优先用户显式配置
        var explicitOne = TryReadExplicitConfig(pluginDir, trace, out var explicitError);
        if (explicitOne is not null) { error = null; return explicitOne; }
        if (explicitError is not null) { error = explicitError; return null; }

        // 2. WeChat 4.0 (xwechat / Weixin)
        var v4 = TryProbeWeChat4(trace);
        if (v4 is not null) { error = null; return v4; }

        // 3. WeChat 3.x (WeChat)
        var v3 = TryProbeWeChat3(trace);
        if (v3 is not null) { error = null; return v3; }

        var sb = new StringBuilder();
        sb.AppendLine("没找到本机微信安装。探测过程：");
        foreach (var t in trace) sb.Append("  • ").AppendLine(t);
        sb.AppendLine();
        sb.AppendLine($"修复方式：(1) 确认微信已安装并启动过一次（首启动会下载 WeChatOCR 插件到 %APPDATA%）；");
        sb.AppendLine($"或 (2) 在 {Path.Combine(pluginDir, "paths.json")} 显式指定 {{\"ocrExe\":\"...\",\"wechatDir\":\"...\"}}");
        error = sb.ToString().TrimEnd();
        return null;
    }

    private static WeChatPaths? TryReadExplicitConfig(string pluginDir, List<string> trace, out string? error)
    {
        var configPath = Path.Combine(pluginDir, "paths.json");
        if (!File.Exists(configPath))
        {
            trace.Add($"显式配置 {configPath} 不存在");
            error = null;
            return null;
        }
        try
        {
            using var fs = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(fs);
            var ocrExe = doc.RootElement.TryGetProperty("ocrExe", out var oe) ? oe.GetString() : null;
            var wechatDir = doc.RootElement.TryGetProperty("wechatDir", out var wd) ? wd.GetString() : null;
            if (string.IsNullOrWhiteSpace(ocrExe) || string.IsNullOrWhiteSpace(wechatDir))
            {
                error = $"{configPath} 缺少 ocrExe 或 wechatDir 字段";
                return null;
            }
            if (!File.Exists(ocrExe))
            {
                error = $"{configPath} 配置的 ocrExe 不存在：{ocrExe}";
                return null;
            }
            if (!Directory.Exists(wechatDir))
            {
                error = $"{configPath} 配置的 wechatDir 不存在：{wechatDir}";
                return null;
            }
            error = null;
            return new WeChatPaths(ocrExe, wechatDir, "paths.json");
        }
        catch (JsonException ex)
        {
            error = $"{configPath} JSON 解析失败：{ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            error = $"{configPath} 读取失败：{ex.Message}";
            return null;
        }
    }

    private static WeChatPaths? TryProbeWeChat4(List<string> trace)
    {
        // 插件目录约定：%APPDATA%\Tencent\xwechat\XPlugin\plugins\WeChatOcr\{ver}\extracted\wxocr.dll
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pluginsRoot = Path.Combine(appData, "Tencent", "xwechat", "XPlugin", "plugins", "WeChatOcr");
        if (!Directory.Exists(pluginsRoot))
        {
            trace.Add($"WeChat 4.0 插件目录不存在：{pluginsRoot}");
            return null;
        }

        var wxocr = PickLatestVersionFile(pluginsRoot, Path.Combine("extracted", "wxocr.dll"));
        if (wxocr is null)
        {
            trace.Add($"WeChat 4.0 插件目录存在但未找到 extracted/wxocr.dll：{pluginsRoot}");
            return null;
        }
        trace.Add($"WeChat 4.0 已找到 wxocr.dll：{wxocr}");

        // 关键改动：同时支持两种 wechatDir 布局
        // - 根目录直接含 Weixin.exe / WeChat.exe（绿色版 / 部分安装包）
        // - 根目录下有版本号子目录（如 4.0.0.26）含 Weixin.exe（官方主流布局）
        foreach (var root in EnumerateProgramFiles())
        {
            var weixinRoot = Path.Combine(root, "Tencent", "Weixin");
            if (!Directory.Exists(weixinRoot))
            {
                trace.Add($"WeChat 4.0 wechatDir 候选不存在：{weixinRoot}");
                continue;
            }
            var (dir, reason) = FindWeixinExeDir(weixinRoot);
            if (dir is not null)
            {
                trace.Add($"WeChat 4.0 已找到 wechatDir：{dir} ({reason})");
                return new WeChatPaths(wxocr, dir, "auto-wechat-4.0");
            }
            trace.Add($"WeChat 4.0 wechatDir 候选存在但未找到 Weixin.exe / WeChat.exe：{weixinRoot}");
        }
        return null;
    }

    private static WeChatPaths? TryProbeWeChat3(List<string> trace)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pluginsRoot = Path.Combine(appData, "Tencent", "WeChat", "XPlugin", "Plugins", "WeChatOCR");
        if (!Directory.Exists(pluginsRoot))
        {
            trace.Add($"WeChat 3.x 插件目录不存在：{pluginsRoot}");
            return null;
        }

        var wechatOcrExe = PickLatestVersionFile(pluginsRoot, Path.Combine("extracted", "WeChatOCR.exe"));
        if (wechatOcrExe is null)
        {
            trace.Add($"WeChat 3.x 插件目录存在但未找到 extracted/WeChatOCR.exe：{pluginsRoot}");
            return null;
        }
        trace.Add($"WeChat 3.x 已找到 WeChatOCR.exe：{wechatOcrExe}");

        // 注册表优先（如果用户改过安装路径，注册表是最权威的来源）
        var fromRegistry = TryReadRegistryString(Registry.CurrentUser, @"Software\Tencent\WeChat", "InstallPath")
                       ?? TryReadRegistryString(Registry.LocalMachine, @"Software\Tencent\WeChat", "InstallPath");
        if (fromRegistry is not null)
        {
            var (dir, reason) = FindWeixinExeDir(fromRegistry);
            if (dir is not null)
            {
                trace.Add($"WeChat 3.x 通过注册表找到 wechatDir：{dir} ({reason})");
                return new WeChatPaths(wechatOcrExe, dir, "auto-wechat-3.x-reg");
            }
            trace.Add($"WeChat 3.x 注册表 InstallPath={fromRegistry} 但未找到 WeChat.exe / Weixin.exe");
        }

        // 注册表没命中，按默认安装路径扫
        foreach (var root in EnumerateProgramFiles())
        {
            var wechatRoot = Path.Combine(root, "Tencent", "WeChat");
            if (!Directory.Exists(wechatRoot))
            {
                trace.Add($"WeChat 3.x wechatDir 候选不存在：{wechatRoot}");
                continue;
            }
            var (dir, reason) = FindWeixinExeDir(wechatRoot);
            if (dir is not null)
            {
                trace.Add($"WeChat 3.x 已找到 wechatDir：{dir} ({reason})");
                return new WeChatPaths(wechatOcrExe, dir, "auto-wechat-3.x");
            }
            trace.Add($"WeChat 3.x wechatDir 候选存在但未找到 WeChat.exe / Weixin.exe：{wechatRoot}");
        }
        return null;
    }

    /// <summary>从一个候选根目录里找出实际的 wechatDir。
    ///
    /// 关键认知：微信主流布局是双层结构 ——
    /// - 根目录（如 C:\Program Files (x86)\Tencent\Weixin\）：只放 Weixin.exe / Uninstall.exe 这种 launcher；
    /// - 版本子目录（如 4.1.9.30\ 或 [3.9.8.25]\）：放真正的 Weixin.dll / WeChatWin.dll 等运行时 DLL。
    ///
    /// wcocr.dll 需要的 wechatDir 是 ** 含真正运行时 DLL 的版本子目录 **，
    /// 传根目录会让 wcocr 找不到 protobuf / OCR 子模块的依赖。
    ///
    /// 探测策略：
    /// (1) 先按字典序倒排扫子目录，第一个含核心 DLL 的就是 wechatDir；
    /// (2) 没有版本子目录（绿色版 / 单版本极简布局）才退到根目录；
    /// (3) 都不命中返回 (null, null) 由调用方写 trace。</summary>
    private static (string? Dir, string? Reason) FindWeixinExeDir(string root)
    {
        if (!Directory.Exists(root)) return (null, null);

        // 优先扫版本子目录（主流布局）。这一步前置很关键：根目录虽然也可能有 Weixin.exe（launcher），
        // 但传给 wcocr 会导致找不到运行时 DLL，必须传版本子目录
        try
        {
            var sortedSubs = Directory.GetDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var sub in sortedSubs)
            {
                if (LooksLikeWeixinRuntimeDir(sub))
                    return (sub, $"版本子目录 {Path.GetFileName(sub)}");
            }
        }
        catch { /* 权限不足 / 路径过深，让外层 trace */ }

        // 版本子目录都不像运行时目录：退到根目录（覆盖绿色版 / 早期单层布局）
        if (LooksLikeWeixinRuntimeDir(root)) return (root, "根目录直接含运行时 DLL");
        return (null, null);
    }

    /// <summary>判断一个目录是不是微信"运行时目录"（即真正的 wechatDir）。
    ///
    /// 判定依据：含 Weixin.dll / WeChat.dll / WeChatWin.dll 任一核心运行时 DLL。
    /// 仅靠 Weixin.exe / WeChat.exe 不够 —— launcher 在根目录也有 .exe，但缺核心 DLL。
    ///
    /// 容错：少数早期 / 极简布局把 exe 与 dll 都放同一目录，所以补一个 exe fallback。</summary>
    private static bool LooksLikeWeixinRuntimeDir(string dir)
    {
        try
        {
            // 核心 DLL：版本子目录必有其一
            if (File.Exists(Path.Combine(dir, "Weixin.dll"))) return true;
            if (File.Exists(Path.Combine(dir, "WeChat.dll"))) return true;
            if (File.Exists(Path.Combine(dir, "WeChatWin.dll"))) return true;
            // 极简单层布局：根目录直接含 exe + 一堆 dll，没有版本子目录
            // 此时随便取一个 launcher 也算（dll 与 exe 同处一目录，wcocr 能找到）
            bool hasExe = File.Exists(Path.Combine(dir, "Weixin.exe"))
                       || File.Exists(Path.Combine(dir, "WeChat.exe"));
            if (!hasExe) return false;
            // 单层布局应该有 ≥5 个 dll；只有 1-2 个 dll 的目录大概率是 launcher 根目录
            return Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).Take(5).Count() >= 5;
        }
        catch { return false; }
    }

    /// <summary>{root}\{version}\{suffix} 形式，挑 version 字典序最大的那个。
    /// version 目录名 wcocr 上游历史是纯数字（如 "7061"）或四段版本号（如 "8011"），
    /// 字符串倒排足以选到最新。</summary>
    private static string? PickLatestVersionFile(string root, string suffix)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(d, suffix);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* 权限不足 / 路径过深 */ }
        return null;
    }

    private static IEnumerable<string> EnumerateProgramFiles()
    {
        // 64-bit 进程读 ProgramFiles → C:\Program Files；ProgramFilesX86 → C:\Program Files (x86)。
        // 实测微信 4.0 安装时即使是 64-bit 系统也常被放进 Program Files (x86)，所以两个都要扫。
        // ProgramW6432 env var：32-bit 进程绕过 WOW64 重定向去读 64-bit Program Files；
        // 这里 ClipAura 是 64-bit 所以等于 ProgramFiles，但保留作为兜底
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        })
        {
            if (!string.IsNullOrEmpty(v) && seen.Add(v)) yield return v;
        }
    }

    private static string? TryReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }
}
