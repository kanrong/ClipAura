using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using PopClip.Core.Logging;

namespace PopClip.App.Ocr;

/// <summary>把 plugins/ocr/{provider}/runtime/ 里的 OCR plugin dll 通过隔离的 AssemblyLoadContext 加载，
/// 用 AssemblyDependencyResolver 解析 plugin 自带的 .deps.json 找到所有 transitive 依赖与 native dll。
///
/// 设计要点：
/// (1) plugin 与主程序的 AssemblyLoadContext 隔离 → plugin 引用 Microsoft.ML.OnnxRuntime / SkiaSharp 等
///     体积大的依赖，不污染主程序 ALC；
/// (2) IOcrProvider 与 ILog 接口由 PluginLoadContext.Load 主动返回 null 让 Default ALC 共享，
///     这样主程序能直接把 plugin 实例 cast 成 IOcrProvider；
/// (3) native dll (onnxruntime.dll / libSkiaSharp.dll) 由 LoadUnmanagedDllFromPath 自动从
///     plugin 同目录加载，无需手写 NativeLibrary.SetDllImportResolver；
/// (4) plugin 入口 dll 命名约定 PopClip.App.OcrProvider.*.dll；目录里也只读这个模式的文件，
///     避免误把 transitive 依赖当成 plugin 入口。
///
/// 错误隔离：单个 plugin 加载失败（缺 dll / 类型不存在 / 构造抛异常）只记一条 warn，
/// 其它 plugin 与 WeChat 内置 provider 都不受影响。</summary>
public static class OcrPluginLoader
{
    /// <summary>plugin 入口 dll 命名约定。OcrPluginLoader 只查找 plugins/ocr/*/runtime/ 下匹配此模式的 dll，
    /// 然后用反射找其中 IOcrProvider 的实现类。</summary>
    private const string PluginEntryPattern = "PopClip.App.OcrProvider.*.dll";

    /// <summary>扫描 plugin 根目录下所有 OCR plugin 并加载它们的 IOcrProvider 实例。
    /// pluginRoot 通常是 AppDomain.CurrentDomain.BaseDirectory + "plugins"。
    /// 返回的列表可能为空（没有任何 plugin 时）— 调用方应允许这种情况，
    /// 让 WeChat 等内置 provider 仍能独立工作。</summary>
    public static IReadOnlyList<IOcrProvider> LoadAll(ILog log, string pluginRoot)
    {
        var providers = new List<IOcrProvider>();
        var ocrRoot = Path.Combine(pluginRoot, "ocr");
        if (!Directory.Exists(ocrRoot))
        {
            log.Debug("ocr plugin root absent, no plugins to load", ("path", ocrRoot));
            return providers;
        }

        foreach (var pluginDir in Directory.GetDirectories(ocrRoot))
        {
            var runtimeDir = Path.Combine(pluginDir, "runtime");
            if (!Directory.Exists(runtimeDir)) continue;

            var entryDlls = Directory.GetFiles(runtimeDir, PluginEntryPattern, SearchOption.TopDirectoryOnly);
            foreach (var dllPath in entryDlls)
            {
                try
                {
                    var instances = LoadPluginAssembly(log, dllPath);
                    providers.AddRange(instances);
                }
                catch (Exception ex)
                {
                    // 任何 plugin 加载失败都不能阻塞主程序启动
                    log.Warn("ocr plugin load failed",
                        ("dll", dllPath), ("err", ex.Message));
                }
            }
        }

        return providers;
    }

    private static IEnumerable<IOcrProvider> LoadPluginAssembly(ILog log, string entryDllPath)
    {
        var ctx = new PluginLoadContext(entryDllPath);
        var asm = ctx.LoadFromAssemblyPath(entryDllPath);

        var providerType = typeof(IOcrProvider);
        var implTypes = asm.GetTypes()
            .Where(t => providerType.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .ToList();

        if (implTypes.Count == 0)
        {
            log.Warn("ocr plugin has no IOcrProvider implementation",
                ("dll", entryDllPath), ("asm", asm.FullName));
            yield break;
        }

        foreach (var type in implTypes)
        {
            IOcrProvider? instance = null;
            try
            {
                // plugin 实现类约定唯一构造函数 (ILog log)；
                // 直接 Activator 实例化，让主程序能把日志重定向到 ConsoleLog
                instance = (IOcrProvider)Activator.CreateInstance(type, log)!;
                log.Info("ocr plugin loaded",
                    ("id", instance.Id),
                    ("type", type.FullName ?? type.Name),
                    ("dll", Path.GetFileName(entryDllPath)));
            }
            catch (Exception ex)
            {
                log.Warn("ocr plugin instantiate failed",
                    ("type", type.FullName ?? type.Name), ("err", ex.Message));
            }
            if (instance is not null) yield return instance;
        }
    }

    /// <summary>plugin 专用 AssemblyLoadContext。
    /// 通过 AssemblyDependencyResolver 解析 plugin 自带的 .deps.json 文件，
    /// 自动定位所有 managed 与 native 依赖。
    ///
    /// 关键策略：IsSharedAssembly 列表里的 assembly 返回 null 让 Default ALC 加载，
    /// 这样主程序与 plugin 看到的是同一份 IOcrProvider / ILog 类型，
    /// 类型 identity 一致才能跨 ALC cast。</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string entryAssemblyPath)
            // isCollectible=false：当前没有 plugin 热卸载需求，关掉省内存
            : base(name: Path.GetFileNameWithoutExtension(entryAssemblyPath), isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // 共享接口契约必须用主程序加载的版本，否则跨 ALC cast 会抛 InvalidCastException
            if (IsSharedAssembly(name)) return null;

            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // RapidOcrNet 内部 P/Invoke onnxruntime / SkiaSharp 时走这条路径，
            // _resolver 会查 plugin 自己的 runtimes/win-x64/native/ 子目录
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }

        /// <summary>这些 assembly 必须由 Default ALC (主程序) 加载，让主程序与 plugin 共享同一份类型。
        /// 不在此列表里的 assembly 走 plugin 自己的 .deps.json 解析路径。
        ///
        /// PopClip.Core 在列：plugin 用了 ILog 接口；
        /// PopClip.App.Ocr.Abstractions 在列：plugin 实现了 IOcrProvider。</summary>
        private static bool IsSharedAssembly(AssemblyName name) =>
            name.Name is "PopClip.App.Ocr.Abstractions"
                      or "PopClip.Core";
    }
}
