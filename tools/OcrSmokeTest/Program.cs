using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.LocalV5;
using Sdcb.PaddleOCR.Models.Shared;
using YamlDotNet.RepresentationModel;

if (args.Length == 0)
{
    Console.WriteLine("usage: OcrSmokeTest <image-path> [norot]");
    return 1;
}
var path = args[0];
var allowRotate = !(args.Length > 1 && args[1] == "norot");

FullOcrModel model = new(new V5Det("mobile-zh-det"), new V5Rec("mobile-zh-rec"));
Console.WriteLine($"allowRotate = {allowRotate}");

var sw = Stopwatch.StartNew();
using var all = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
{
    AllowRotateDetection = allowRotate,
    Enable180Classification = false,
};
sw.Stop();
Console.WriteLine($"engine init: {sw.ElapsedMilliseconds} ms");

using var src = Cv2.ImRead(path, ImreadModes.Color);
Console.WriteLine($"input: {src.Width}x{src.Height} channels={src.Channels()}");
if (src.Empty()) { Console.WriteLine("EMPTY image"); return 2; }

sw.Restart();
var result = all.Run(src);
sw.Stop();
Console.WriteLine($"run: {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"regions: {result.Regions?.Length ?? 0}");
Console.WriteLine($"text:    [{result.Text}]");
return 0;

internal sealed class V5Det : DetectionModel
{
    private readonly string _name;
    public V5Det(string name) : base(ModelVersion.V5) => _name = name;
    public override PaddleConfig CreateConfig() => Embedded.LoadConfig(_name);
}

internal sealed class V5Rec : RecognizationModel
{
    private readonly string _name;
    public IReadOnlyList<string> Labels { get; }
    public V5Rec(string name) : base(ModelVersion.V5)
    {
        _name = name;
        Labels = Embedded.LoadLabels(name);
    }
    public override PaddleConfig CreateConfig() => Embedded.LoadConfig(_name);
    public override string GetLabelByIndex(int i) => GetLabelByIndex(i, Labels);
}

internal static class Embedded
{
    private static readonly Assembly s_asm = typeof(KnownModels).Assembly;
    private static readonly string s_prefix = typeof(KnownModels).Namespace!;
    private static string K(string n, string s) => $"{s_prefix}.models.{SharedUtils.EmbeddedResourceTransform(n)}.inference.{s}";
    public static PaddleConfig LoadConfig(string name) => PaddleConfig.FromMemoryModel(
        SharedUtils.ReadResourceAsBytes(K(name, "json"), s_asm),
        SharedUtils.ReadResourceAsBytes(K(name, "pdiparams"), s_asm));
    public static IReadOnlyList<string> LoadLabels(string name)
    {
        using var s = s_asm.GetManifestResourceStream(K(name, "yml"))!;
        var y = new YamlStream();
        y.Load(new StreamReader(s));
        return ((YamlSequenceNode)y.Documents[0].RootNode["PostProcess"]["character_dict"])
            .Select(x => ((YamlScalarNode)x).Value!).ToList();
    }
}
