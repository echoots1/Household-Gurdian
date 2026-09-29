using Dapper;
using Guardian.Core.Time;

namespace Guardian.Core.Storage;

public sealed class ListRepo
{
    private readonly Db _db;
    public ListRepo(Db db) => _db = db;

    public void ReplaceList(string listName, IEnumerable<string> domains, DateTimeOffset at)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM category_list WHERE list_name=@l", new { l = listName }, tx);
        foreach (var d in domains.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct())
            c.Execute("INSERT OR IGNORE INTO category_list(domain, list_name, updated_at) VALUES(@d, @l, @t)", new { d, l = listName, t = at.ToUnix() }, tx);
        tx.Commit();
    }

    public void AddToList(string listName, string domain)
    {
        using var c = _db.Open();
        c.Execute("INSERT OR IGNORE INTO category_list(domain, list_name, updated_at) VALUES(@d, @l, @t)", new { d = domain.ToLowerInvariant(), l = listName, t = DateTimeOffset.Now.ToUnix() });
    }

    public void RemoveFromList(string listName, string domain)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM category_list WHERE domain=@d AND list_name=@l", new { d = domain.ToLowerInvariant(), l = listName });
    }

    public IReadOnlyList<string> ListNames()
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT DISTINCT list_name FROM category_list ORDER BY list_name").ToList();
    }

    public IReadOnlyList<string> Domains(string listName)
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT domain FROM category_list WHERE list_name=@l ORDER BY domain", new { l = listName }).ToList();
    }

    public Dictionary<string, int> Counts()
    {
        using var c = _db.Open();
        return c.Query<(string l, int n)>("SELECT list_name, COUNT(*) FROM category_list GROUP BY list_name").ToDictionary(r => r.l, r => r.n);
    }

    /// <summary>Returns the list names the domain (or any parent domain) is on.</summary>
    public IReadOnlyList<string> Match(string domain)
    {
        var candidates = new List<string>();
        var parts = domain.ToLowerInvariant().Split('.');
        for (var i = 0; i < parts.Length - 1; i++) candidates.Add(string.Join('.', parts.Skip(i)));
        if (candidates.Count == 0) candidates.Add(domain.ToLowerInvariant());
        using var c = _db.Open();
        return c.Query<string>("SELECT DISTINCT list_name FROM category_list WHERE domain IN @candidates", new { candidates }).ToList();
    }

    /// <summary>"allow" suppresses flags; "flag" always flags.</summary>
    public string? Override(string domain)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>("SELECT mode FROM domain_override WHERE domain=@d", new { d = domain.ToLowerInvariant() });
    }

    public void SetOverride(string domain, string? mode)
    {
        using var c = _db.Open();
        if (mode is null) c.Execute("DELETE FROM domain_override WHERE domain=@d", new { d = domain.ToLowerInvariant() });
        else c.Execute("INSERT INTO domain_override(domain, mode, created_at) VALUES(@d, @m, @t) ON CONFLICT(domain) DO UPDATE SET mode=excluded.mode", new { d = domain.ToLowerInvariant(), m = mode, t = DateTimeOffset.Now.ToUnix() });
    }

    public IReadOnlyList<(string domain, string mode)> Overrides()
    {
        using var c = _db.Open();
        return c.Query<(string domain, string mode)>("SELECT domain, mode FROM domain_override ORDER BY domain").ToList();
    }

    /// <summary>Records first sight; returns true if this is the first time.</summary>
    public bool MarkSeen(string domain, DateTimeOffset at)
    {
        using var c = _db.Open();
        return c.Execute("INSERT OR IGNORE INTO domain_seen(domain, first_seen) VALUES(@d, @t)", new { d = domain.ToLowerInvariant(), t = at.ToUnix() }) == 1;
    }

    public DateTimeOffset? FirstSeen(string domain)
    {
        using var c = _db.Open();
        var v = c.ExecuteScalar<long?>("SELECT first_seen FROM domain_seen WHERE domain=@d", new { d = domain.ToLowerInvariant() });
        return v is null ? null : TimeExt.FromUnix(v.Value);
    }

    public Dictionary<string, DateTimeOffset> AllFirstSeen()
    {
        using var c = _db.Open();
        return c.Query<(string d, long t)>("SELECT domain, first_seen FROM domain_seen").ToDictionary(r => r.d, r => TimeExt.FromUnix(r.t));
    }
}
