using Dapper;
using Guardian.Core.Time;
using System.Text.Json;

namespace Guardian.Core.Storage;

public sealed record Alert(long Id, string Type, JsonElement Payload, DateTimeOffset CreatedAt, DateTimeOffset? AcknowledgedAt)
{
    public string? Str(string key) => Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty(key, out var v) ? v.ToString() : null;
}

public sealed class AlertRepo
{
    private readonly Db _db;
    public AlertRepo(Db db) => _db = db;

    public long Add(string type, object payload, DateTimeOffset? at = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("INSERT INTO alert(type, payload, created_at) VALUES(@t, @p, @a); SELECT last_insert_rowid();",
            new { t = type, p = JsonSerializer.Serialize(payload, Contracts.Policy.JsonOptions), a = (at ?? DateTimeOffset.Now).ToUnix() });
    }

    public IReadOnlyList<Alert> List(bool unreadOnly = false, int take = 200, string? type = null)
    {
        using var c = _db.Open();
        var sql = "SELECT id, type, payload, created_at, acknowledged_at FROM alert WHERE 1=1" + (unreadOnly ? " AND acknowledged_at IS NULL" : "") + (type is null ? "" : " AND type=@type") + " ORDER BY id DESC LIMIT @take";
        return c.Query<(long id, string type, string payload, long created_at, long? ack)>(sql, new { take, type })
            .Select(r => new Alert(r.id, r.type, JsonDocument.Parse(r.payload).RootElement, TimeExt.FromUnix(r.created_at), r.ack is null ? null : TimeExt.FromUnix(r.ack.Value))).ToList();
    }

    public int UnreadCount()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM alert WHERE acknowledged_at IS NULL");
    }

    public void Acknowledge(long id)
    {
        using var c = _db.Open();
        c.Execute("UPDATE alert SET acknowledged_at=@t WHERE id=@id AND acknowledged_at IS NULL", new { t = DateTimeOffset.Now.ToUnix(), id });
    }

    public IReadOnlyList<Alert> TakeUnemailed()
    {
        using var c = _db.Open();
        var rows = List(take: 500).Where(a => true).ToList();
        var ids = c.Query<long>("SELECT id FROM alert WHERE emailed=0").ToHashSet();
        return rows.Where(a => ids.Contains(a.Id)).OrderBy(a => a.Id).ToList();
    }

    public void MarkEmailed(IEnumerable<long> ids)
    {
        using var c = _db.Open();
        c.Execute("UPDATE alert SET emailed=1 WHERE id IN @ids", new { ids = ids.ToArray() });
    }

    public int Purge(DateTimeOffset olderThan)
    {
        using var c = _db.Open();
        return c.Execute("DELETE FROM alert WHERE created_at < @t", new { t = olderThan.ToUnix() });
    }
}
