using System.Text.Json;
using System.Text.RegularExpressions;
using PopClip.App.Ocr;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

/// <summary>隐私扫描命中点。命中区域以 OCR 多边形的最小外接矩形描述（像素坐标，原点左上、Y 向下），
/// 与 OcrPolygon.AABB 一致。Coordinator 会把它转换成预览窗 DIP 坐标，再用红色虚线框提示给用户。
///
/// MatchText 只用于日志 / tooltip：默认 UI 不展示原文（避免在隐私提示里又把隐私重复一次）。
/// RuleId 是规则编号 / 自定义规则 name，便于"按规则忽略"等高级路径后续叠加</summary>
internal readonly record struct PrivacyHit(
    string RuleId,
    string MatchText,
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
}

/// <summary>用户自定义隐私规则。从 settings.PrivacyCustomRulesJson 读取的 JSON 数组。
/// 仅 Enabled=true 的规则会进入扫描；regex 编译失败的整条规则会被跳过（不阻断其他规则）</summary>
internal sealed record PrivacyCustomRule(string Name, string Regex, bool Enabled);

/// <summary>对 OCR 结果中的文本逐行做正则匹配，把命中位置回填为 OCR 多边形的 AABB。
///
/// 设计取舍：
/// - 直接对 block.Text 做匹配，命中后整段 block 的 polygon 作为打码区域；
///   这样不需要做"匹配字符 → 字符屏幕位置"的精细映射，对 OCR 多 block 命中率足够。
///   代价是会比"逐字符精确打码"稍微多打一点（命中 block 全部覆盖），
///   隐私场景这反而更安全（部分手机号被精确打码、剩余 4 位仍可见才是问题）。
/// - 规则集刻意做小（7~8 条），不试图覆盖所有泄漏场景，避免误打码扰民；
///   高级用户通过自定义规则补充，规则在设置面板编辑。
/// - 不做 OCR 召回率提升（如同时跑多个 provider）：M4 只解决"看得见的隐私文字"，
///   被 OCR 漏掉的字段是 OCR 后端能力问题，不在隐私扫描职责内。</summary>
internal sealed class PrivacyScanner
{
    private readonly ILog _log;
    private readonly List<CompiledRule> _builtin;
    private readonly Func<AppSettingsSnapshot> _getSnapshot;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly RegexOptions DefaultOptions =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private sealed record CompiledRule(string Id, Regex Regex, bool ApplyLuhn);

    /// <summary>把扫描时需要的 settings 字段冷冻成一份只读快照，避免扫描运行期间被并发改动</summary>
    public readonly record struct AppSettingsSnapshot(
        bool Enabled,
        bool BuiltinPhone,
        bool BuiltinIdCard,
        bool BuiltinEmail,
        bool BuiltinBankCard,
        bool BuiltinApiKey,
        bool BuiltinJwt,
        bool BuiltinWindowsUser,
        string CustomRulesJson);

    public PrivacyScanner(ILog log, Func<AppSettingsSnapshot> getSnapshot)
    {
        _log = log;
        _getSnapshot = getSnapshot;
        _builtin = BuildBuiltins();
    }

    /// <summary>对一份 OCR 结果做隐私扫描；返回所有命中点。
    /// 同一个 block 命中多次只算一次（按 polygon 去重），避免同一手机号被命中多个规则后画多个框</summary>
    public IReadOnlyList<PrivacyHit> Scan(OcrResult result)
    {
        if (result.Blocks.Count == 0) return Array.Empty<PrivacyHit>();

        var snap = _getSnapshot();
        if (!snap.Enabled) return Array.Empty<PrivacyHit>();

        var rules = SelectActiveRules(snap);
        if (rules.Count == 0) return Array.Empty<PrivacyHit>();

        // (left,top,right,bottom) 元组做命中去重 key：同一 block 不同规则只画一个红框
        var uniques = new HashSet<(int, int, int, int)>();
        var hits = new List<PrivacyHit>();
        foreach (var block in result.Blocks)
        {
            if (string.IsNullOrEmpty(block.Text)) continue;
            foreach (var rule in rules)
            {
                Match match;
                try { match = rule.Regex.Match(block.Text); }
                catch (RegexMatchTimeoutException ex)
                {
                    _log.Warn("privacy regex timeout", ("rule", rule.Id), ("err", ex.Message));
                    continue;
                }
                if (!match.Success) continue;

                if (rule.ApplyLuhn && !PassesLuhn(match.Value)) continue;

                var (l, t, r, b) = block.Box.AABB();
                var key = ((int)Math.Floor(l), (int)Math.Floor(t), (int)Math.Ceiling(r), (int)Math.Ceiling(b));
                if (!uniques.Add(key)) break;

                hits.Add(new PrivacyHit(
                    rule.Id, match.Value,
                    key.Item1, key.Item2, key.Item3, key.Item4));
                break;
            }
        }
        return hits;
    }

