using Guardian.Contracts;
using Guardian.Core.Categorization;
using Guardian.Core.Storage;

namespace Guardian.Core.Rollups;

/// <summary>
/// Maintains rollup_daily and rollup_item from raw activity events. Today is re-rolled every minute;
/// all history is rebuilt when the rules change so fixes apply retroactively.
/// </summary>
public sealed class RollupService
{
    private readonly ActivityRepo _activity;
    private readonly RollupRepo _rollups;
    private readonly RuleRepo _rules;

    public RollupService(ActivityRepo activity, RollupRepo rollups, RuleRepo rules)
    {
        _activity = activity; _rollups = rollups; _rules = rules;
    }

    public Categorizer CurrentCategorizer() => new(_rules.All(), _rules.Version());

    public void RollDay(string date)
    {
        var events = _activity.ForDate(date);
        var byCat = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Categories.All) byCat[c] = 0;
        var items = new Dictionary<(string kind, string name), (string category, int seconds)>();
        foreach (var e in events)
        {
            var s = e.Seconds;
            if (s <= 0) continue;
            byCat[e.Category] = byCat.GetValueOrDefault(e.Category) + s;
            var name = e.Name;
            if (string.IsNullOrEmpty(name)) continue;
            var key = (e.Kind, name);
            items[key] = (e.Category, items.TryGetValue(key, out var cur) ? cur.seconds + s : s);
        }
        _rollups.WriteDay(date, byCat, items.Select(kv => new ItemTotal(date, kv.Key.kind, kv.Key.name, kv.Value.category, kv.Value.seconds)), _rules.Version());
    }

    /// <summary>Recategorizes every raw event with the current rules and rebuilds every day that has data.</summary>
    public int RebuildAll()
    {
        var cat = CurrentCategorizer();
        _activity.Recategorize(cat.Categorize);
        var dates = _activity.DatesWithData();
        foreach (var d in dates) RollDay(d);
        _rules.ClearRebuildPending();
        return dates.Count;
    }

    public void RebuildIfPending()
    {
        if (_rules.RebuildPending()) RebuildAll();
    }
}
