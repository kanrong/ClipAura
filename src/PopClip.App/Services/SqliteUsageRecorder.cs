using Microsoft.Data.Sqlite;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

/// <summary>把每次 AI 调用按"日 + provider + 模型"聚合到 usage_daily 表。
/// 选用 UPSERT 而非"读-改-写"，避免并发条件下重复计数</summary>
internal sealed class SqliteUsageRecorder : IUsageRecorder
{
    private readonly HistoryDatabase _db;
    private readonly ILog _log;

    public SqliteUsageRecorder(HistoryDatabase db, ILog log)
    {
        _db = db;
        _log = log;
    }

    public void Record(string provider, string model, int promptTokens, int completionTokens, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model)) return;
        using var conn = _db.Open();
        if (conn is null) return;
        try
        {
            using var cmd = conn.CreateCommand();
            // UPSERT：首次插入 calls=1，重复时累加
            cmd.CommandText = @"
                INSERT INTO usage_daily (day, provider, model, calls, prompt_tokens, completion_tokens, elapsed_ms)
                VALUES ($day, $provider, $model, 1, $pt, $ct, $ms)
                ON CONFLICT(day, provider, model) DO UPDATE SET
                    calls = calls + 1,
                    prompt_tokens = prompt_tokens + excluded.prompt_tokens,
                    completion_tokens = completion_tokens + excluded.completion_tokens,
                    elapsed_ms = elapsed_ms + excluded.elapsed_ms;
            ";
            cmd.Parameters.AddWithValue("$day", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$provider", provider ?? "");
            cmd.Parameters.AddWithValue("$model", model ?? "");
            cmd.Parameters.AddWithValue("$pt", promptTokens);
            cmd.Parameters.AddWithValue("$ct", completionTokens);
            cmd.Parameters.AddWithValue("$ms", (long)elapsed.TotalMilliseconds);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.Debug("usage record failed", ("err", ex.Message));
        }
    }

    public IReadOnlyList<UsageDay> Daily(int days)
    {
        using var conn = _db.Open();
        if (conn is null) return Array.Empty<UsageDay>();
        var list = new List<UsageDay>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT day, SUM(calls), SUM(prompt_tokens), SUM(completion_tokens)
                FROM usage_daily
                GROUP BY day
                ORDER BY day DESC
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(days, 1, 365));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!DateOnly.TryParse(reader.GetString(0), out var day)) continue;
                list.Add(new UsageDay(
                    day,
                    Convert.ToInt32(reader.GetValue(1)),
                    Convert.ToInt32(reader.GetValue(2)),
                    Convert.ToInt32(reader.GetValue(3))));
            }
        }
        catch (Exception ex)
        {
            _log.Debug("usage daily failed", ("err", ex.Message));
        }
        return list;
    }

    public UsageTotals Totals()
    {
        using var conn = _db.Open();
        if (conn is null) return new UsageTotals(0, 0, 0, TimeSpan.Zero);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT IFNULL(SUM(calls),0), IFNULL(SUM(prompt_tokens),0), IFNULL(SUM(completion_tokens),0), IFNULL(SUM(elapsed_ms),0) FROM usage_daily";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return new UsageTotals(0, 0, 0, TimeSpan.Zero);
            return new UsageTotals(
                Convert.ToInt32(reader.GetValue(0)),
                Convert.ToInt32(reader.GetValue(1)),
                Convert.ToInt32(reader.GetValue(2)),
                TimeSpan.FromMilliseconds(Convert.ToInt64(reader.GetValue(3))));
        }
        catch (Exception ex)
        {
            _log.Debug("usage totals failed", ("err", ex.Message));
            return new UsageTotals(0, 0, 0, TimeSpan.Zero);
        }
    }
}
