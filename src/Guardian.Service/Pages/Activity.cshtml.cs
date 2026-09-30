using Guardian.Core.Storage;
using Guardian.Core.Time;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

public class ActivityModel : PageModel
{
    private readonly ActivityRepo _activity; private readonly EnforcementRepo _events; private readonly IClock _clock;
    public ActivityModel(ActivityRepo activity, EnforcementRepo events, IClock clock) { _activity = activity; _events = events; _clock = clock; }

    public sealed record Row(DateTimeOffset At, ActivityEvent? Event = null, EnforcementEvent? Enforcement = null, SessionEvent? Session = null);
    public string From { get; private set; } = ""; public string To { get; private set; } = ""; public string? Category { get; private set; } public string? Q { get; private set; }
    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    public void OnGet(string? from, string? to, string? category, string? q)
    {
        var today = _clock.Now.LocalDate();
        var f = from is null ? today : DateOnly.Parse(from);
        var t = to is null ? f : DateOnly.Parse(to);
        From = f.DateKey(); To = t.DateKey(); Category = string.IsNullOrEmpty(category) ? null : category; Q = string.IsNullOrEmpty(q) ? null : q;
        var (a, b) = (f.AtLocal(TimeOnly.MinValue), t.AddDays(1).AtLocal(TimeOnly.MinValue));
        var rows = _activity.Query(new ActivityQuery(a, b, Category, Q, false, 1000)).Select(e => new Row(e.StartedAt, e))
            .Concat(_events.Range(a, b).Select(e => new Row(e.At, Enforcement: e)))
            .Concat(_events.SessionsRange(a, b).Select(s => new Row(s.At, Session: s)));
        Rows = rows.OrderByDescending(r => r.At).ToList();
    }
}
