using Dapper;
using Guardian.Core.Time;

namespace Guardian.Core.Storage;

public sealed record ActivityEvent(long Id, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Kind, string? Process, string? Title,
    string? Domain, string? Subdomain, bool Idle, string? Browser, string Category)
{
    public int Seconds => (int)Math.Max(0, (EndedAt - StartedAt).TotalSeconds);
    public string Name => Kind == "web" ? Domain ?? "" : Process ?? "";
}

public sealed record ActivityQuery(DateTimeOffset From, DateTimeOffset To, string? Category = null, string? Text = null, bool IncludeIdle = false, int Limit = 2000);

public sealed class ActivityRepo
{
    private readonly Db _db;
    public ActivityRepo(Db db) => _db = db;

    public long Insert(DateTimeOffset startedAt, DateTimeOffset endedAt, string kind, string? process, string? title, string? domain, string? subdomain, bool idle, string? browser, string category)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO activity_event(started_at, ended_at, kind, process, title, domain, subdomain, idle, browser, category, date)
            VALUES(@s, @e, @k, @p, @t, @d, @sd, @i, @b, @c, @date); SELECT last_insert_rowid();
            """, new { s = startedAt.ToUnix(), e = endedAt.ToUnix(), k = kind, p = process, t = title, d = domain, sd = subdomain, i = idle ? 1 : 0, b = browser, c = category, date = startedAt.DateKey() });
    }

    /// <summary>Extends the end time of an open interval (the sampler keeps one row per contiguous run).</summary>
    public void Extend(long id, DateTimeOffset endedAt)
    {
        using var c = _db.Open();
        c.Execute("UPDATE activity_event SET ended_at=@e WHERE id=@id", new { e = endedAt.ToUnix(), id });
    }

    public IReadOnlyList<ActivityEvent> Query(ActivityQuery q)
    {
        using var c = _db.Open();
        var sql = "SELECT id, started_at, ended_at, kind, process, title, domain, subdomain, idle, browser, category FROM activity_event WHERE started_at < @to AND ended_at > @from";
        if (!q.IncludeIdle) sql += " AND idle=0";
        if (!string.IsNullOrEmpty(q.Category)) sql += " AND category=@cat";
        if (!string.IsNullOrEmpty(q.Text)) sql += " AND (process LIKE @txt OR domain LIKE @txt OR title LIKE @txt)";
        sql += " ORDER BY started_at DESC LIMIT @limit";
        return c.Query<(long id, long s, long e, string kind, string? process, string? title, string? domain, string? subdomain, int idle, string? browser, string category)>(sql,
                new { from = q.From.ToUnix(), to = q.To.ToUnix(), cat = q.Category, txt = "%" + q.Text + "%", limit = q.Limit })
            .Select(r => new ActivityEvent(r.id, TimeExt.FromUnix(r.s), TimeExt.FromUnix(r.e), r.kind, r.process, r.title, r.domain, r.subdomain, r.idle == 1, r.browser, r.category))
            .ToList();
    }

    public IReadOnlyList<ActivityEvent> ForDate(string date, bool includeIdle = false)
    {
        using var c = _db.Open();
        var sql = "SELECT id, started_at, ended_at, kind, process, title, domain, subdomain, idle, browser, category FROM activity_event WHERE date=@date" + (includeIdle ? "" : " AND idle=0") + " ORDER BY started_at";
        return c.Query<(long id, long s, long e, string kind, string? process, string? title, string? domain, string? subdomain, int idle, string? browser, string category)>(sql, new { date })
            .Select(r => new ActivityEvent(r.id, TimeExt.FromUnix(r.s), TimeExt.FromUnix(r.e), r.kind, r.process, r.title, r.domain, r.subdomain, r.idle == 1, r.browser, r.category))
            .ToList();
    }

    public IReadOnlyList<string> DatesWithData()
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT DISTINCT date FROM activity_event ORDER BY date").ToList();
    }

    public void Recategorize(Func<ActivityEvent, string> categorize)
    {
        using var c = _db.Open();
        var rows = c.Query<(long id, long s, long e, string kind, string? process, string? title, string? domain, string? subdomain, int idle, string? browser, string category)>(
            "SELECT id, started_at, ended_at, kind, process, title, domain, subdomain, idle, browser, category FROM activity_event").ToList();
        using var tx = c.BeginTransaction();
        foreach (var r in rows)
        {
            var ev = new ActivityEvent(r.id, TimeExt.FromUnix(r.s), TimeExt.FromUnix(r.e), r.kind, r.process, r.title, r.domain, r.subdomain, r.idle == 1, r.browser, r.category);
            var cat = categorize(ev);
            if (cat != r.category) c.Execute("UPDATE activity_event SET category=@cat WHERE id=@id", new { cat, id = r.id }, tx);
        }
        tx.Commit();
    }

    public int Purge(DateTimeOffset olderThan)
    {
        using var c = _db.Open();
        return c.Execute("DELETE FROM activity_event WHERE ended_at < @t", new { t = olderThan.ToUnix() });
    }

    public (int seconds, string? currentName, string? currentCategory) CurrentForeground(DateTimeOffset now)
    {
        using var c = _db.Open();
        var r = c.QueryFirstOrDefault<(string? process, string? domain, string? category, long ended)>(
            "SELECT process, domain, category, ended_at FROM activity_event WHERE idle=0 ORDER BY ended_at DESC LIMIT 1");
        if (r.category is null || now.ToUnix() - r.ended > 20) return (0, null, null);
        return (0, r.domain ?? r.process, r.category);
    }
}
