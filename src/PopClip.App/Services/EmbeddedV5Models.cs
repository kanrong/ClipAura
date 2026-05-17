using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.LocalV5;
using Sdcb.PaddleOCR.Models.Shared;
using YamlDotNet.RepresentationModel;

namespace PopClip.App.Services;

/// <summary>从 Sdcb.PaddleOCR.Models.LocalV5 嵌入资源里直接构造 PP-OCRv5 模型，
/// 绕开 Sdcb.PaddleOCR.Models.Local 主包对 V3 + V4 的传递依赖（这两个子包加起来 ~250 MB native）。
///
/// 仅支持 ChineseV5 (mobile-zh-det + mobile-zh-rec)：UI 截图 OCR 场景中英文双语已覆盖。
/// 如果未来需要其它语言（日韩多语等），加新的 EmbeddedV5RecognizationModel 实例即可。
///
/// 资源 key 复刻自 Sdcb.PaddleOCR.Models.Local.Details.Utils.LocalModel：
///   Sdcb.PaddleOCR.Models.LocalV5.models.{name_with_underscore}.inference.json     (program)
///   Sdcb.PaddleOCR.Models.LocalV5.models.{name_with_underscore}.inference.pdiparams (params)
///   Sdcb.PaddleOCR.Models.LocalV5.models.{name_with_underscore}.inference.yml      (字典，仅 recognizer)
///
/// EmbeddedResourceTransform 把 '-' 替换为 '_'，跟 SharedUtils 行为一致</summary>
internal static class EmbeddedV5Models
{
    // 注意：这两个字段必须排在 ChineseFullModel 之前。
    // C# 静态字段按文件文本顺序初始化；ChineseFullModel 通过构造函数立刻调用 LoadLabels/LoadConfig，
    // 而它们依赖 s_v5Assembly / s_v5Prefix。如果顺序反了，那两个字段还是默认 null，
    // 会引出 NullReferenceException
    private static readonly Assembly s_v5Assembly = typeof(KnownModels).Assembly;
    private static readonly string s_v5Prefix = typeof(KnownModels).Namespace!;

    /// <summary>PP-OCRv5 中文 + 英文的 Detection + Recognition 组合。
    /// FullOcrModel 走不带 classifier 的构造：classifier 仅用于 180° 翻转分类，
    /// OcrService 关闭了 Enable180Classification，不需要该模型。
    /// 用 Lazy 保证字段初始化已完成后才构造 model，避免静态初始化顺序陷阱。</summary>
    private static readonly Lazy<FullOcrModel> s_chineseFull = new(() => new FullOcrModel(
        new EmbeddedV5DetectionModel("mobile-zh-det"),
        new EmbeddedV5RecognizationModel("mobile-zh-rec")));

    public static FullOcrModel ChineseFullModel => s_chineseFull.Value;

    private static string ResourceKey(string name, string suffix)
        => $"{s_v5Prefix}.models.{SharedUtils.EmbeddedResourceTransform(name)}.inference.{suffix}";

    internal static PaddleConfig LoadConfig(string name)
    {
        byte[] program = SharedUtils.ReadResourceAsBytes(ResourceKey(name, "json"), s_v5Assembly);
        byte[] parameters = SharedUtils.ReadResourceAsBytes(ResourceKey(name, "pdiparams"), s_v5Assembly);
        return PaddleConfig.FromMemoryModel(program, parameters);
    }

    /// <summary>从 V5 模型旁边的 inference.yml 里读字典：YAML 路径是 PostProcess.character_dict[]。
    /// 解析逻辑跟 Sdcb.PaddleOCR.Models.Local.Details.Utils.LoadV5Dicts 一致。</summary>
    internal static IReadOnlyList<string> LoadLabels(string name)
    {
        using Stream? stream = s_v5Assembly.GetManifestResourceStream(ResourceKey(name, "yml"))
            ?? throw new FileNotFoundException(
                $"PaddleOCR V5 字典资源缺失：{ResourceKey(name, "yml")}");
        var yaml = new YamlStream();
        yaml.Load(new StreamReader(stream));
        var dict = (YamlSequenceNode)yaml.Documents[0].RootNode["PostProcess"]["character_dict"];
        return dict.Select(x => ((YamlScalarNode)x).Value!).ToList();
    }
}

internal sealed class EmbeddedV5DetectionModel : DetectionModel
{
    private readonly string _name;
    public EmbeddedV5DetectionModel(string name) : base(ModelVersion.V5) => _name = name;
    public override PaddleConfig CreateConfig() => EmbeddedV5Models.LoadConfig(_name);
}

internal sealed class EmbeddedV5RecognizationModel : RecognizationModel
{
    private readonly string _name;
    public IReadOnlyList<string> Labels { get; }

    public EmbeddedV5RecognizationModel(string name) : base(ModelVersion.V5)
    {
        _name = name;
        Labels = EmbeddedV5Models.LoadLabels(name);
    }

    public override PaddleConfig CreateConfig() => EmbeddedV5Models.LoadConfig(_name);
    public override string GetLabelByIndex(int i) => GetLabelByIndex(i, Labels);
}
