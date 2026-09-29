using Guardian.Contracts;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

public class PolicyModel : PageModel
{
    private readonly PolicyRepo _policies; private readonly IClock _clock;
    public PolicyModel(PolicyRepo policies, IClock clock) { _policies = policies; _clock = clock; }
    public Policy Policy { get; private set; } = null!;
    public IReadOnlyList<PolicyException> Exceptions { get; private set; } = Array.Empty<PolicyException>();
    public IReadOnlyList<(int version, DateTimeOffset savedAt)> History { get; private set; } = Array.Empty<(int, DateTimeOffset)>();
    public string Today => _clock.Now.DateKey();

    public void OnGet()
    {
        Policy = _policies.Current();
        Exceptions = _policies.UpcomingExceptions(_clock.Now.LocalDate());
        History = _policies.History(8);
    }

    public static string Describe(PolicyException e)
    {
        var parts = new List<string>();
        if (e.NoBedtime) parts.Add("no bedtime");
        if (e.Bedtime is not null) parts.Add($"bedtime {e.Bedtime.Start}–{e.Bedtime.End}");
        if (e.DailyLimits is not null) foreach (var (k, v) in e.DailyLimits) parts.Add($"{k} limit {v} min");
        if (e.AddMinutes > 0) parts.Add($"+{e.AddMinutes} min");
        if (e.PauseUntil is not null) parts.Add($"paused until {e.PauseUntil:h:mm tt}");
        if (e.LockUntil is not null) parts.Add($"locked until {e.LockUntil:h:mm tt}");
        if (e.Cancel.Count > 0) parts.Add("cancelled " + string.Join(", ", e.Cancel.Select(EnforcementReason.Describe)));
        var s = string.Join(", ", parts);
        return string.IsNullOrEmpty(e.Note) ? s : $"{e.Note} ({s})";
    }
}
