using Dapper;
using Guardian.Contracts;
using Guardian.Core.Time;
using System.Text.Json;

namespace Guardian.Core.Storage;

public sealed class PolicyRepo
{
    private readonly Db _db;
    public PolicyRepo(Db db) => _db = db;

    public Policy Current()
    {
        using var c = _db.Open();
        var json = c.ExecuteScalar<string?>("SELECT json FROM policy ORDER BY version DESC LIMIT 1");
        if (json is null)
        {
            var p = Policy.Default();
            c.Execute("INSERT INTO policy(version, json, saved_at) VALUES(@v, @j, @t)", new { v = p.Version, j = p.ToJson(), t = DateTimeOffset.Now.ToUnix() });
            return p;
        }
        return Policy.FromJson(json);
    }

    /// <summary>Saves a new version (append-only) and returns it.</summary>
    public Policy Save(Policy p)
    {
        using var c = _db.Open();
        var latest = c.ExecuteScalar<int?>("SELECT MAX(version) FROM policy") ?? 0;
        p.Version = latest + 1;
        p.Exceptions = new();
        c.Execute("INSERT INTO policy(version, json, saved_at) VALUES(@v, @j, @t)", new { v = p.Version, j = p.ToJson(), t = DateTimeOffset.Now.ToUnix() });
        return p;
    }

    public IReadOnlyList<(int version, DateTimeOffset savedAt)> History(int take = 20)
    {
        using var c = _db.Open();
        return c.Query<(int version, long saved_at)>("SELECT version, saved_at FROM policy ORDER BY version DESC LIMIT @take", new { take })
            .Select(r => (r.version, TimeExt.FromUnix(r.saved_at))).ToList();
    }

    // ---- exceptions ----

    public PolicyException AddException(PolicyException e)
    {
        using var c = _db.Open();
        e.CreatedAt = e.CreatedAt == default ? DateTimeOffset.Now : e.CreatedAt;
        var json = JsonSerializer.Serialize(e, Policy.JsonOptions);
        e.Id = c.ExecuteScalar<long>("INSERT INTO exception(date, json, created_at, note) VALUES(@d, @j, @t, @n); SELECT last_insert_rowid();",
            new { d = e.Date, j = json, t = e.CreatedAt.ToUnix(), n = e.Note });
        return e;
    }

    public IReadOnlyList<PolicyException> ExceptionsFor(string date)
    {
        using var c = _db.Open();
        return c.Query<(long id, string json)>("SELECT id, json FROM exception WHERE date=@date ORDER BY id", new { date })
            .Select(r => { var e = JsonSerializer.Deserialize<PolicyException>(r.json, Policy.JsonOptions)!; e.Id = r.id; return e; }).ToList();
    }

    /// <summary>Exceptions for the date and the day before (bedtime windows cross midnight, pauses can too).</summary>
    public IReadOnlyList<PolicyException> ExceptionsAround(DateOnly date)
    {
        var a = ExceptionsFor(date.AddDays(-1).DateKey());
        var b = ExceptionsFor(date.DateKey());
        return a.Concat(b).ToList();
    }

    public IReadOnlyList<PolicyException> UpcomingExceptions(DateOnly from)
    {
        using var c = _db.Open();
        return c.Query<(long id, string json)>("SELECT id, json FROM exception WHERE date>=@d ORDER BY date, id", new { d = from.DateKey() })
            .Select(r => { var e = JsonSerializer.Deserialize<PolicyException>(r.json, Policy.JsonOptions)!; e.Id = r.id; return e; }).ToList();
    }

    public void DeleteException(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM exception WHERE id=@id", new { id });
    }
}
