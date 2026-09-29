using Dapper;
using Guardian.Contracts;
using Guardian.Core.Time;

namespace Guardian.Core.Storage;

public sealed class RuleRepo
{
    private readonly Db _db;
    public RuleRepo(Db db) => _db = db;

    public IReadOnlyList<Rule> All()
    {
        using var c = _db.Open();
        return c.Query<(long id, string match_type, string pattern, string category, int priority, long created_at)>(
                "SELECT id, match_type, pattern, category, priority, created_at FROM rule ORDER BY priority, id")
            .Select(r => new Rule { Id = r.id, MatchType = Enum.Parse<RuleMatch>(r.match_type, true), Pattern = r.pattern, Category = r.category, Priority = r.priority, CreatedAt = TimeExt.FromUnix(r.created_at) })
            .ToList();
    }

    public int Version()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("SELECT version FROM rules_meta WHERE id=1");
    }

    public bool RebuildPending()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("SELECT rebuild_pending FROM rules_meta WHERE id=1") == 1;
    }

    public void ClearRebuildPending()
    {
        using var c = _db.Open();
        c.Execute("UPDATE rules_meta SET rebuild_pending=0 WHERE id=1");
    }

    /// <summary>Replaces the whole rule set, bumps the rules version, and queues a rollup rebuild.</summary>
    public int Replace(IEnumerable<Rule> rules)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM rule", transaction: tx);
        var now = DateTimeOffset.Now.ToUnix();
        foreach (var r in rules)
        {
            if (!Categories.IsValid(r.Category)) throw new ArgumentException($"Unknown category '{r.Category}'");
            if (string.IsNullOrWhiteSpace(r.Pattern)) continue;
            c.Execute("INSERT INTO rule(match_type, pattern, category, priority, created_at) VALUES(@m, @p, @c, @pr, @t)",
                new { m = r.MatchType.ToString(), p = r.Pattern.Trim(), c = r.Category, pr = r.Priority, t = now }, tx);
        }
        c.Execute("UPDATE rules_meta SET version=version+1, rebuild_pending=1 WHERE id=1", transaction: tx);
        var v = c.ExecuteScalar<int>("SELECT version FROM rules_meta WHERE id=1", transaction: tx);
        tx.Commit();
        return v;
    }

    /// <summary>Adds one rule ahead of everything else (used by "reclassify" in the Activity screen).</summary>
    public int Add(Rule r)
    {
        var all = All().ToList();
        r.Priority = all.Count == 0 ? 10 : Math.Min(r.Priority, all.Min(x => x.Priority) - 1);
        all.Insert(0, r);
        return Replace(all);
    }

    public void SeedIfEmpty(IEnumerable<Rule> defaults)
    {
        using var c = _db.Open();
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM rule") > 0) return;
        var now = DateTimeOffset.Now.ToUnix();
        foreach (var r in defaults)
            c.Execute("INSERT INTO rule(match_type, pattern, category, priority, created_at) VALUES(@m, @p, @c, @pr, @t)",
                new { m = r.MatchType.ToString(), p = r.Pattern, c = r.Category, pr = r.Priority, t = now });
    }
}
