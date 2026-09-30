using Dapper;

namespace Guardian.Core.Storage;

public sealed record DailyTotals(string Date, Dictionary<string, int> SecondsByCategory)
{
    public int Seconds(string category) => SecondsByCategory.TryGetValue(category, out var s) ? s : 0;
    /// <summary>Everything except School.</summary>
    public int CountedSeconds => SecondsByCategory.Where(kv => kv.Key != Contracts.Categories.School).Sum(kv => kv.Value);
    public int AllSeconds => SecondsByCategory.Values.Sum();
}

public sealed record ItemTotal(string Date, string Kind, string Name, string Category, int Seconds);

public sealed class RollupRepo
{
    private readonly Db _db;
    public RollupRepo(Db db) => _db = db;

    public DailyTotals Day(string date)
    {
        using var c = _db.Open();
        var d = c.Query<(string category, int seconds)>("SELECT category, seconds FROM rollup_daily WHERE date=@date", new { date })
            .ToDictionary(r => r.category, r => r.seconds, StringComparer.OrdinalIgnoreCase);
        foreach (var cat in Contracts.Categories.All) d.TryAdd(cat, 0);
        return new DailyTotals(date, d);
    }

    public IReadOnlyList<DailyTotals> Range(DateOnly from, DateOnly to)
    {
        using var c = _db.Open();
        var rows = c.Query<(string date, string category, int seconds)>("SELECT date, category, seconds FROM rollup_daily WHERE date BETWEEN @a AND @b",
            new { a = from.ToString("yyyy-MM-dd"), b = to.ToString("yyyy-MM-dd") }).ToLookup(r => r.date);
        var list = new List<DailyTotals>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd");
            var dict = rows[key].ToDictionary(r => r.category, r => r.seconds, StringComparer.OrdinalIgnoreCase);
            foreach (var cat in Contracts.Categories.All) dict.TryAdd(cat, 0);
            list.Add(new DailyTotals(key, dict));
        }
        return list;
    }

    public IReadOnlyList<ItemTotal> Items(DateOnly from, DateOnly to, string? kind = null, int top = 50)
    {
        using var c = _db.Open();
        var sql = "SELECT kind, name, category, SUM(seconds) AS seconds FROM rollup_item WHERE date BETWEEN @a AND @b" + (kind is null ? "" : " AND kind=@kind") +
                  " GROUP BY kind, name, category ORDER BY seconds DESC LIMIT @top";
        return c.Query<(string kind, string name, string category, int seconds)>(sql, new { a = from.ToString("yyyy-MM-dd"), b = to.ToString("yyyy-MM-dd"), kind, top })
            .Select(r => new ItemTotal("", r.kind, r.name, r.category, r.seconds)).ToList();
    }

    /// <summary>Replaces the rollups for one date from the given per-category and per-item totals.</summary>
    public void WriteDay(string date, IReadOnlyDictionary<string, int> byCategory, IEnumerable<ItemTotal> items, int rulesVersion)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM rollup_daily WHERE date=@date; DELETE FROM rollup_item WHERE date=@date;", new { date }, tx);
        foreach (var kv in byCategory)
            c.Execute("INSERT INTO rollup_daily(date, category, seconds, rules_version) VALUES(@date, @c, @s, @v)", new { date, c = kv.Key, s = kv.Value, v = rulesVersion }, tx);
        foreach (var it in items)
            c.Execute("INSERT OR REPLACE INTO rollup_item(date, kind, name, category, seconds) VALUES(@date, @k, @n, @c, @s)", new { date, k = it.Kind, n = it.Name, c = it.Category, s = it.Seconds }, tx);
        tx.Commit();
    }

    public int PurgeItems(DateOnly olderThan)
    {
        using var c = _db.Open();
        return c.Execute("DELETE FROM rollup_item WHERE date < @d", new { d = olderThan.ToString("yyyy-MM-dd") });
    }
}