    /// <summary>把当前生效的内置规则 + 用户自定义规则合并成最终扫描列表。
    /// 自定义规则 JSON 解析失败仅记日志，不抛异常；单条 regex 编译失败也只跳过该条</summary>
    private List<CompiledRule> SelectActiveRules(AppSettingsSnapshot snap)
    {
        var list = new List<CompiledRule>();
        foreach (var r in _builtin)
        {
            if (!IsBuiltinEnabled(r.Id, snap)) continue;
            list.Add(r);
        }

        if (!string.IsNullOrWhiteSpace(snap.CustomRulesJson))
        {
            List<PrivacyCustomRule>? rules = null;
            try
            {
                rules = JsonSerializer.Deserialize<List<PrivacyCustomRule>>(
                    snap.CustomRulesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _log.Warn("privacy custom rules invalid json", ("err", ex.Message));
            }

            if (rules is not null)
            {
                foreach (var c in rules)
                {
                    if (c is null || !c.Enabled || string.IsNullOrWhiteSpace(c.Regex)) continue;
                    if (string.IsNullOrWhiteSpace(c.Name)) continue;
                    try
                    {
                        list.Add(new CompiledRule(
                            "custom:" + c.Name,
                            new Regex(c.Regex, DefaultOptions, RegexTimeout),
                            ApplyLuhn: false));
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("privacy custom rule compile failed",
                            ("name", c.Name), ("err", ex.Message));
                    }
                }
            }
        }

        return list;
    }

    private static bool IsBuiltinEnabled(string id, AppSettingsSnapshot snap) => id switch
    {
        BuiltinIds.Phone => snap.BuiltinPhone,
        BuiltinIds.IdCard => snap.BuiltinIdCard,
        BuiltinIds.Email => snap.BuiltinEmail,
        BuiltinIds.BankCard => snap.BuiltinBankCard,
        BuiltinIds.ApiKey => snap.BuiltinApiKey,
        BuiltinIds.Jwt => snap.BuiltinJwt,
        BuiltinIds.WindowsUser => snap.BuiltinWindowsUser,
        _ => false,
    };

    private static List<CompiledRule> BuildBuiltins()
    {
        return new List<CompiledRule>
        {
            new(BuiltinIds.Phone,
                new Regex(@"(?<!\d)1[3-9]\d{9}(?!\d)", DefaultOptions, RegexTimeout),
                ApplyLuhn: false),
            new(BuiltinIds.IdCard,
                new Regex(@"(?<![\dXx])\d{17}[\dXx](?![\dXx])", DefaultOptions, RegexTimeout),
                ApplyLuhn: false),
            new(BuiltinIds.Email,
                new Regex(@"[\w.+-]+@[\w-]+\.[\w.-]+", DefaultOptions, RegexTimeout),
                ApplyLuhn: false),
            // 银行卡：13~19 位连续数字。子串歧义（比如号码混在 OCR 行里）由 Luhn 校验过滤
            new(BuiltinIds.BankCard,
                new Regex(@"(?<!\d)\d{13,19}(?!\d)", DefaultOptions, RegexTimeout),
                ApplyLuhn: true),
            // API Key 常见前缀：sk-... / ghp_ / xoxb- 等；取交集让一条规则覆盖最常见三家
            new(BuiltinIds.ApiKey,
                new Regex(@"(sk-[A-Za-z0-9_-]{20,}|gh[ps]_[A-Za-z0-9]{20,}|xox[bps]-[A-Za-z0-9-]{10,})", DefaultOptions, RegexTimeout),
                ApplyLuhn: false),
            new(BuiltinIds.Jwt,
                new Regex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", DefaultOptions, RegexTimeout),
                ApplyLuhn: false),
            // Windows 用户名：C:\Users\xxx\ 路径中的用户名段
            new(BuiltinIds.WindowsUser,
                new Regex(@"[A-Za-z]:\\Users\\[^\\\s/:""<>|?*]+", DefaultOptions, RegexTimeout),
                ApplyLuhn: false),
        };
    }

    /// <summary>Luhn 校验：用于过滤银行卡误报。
    /// 算法：从右到左，偶数位 ×2（>9 则减 9），所有位求和，再 mod 10 == 0 通过</summary>
    private static bool PassesLuhn(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int sum = 0;
        bool doubleIt = false;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            char c = s[i];
            if (c < '0' || c > '9') return false;
            int n = c - '0';
            if (doubleIt)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }

    public static class BuiltinIds
    {
        public const string Phone = "builtin:phone";
        public const string IdCard = "builtin:idcard";
        public const string Email = "builtin:email";
        public const string BankCard = "builtin:bankcard";
        public const string ApiKey = "builtin:apikey";
        public const string Jwt = "builtin:jwt";
        public const string WindowsUser = "builtin:winuser";
    }
}
