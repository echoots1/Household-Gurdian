using Guardian.Core.Storage;
using Guardian.Core.Time;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

public class SitesModel : PageModel
{
    private readonly RollupRepo _rollups; private readonly ListRepo _lists; private readonly IClock _clock;
    public SitesModel(RollupRepo rollups, ListRepo lists, IClock clock) { _rollups = rollups; _lists = lists; _clock = clock; }
    public sealed record SiteRow(string Name, string Category, int Seconds, DateTimeOffset? FirstSeen, string? Override, IReadOnlyList<string> Lists);
    public string From { get; private set; } = ""; public string To { get; private set; } = ""; public string WeekStart { get; private set; } = "";
    public IReadOnlyList<SiteRow> Rows { get; private set; } = Array.Empty<SiteRow>();

    public void OnGet(string? from, string? to)
    {
        var today = _clock.Now.LocalDate();
        var f = from is null ? today.AddDays(-6) : DateOnly.Parse(from);
        var t = to is null ? today : DateOnly.Parse(to);
        From = f.DateKey(); To = t.DateKey(); WeekStart = today.AddDays(-6).DateKey();
        var seen = _lists.AllFirstSeen();
        var ov = _lists.Overrides().ToDictionary(o => o.domain, o => o.mode);
        Rows = _rollups.Items(f, t, "web", 200).Select(i => new SiteRow(i.Name, i.Category, i.Seconds, seen.GetValueOrDefault(i.Name), ov.GetValueOrDefault(i.Name), _lists.Match(i.Name))).ToList();
    }
}
