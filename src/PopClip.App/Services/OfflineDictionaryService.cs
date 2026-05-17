using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PopClip.Core.Actions;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

internal sealed class OfflineDictionaryService : IOfflineDictionaryService
{
    private static readonly string[] CandidateDbNames =
    {
        "ecdict.sqlite",
        "ecdict.db",
        "stardict.sqlite",
        "stardict.db",
    };

    private static readonly Dictionary<string, string[]> IrregularLemmas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["am"] = new[] { "be" },
        ["is"] = new[] { "be" },
        ["are"] = new[] { "be" },
        ["was"] = new[] { "be" },
        ["were"] = new[] { "be" },
        ["been"] = new[] { "be" },
        ["being"] = new[] { "be" },
        ["has"] = new[] { "have" },
        ["had"] = new[] { "have" },
        ["does"] = new[] { "do" },
        ["did"] = new[] { "do" },
        ["done"] = new[] { "do" },
        ["went"] = new[] { "go" },
        ["gone"] = new[] { "go" },
        ["better"] = new[] { "good", "well" },
        ["best"] = new[] { "good", "well" },
        ["worse"] = new[] { "bad" },
        ["worst"] = new[] { "bad" },
        ["children"] = new[] { "child" },
        ["men"] = new[] { "man" },
        ["women"] = new[] { "woman" },
        ["people"] = new[] { "person" },
        ["teeth"] = new[] { "tooth" },
        ["feet"] = new[] { "foot" },
        ["mice"] = new[] { "mouse" },
    };

    private readonly ILog _log;
    private readonly string _dictDir;
    private string? _cachedDbPath;
    private DateTime _lastProbeUtc = DateTime.MinValue;

    public OfflineDictionaryService(ILog log)
    {
        _log = log;
        _dictDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "dict", "ecdict");
    }

    public bool IsAvailable => ResolveDbPath() is not null;

    public string? UnavailableReason
        => IsAvailable ? null : $"缺少离线词库。请运行 tools\\import_ecdict.py 生成 {_dictDir}\\ecdict.sqlite";

    public IReadOnlyList<DictionaryLookupResult> Lookup(string query, int maxResults = 5)
    {
        var dbPath = ResolveDbPath();
        if (dbPath is null) return Array.Empty<DictionaryLookupResult>();

        var candidates = BuildLookupCandidates(query).Take(24).ToList();
        if (candidates.Count == 0) return Array.Empty<DictionaryLookupResult>();

        var results = new List<DictionaryLookupResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var con = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            con.Open();

            foreach (var candidate in candidates)
            {
                foreach (var row in QueryExact(con, candidate.Query, candidate.MatchedFrom))
                {
                    if (seen.Add(row.Word)) results.Add(row);
                    if (results.Count >= maxResults) return results;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("offline dictionary lookup failed", ("err", ex.Message), ("query", query));
            return Array.Empty<DictionaryLookupResult>();
        }

        return results;
    }

    private string? ResolveDbPath()
    {
        var now = DateTime.UtcNow;
        if (_cachedDbPath is not null && File.Exists(_cachedDbPath)) return _cachedDbPath;
        if ((now - _lastProbeUtc).TotalSeconds < 5) return null;
        _lastProbeUtc = now;

        foreach (var name in CandidateDbNames)
        {
            var path = Path.Combine(_dictDir, name);
            if (File.Exists(path))
            {
                _cachedDbPath = path;
                _log.Info("offline dictionary ready", ("path", path));
                return path;
            }
        }

        _cachedDbPath = null;
        return null;
    }

    private static IEnumerable<DictionaryLookupResult> QueryExact(SqliteConnection con, string word, string? matchedFrom)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT word, phonetic, translation, definition, pos, exchange, frq, bnc, collins, oxford, tag
FROM stardict
WHERE lower(word) = lower($word)
LIMIT 4";
        cmd.Parameters.AddWithValue("$word", word);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var found = ReadString(reader, 0) ?? word;
            yield return new DictionaryLookupResult(
                Word: found,
                MatchedFrom: string.Equals(found, matchedFrom, StringComparison.OrdinalIgnoreCase) ? null : matchedFrom,
                Phonetic: ReadString(reader, 1),
                Translation: ReadString(reader, 2),
                Definition: ReadString(reader, 3),
                PartOfSpeech: ReadString(reader, 4),
                Exchange: ReadString(reader, 5),
                Frequency: ReadBestFrequency(reader),
                Collins: ReadInt(reader, 8),
                Oxford: ReadInt(reader, 9),
                Tags: ReadString(reader, 10),
                Bnc: ReadInt(reader, 7),
                Frq: ReadInt(reader, 6));
        }
    }

    private static string? ReadString(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static int? ReadInt(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        try
        {
            var value = reader.GetInt32(index);
            return value > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadBestFrequency(SqliteDataReader reader)
    {
        for (var i = 6; i <= 8; i++)
        {
            if (reader.IsDBNull(i)) continue;
            try
            {
                var value = reader.GetInt32(i);
                if (value > 0) return value;
            }
            catch { }
        }

        return null;
    }

    private static IEnumerable<(string Query, string? MatchedFrom)> BuildLookupCandidates(string raw)
    {
        var cleaned = NormalizeQuery(raw);
        if (cleaned.Length == 0) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ExpandCandidates(cleaned))
        {
            if (candidate.Length == 0 || !seen.Add(candidate)) continue;
            yield return (candidate, cleaned);
        }
    }

    private static IEnumerable<string> ExpandCandidates(string query)
    {
        yield return query;
        var lower = query.ToLowerInvariant();
        if (!string.Equals(lower, query, StringComparison.Ordinal)) yield return lower;

        if (IrregularLemmas.TryGetValue(lower, out var lemmas))
        {
            foreach (var lemma in lemmas) yield return lemma;
        }

        if (lower.EndsWith("'s", StringComparison.Ordinal) && lower.Length > 2)
            yield return lower[..^2];

        if (lower.Contains('-', StringComparison.Ordinal))
        {
            yield return lower.Replace("-", " ", StringComparison.Ordinal);
            yield return lower.Replace("-", "", StringComparison.Ordinal);
            foreach (var part in lower.Split('-', StringSplitOptions.RemoveEmptyEntries))
                yield return part;
        }

        if (lower.EndsWith("ies", StringComparison.Ordinal) && lower.Length > 4)
            yield return lower[..^3] + "y";
        if (lower.EndsWith("ves", StringComparison.Ordinal) && lower.Length > 4)
        {
            yield return lower[..^3] + "f";
            yield return lower[..^3] + "fe";
        }
        if (lower.EndsWith("ing", StringComparison.Ordinal) && lower.Length > 5)
        {
            var stem = lower[..^3];
            yield return stem;
            yield return stem + "e";
            if (stem.Length > 2 && stem[^1] == stem[^2]) yield return stem[..^1];
        }
        if (lower.EndsWith("ed", StringComparison.Ordinal) && lower.Length > 4)
        {
            var stem = lower[..^2];
            yield return stem;
            yield return stem + "e";
            if (stem.Length > 2 && stem[^1] == stem[^2]) yield return stem[..^1];
        }
        if (lower.EndsWith("es", StringComparison.Ordinal) && lower.Length > 4)
            yield return lower[..^2];
        if (lower.EndsWith('s') && lower.Length > 3)
            yield return lower[..^1];
    }

    private static string NormalizeQuery(string raw)
    {
        var text = (raw ?? "").Trim();
        text = Regex.Replace(text, @"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$", "");
        text = Regex.Replace(text, @"\s+", " ");
        return text;
    }
}
