using Guardian.Contracts;
using Guardian.Core.Policies;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Guardian.Service.Infrastructure;

namespace Guardian.Service.Web;

public sealed record LimitMeter(string Key, string Label, int UsedSeconds, int? LimitMinutes)
{
    public int? RemainingSeconds => LimitMinutes is null ? null : Math.Max(0, LimitMinutes.Value * 60 - UsedSeconds);
    public int Percent => LimitMinutes is null or 0 ? 0 : Math.Min(100, (int)Math.Round(100.0 * UsedSeconds / (LimitMinutes.Value * 60)));
}

public sealed record TodayView(string Date, DailyTotals Totals, IReadOnlyList<LimitMeter> Meters, IReadOnlyList<ItemTotal> TopApps, IReadOnlyList<ItemTotal> TopSites,
    string? CurrentName, string? CurrentCategory, bool SessionActive, bool NoticeAccepted, TrayStatus Status, string? BedtimeTonight, DateTimeOffset? PausedUntil,
    IReadOnlyList<PolicyException> Exceptions, IReadOnlyList<EnforcementEvent> Events, bool ExtensionActive);

public sealed record WeekDay(string Date, DayOfWeek Day, DailyTotals Totals, int? TotalLimit, bool BedtimeSignOut, bool LateLogin);
public sealed record WeekView(DateOnly Start, IReadOnlyList<WeekDay> Days, IReadOnlyList<PolicyException> Exceptions);

public sealed record ScheduleDay(string Date, DayOfWeek Day, string? Bedtime, IReadOnlyList<PolicyException> Exceptions);
public sealed record ScheduleView(IReadOnlyList<ScheduleDay> Days, Dictionary<string, int> Limits, Dictionary<string, List<CategoryHoursRule>> Hours, int Version);

/// <summary>Read models for the parent dashboard and the child's page. Both read the same rollups, so both see the same numbers.</summary>
public sealed class Queries
{
    private readonly RollupRepo _rollups;
    private readonly PolicyRepo _policies;
    private readonly SettingsRepo _settings;
    private readonly EnforcementRepo _events;
    private readonly GuardianState _state;
    private readonly IClock _clock;

    public Queries(RollupRepo rollups, PolicyRepo policies, SettingsRepo settings, EnforcementRepo events, GuardianState state, IClock clock)
    {
        _rollups = rollups; _policies = policies; _settings = settings; _events = events; _state = state; _clock = clock;
    }

    public EffectivePolicy Effective(DateOnly date, DateTimeOffset now) => new(_policies.Current(), date, now, _policies.ExceptionsAround(date));

    public TodayView Today(DateOnly? date = null)
    {
        var now = _clock.Now;
        var d = date ?? now.LocalDate();
        var key = d.DateKey();
        var totals = _rollups.Day(key);
        var ep = Effective(d, d == now.LocalDate() ? now : d.AtLocal(new TimeOnly(12, 0)));
        var meters = new List<LimitMeter> { new("total", "Total (School excluded)", totals.CountedSeconds, ep.Limit("total")) };
        foreach (var c in Categories.All) meters.Add(new LimitMeter(c, c, totals.Seconds(c), c == Categories.School ? null : ep.Limit(c)));
        var items = _rollups.Items(d, d, top: 200);
        var status = _state.Status;
        var bedtime = ep.BedtimeForNight(d);
        return new TodayView(key, totals, meters,
            items.Where(i => i.Kind == "app").Take(10).ToList(), items.Where(i => i.Kind == "web").Take(10).ToList(),
            _state.SessionActive(now) && !_state.Sampler.LastIdle ? _state.Sampler.LastDomain ?? _state.Sampler.LastProcess : null,
            _state.Sampler.LastCategory, _state.SessionActive(now), _settings.NoticeAcceptedAt is not null, status,
            bedtime is null ? null : $"{Fmt(bedtime.StartTime)} – {Fmt(bedtime.EndTime)}", ep.PausedUntil,
            _policies.ExceptionsFor(key), _events.ForDate(key), _state.Sampler.ExtensionActive(now));
    }

    public WeekView Week(DateOnly? start = null)
    {
        var now = _clock.Now;
        var today = now.LocalDate();
        var s = start ?? today.AddDays(-(int)today.DayOfWeek + (int)DayOfWeek.Monday);
        if (s > today) s = today.AddDays(-6);
        var totals = _rollups.Range(s, s.AddDays(6));
        var days = new List<WeekDay>();
        var policy = _policies.Current();
        var sessions = _events.SessionsRange(s.AtLocal(TimeOnly.MinValue), s.AddDays(7).AtLocal(TimeOnly.MinValue));
        var enforcement = _events.Range(s.AtLocal(TimeOnly.MinValue), s.AddDays(7).AtLocal(TimeOnly.MinValue));
        var allEx = _policies.UpcomingExceptions(s.AddDays(-1)).Where(e => string.CompareOrdinal(e.Date, s.AddDays(6).DateKey()) <= 0).ToList();
        for (var i = 0; i < 7; i++)
        {
            var d = s.AddDays(i);
            var ep = new EffectivePolicy(policy, d, d.AtLocal(new TimeOnly(12, 0)), allEx);
            var signOut = enforcement.Any(e => e.Reason == EnforcementReason.Bedtime && e.Step == EnforcementStep.LoggedOff && e.At.LocalDate() == d);
            var late = sessions.Any(se => se.Kind == "logoff" && se.Detail == EnforcementReason.Bedtime && se.At.LocalDate() == d) && enforcement.Any(e => e.Reason == EnforcementReason.Bedtime && e.Detail == "repeat" && e.At.LocalDate() == d);
            days.Add(new WeekDay(d.DateKey(), d.DayOfWeek, totals[i], ep.Limit("total"), signOut, late));
        }
        return new WeekView(s, days, allEx);
    }

    public ScheduleView Schedule()
    {
        var now = _clock.Now;
        var today = now.LocalDate();
        var policy = _policies.Current();
        var ex = _policies.UpcomingExceptions(today.AddDays(-1));
        var days = new List<ScheduleDay>();
        for (var i = 0; i < 7; i++)
        {
            var d = today.AddDays(i);
            var ep = new EffectivePolicy(policy, d, d.AtLocal(new TimeOnly(12, 0)), ex);
            var w = ep.BedtimeForNight(d);
            days.Add(new ScheduleDay(d.DateKey(), d.DayOfWeek, w is null ? null : $"{Fmt(w.StartTime)} – {Fmt(w.EndTime)}", ex.Where(e => e.Date == d.DateKey()).ToList()));
        }
        return new ScheduleView(days, policy.DailyLimits, policy.CategoryHours, policy.Version);
    }

    public static string Fmt(TimeOnly t) => t.ToString("h:mm tt");
    public static string Hm(int seconds)
    {
        var m = (seconds + 30) / 60;
        return m >= 60 ? $"{m / 60}h {m % 60:00}m" : $"{m}m";
    }
}
