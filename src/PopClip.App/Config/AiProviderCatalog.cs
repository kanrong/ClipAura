namespace PopClip.App.Config;

public sealed record AiProviderPresetInfo(
    AiProviderPreset Preset,
    string Label,
    string BaseUrl,
    string Model,
    string KeyBucket,
    string Description,
    bool IsCustom = false);

public static class AiProviderCatalog
{
    public const string DeepSeekKeyBucket = "deepseek";
    public const string OpenAiKeyBucket = "openai";
    public const string CustomKeyBucket = "custom";

    public static IReadOnlyList<AiProviderPresetInfo> All { get; } = new[]
    {
        new AiProviderPresetInfo(
            AiProviderPreset.DeepSeekV4Flash,
            "DeepSeek V4 Flash",
            "https://api.deepseek.com",
            "deepseek-v4-flash",
            DeepSeekKeyBucket,
            "速度和成本优先"),
        new AiProviderPresetInfo(
            AiProviderPreset.DeepSeekV4Pro,
            "DeepSeek V4 Pro",
            "https://api.deepseek.com",
            "deepseek-v4-pro",
            DeepSeekKeyBucket,
            "质量优先"),
        new AiProviderPresetInfo(
            AiProviderPreset.OpenAiFast,
            "OpenAI Fast",
            "https://api.openai.com/v1",
            "gpt-5.4-mini",
            OpenAiKeyBucket,
            "速度和成本优先"),
        new AiProviderPresetInfo(
            AiProviderPreset.OpenAiPro,
            "OpenAI Pro",
            "https://api.openai.com/v1",
            "gpt-5.5",
            OpenAiKeyBucket,
            "质量优先"),
        new AiProviderPresetInfo(
            AiProviderPreset.Custom,
            "Custom",
            "",
            "",
            CustomKeyBucket,
            "自定义 OpenAI-compatible 接口",
            IsCustom: true),
    };

    public static AiProviderPresetInfo Get(AiProviderPreset preset)
        => All.FirstOrDefault(x => x.Preset == preset)
           ?? All[0];

    public static AiProviderPreset Parse(string? value)
        => Enum.TryParse<AiProviderPreset>(value, out var preset)
            ? preset
            : AiProviderPreset.DeepSeekV4Flash;

    public static string GetProtectedKey(AppSettings settings, string bucket)
        => bucket switch
        {
            OpenAiKeyBucket => settings.AiOpenAiApiKeyProtected,
            CustomKeyBucket => settings.AiCustomApiKeyProtected,
            _ => settings.AiDeepSeekApiKeyProtected,
        };

    public static void SetProtectedKey(AppSettings settings, string bucket, string protectedKey)
    {
        switch (bucket)
        {
            case OpenAiKeyBucket:
                settings.AiOpenAiApiKeyProtected = protectedKey;
                break;
            case CustomKeyBucket:
                settings.AiCustomApiKeyProtected = protectedKey;
                break;
            default:
                settings.AiDeepSeekApiKeyProtected = protectedKey;
                break;
        }
    }
}
