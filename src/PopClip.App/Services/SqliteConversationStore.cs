using System.Text.Json;
using Microsoft.Data.Sqlite;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

/// <summary>用 SQLite 把对话写入 history.db。
/// 消息以 JSON 形式整体保存，列只索引最常用的 created_at 与 message_count，便于"最近对话"列表与搜索</summary>
internal sealed class SqliteConversationStore : IConversationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HistoryDatabase _db;
    private readonly ILog _log;

    public SqliteConversationStore(HistoryDatabase db, ILog log)
    {
        _db = db;
        _log = log;
    }

    public void Save(ConversationRecord record)
    {
        using var conn = _db.Open();
        if (conn is null) return;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO conversations (id, title, reference_text, model, provider,
                    messages_json, prompt_tokens, completion_tokens, created_at, message_count)
                VALUES ($id, $title, $ref, $model, $provider, $msgs, $pt, $ct, $created, $count);
            ";
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$title", record.Title);
            cmd.Parameters.AddWithValue("$ref", record.ReferenceText);
            cmd.Parameters.AddWithValue("$model", record.Model);
            cmd.Parameters.AddWithValue("$provider", record.Provider);
            cmd.Parameters.AddWithValue("$msgs", SerializeMessages(record.Messages));
            cmd.Parameters.AddWithValue("$pt", record.PromptTokens);
            cmd.Parameters.AddWithValue("$ct", record.CompletionTokens);
            cmd.Parameters.AddWithValue("$created", new DateTimeOffset(record.CreatedAtUtc, TimeSpan.Zero).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$count", record.Messages.Count);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.Warn("conversation save failed", ("err", ex.Message), ("id", record.Id));
        }
    }

    public IReadOnlyList<ConversationSummary> Recent(int limit)
    {
        using var conn = _db.Open();
        if (conn is null) return Array.Empty<ConversationSummary>();
        var list = new List<ConversationSummary>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, title, model, message_count, created_at FROM conversations ORDER BY created_at DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ConversationSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).UtcDateTime));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("conversation recent failed", ("err", ex.Message));
        }
        return list;
    }

    public ConversationRecord? Load(string id)
    {
        using var conn = _db.Open();
        if (conn is null) return null;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, title, reference_text, model, provider, messages_json,
                       prompt_tokens, completion_tokens, created_at
                FROM conversations WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new ConversationRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DeserializeMessages(reader.GetString(5)),
                reader.GetInt32(6),
                reader.GetInt32(7),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)).UtcDateTime);
        }
        catch (Exception ex)
        {
            _log.Warn("conversation load failed", ("err", ex.Message), ("id", id));
            return null;
        }
    }

    public bool Delete(string id)
    {
        using var conn = _db.Open();
        if (conn is null) return false;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM conversations WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _log.Warn("conversation delete failed", ("err", ex.Message));
            return false;
        }
    }

    public IReadOnlyList<ConversationSummary> Search(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) return Recent(limit);
        using var conn = _db.Open();
        if (conn is null) return Array.Empty<ConversationSummary>();
        var list = new List<ConversationSummary>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, title, model, message_count, created_at
                FROM conversations
                WHERE title LIKE $q OR reference_text LIKE $q OR messages_json LIKE $q
                ORDER BY created_at DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$q", "%" + query.Trim() + "%");
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ConversationSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).UtcDateTime));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("conversation search failed", ("err", ex.Message));
        }
        return list;
    }

    private static string SerializeMessages(IReadOnlyList<(string Role, string Content)> messages)
        => JsonSerializer.Serialize(messages.Select(m => new RoleContent(m.Role, m.Content)).ToArray(), Json);

    private static IReadOnlyList<(string, string)> DeserializeMessages(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<(string, string)>();
        try
        {
            var arr = JsonSerializer.Deserialize<RoleContent[]>(raw, Json) ?? Array.Empty<RoleContent>();
            return arr.Select(x => (x.Role, x.Content)).ToArray();
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    private sealed record RoleContent(string Role, string Content);
}
