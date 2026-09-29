using Dapper;
using Guardian.Core.Time;

namespace Guardian.Core.Storage;

public sealed record EnforcementEvent(long Id, DateTimeOffset At, string Reason, string Step, int PolicyVersion, string? Detail);
public sealed record SessionEvent(long Id, DateTimeOffset At, string Kind, string? Detail);

public sealed class EnforcementRepo
{
    private readonly Db _db;
    public EnforcementRepo(Db db) => _db = db;

    public void Add(DateTimeOffset at, string reason, string step, int policyVersion, string? detail = null)
    {
        using var c = _db.Open();
        c.Execute("INSERT INTO enforcement_event(at, reason, step, policy_version, detail, date) VALUES(@a, @r, @s, @v, @d, @date)",
            new { a = at.ToUnix(), r = reason, s = step, v = policyVersion, d = detail, date = at.DateKey() });
    }

    public IReadOnlyList<EnforcementEvent> ForDate(string date)
    {
        using var c = _db.Open();
        return c.Query<(long id, long at, string reason, string step, int v, string? detail)>("SELECT id, at, reason, step, policy_version, detail FROM enforcement_event WHERE date=@date ORDER BY at", new { date })
            .Select(r => new EnforcementEvent(r.id, TimeExt.FromUnix(r.at), r.reason, r.step, r.v, r.detail)).ToList();
    }

    public IReadOnlyList<EnforcementEvent> Range(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = _db.Open();
        return c.Query<(long id, long at, string reason, string step, int v, string? detail)>("SELECT id, at, reason, step, policy_version, detail FROM enforcement_event WHERE at BETWEEN @a AND @b ORDER BY at DESC", new { a = from.ToUnix(), b = to.ToUnix() })
            .Select(r => new EnforcementEvent(r.id, TimeExt.FromUnix(r.at), r.reason, r.step, r.v, r.detail)).ToList();
    }

    public void AddSession(DateTimeOffset at, string kind, string? detail = null)
    {
        using var c = _db.Open();
        c.Execute("INSERT INTO session_event(at, kind, detail, date) VALUES(@a, @k, @d, @date)", new { a = at.ToUnix(), k = kind, d = detail, date = at.DateKey() });
    }

    public IReadOnlyList<SessionEvent> SessionsRange(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = _db.Open();
        return c.Query<(long id, long at, string kind, string? detail)>("SELECT id, at, kind, detail FROM session_event WHERE at BETWEEN @a AND @b ORDER BY at DESC", new { a = from.ToUnix(), b = to.ToUnix() })
            .Select(r => new SessionEvent(r.id, TimeExt.FromUnix(r.at), r.kind, r.detail)).ToList();
    }

    public int Purge(DateTimeOffset olderThan)
    {
        using var c = _db.Open();
        var t = olderThan.ToUnix();
        return c.Execute("DELETE FROM enforcement_event WHERE at < @t; DELETE FROM session_event WHERE at < @t;", new { t });
    }
}
