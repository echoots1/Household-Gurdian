using Guardian.Contracts;
using Guardian.Core.Time;

namespace Guardian.Core.Policies;

/// <summary>The base policy with today's (and last night's) exceptions merged in, evaluated for one instant.</summary>
public sealed class EffectivePolicy
{
    public Contracts.Policy Base { get; }
    public DateOnly Today { get; }
    public DateTimeOffset Now { get; }
    public IReadOnlyList<PolicyException> TodayExceptions { get; }
    public IReadOnlyList<PolicyException> YesterdayExceptions { get; }

    public EffectivePolicy(Contracts.Policy basePolicy, DateOnly today, DateTimeOffset now, IReadOnlyList<PolicyException> exceptions)
    {
        Base = basePolicy; Today = today; Now = now;
        var t = today.DateKey();
        var y = today.AddDays(-1).DateKey();
        TodayExceptions = exceptions.Where(e => e.Date == t).ToList();
        YesterdayExceptions = exceptions.Where(e => e.Date == y).ToList();
    }

    public int Version => Base.Version;

    /// <summary>All enforcement is suspended while paused.</summary>
    public bool Paused => TodayExceptions.Concat(YesterdayExceptions).Any(e => e.PauseUntil is { } p && p > Now);
    public DateTimeOffset? PausedUntil => TodayExceptions.Concat(YesterdayExceptions).Where(e => e.PauseUntil is { } p && p > Now).Max(e => e.PauseUntil);

    public bool Locked => LockedUntil is not null;
    public DateTimeOffset? LockedUntil => TodayExceptions.Concat(YesterdayExceptions)
        .Where(e => e.LockFrom is { } f && e.LockUntil is { } u && f <= Now && Now < u && !IsCancelled(EnforcementReason.Lock))
        .Select(e => e.LockUntil).Max();
    /// <summary>The next lock start within the horizon, for warnings.</summary>
    public DateTimeOffset? NextLockStart => TodayExceptions.Where(e => e.LockFrom is { } f && f > Now && !IsCancelled(EnforcementReason.Lock)).Select(e => e.LockFrom).Min();

    public bool IsCancelled(string reason) => TodayExceptions.Any(e => e.Cancel.Contains(reason, StringComparer.OrdinalIgnoreCase));

    /// <summary>Daily limit in minutes for "total" or a category, after today's overrides and added minutes. Null = no limit.</summary>
    public int? Limit(string key)
    {
        int? limit = Base.DailyLimits.TryGetValue(key, out var v) ? v : null;
        foreach (var e in TodayExceptions)
        {
            if (e.DailyLimits is not null && e.DailyLimits.TryGetValue(key, out var ov)) limit = ov <= 0 ? null : ov;
        }
        if (limit is null) return null;
        var extra = TodayExceptions.Sum(e => e.AddMinutes);
        return limit + extra;
    }

    public int AddedMinutesToday => TodayExceptions.Sum(e => e.AddMinutes);

    /// <summary>The bedtime window for the night that starts on the given date, or null when there is none.</summary>
    public TimeWindow? BedtimeForNight(DateOnly night)
    {
        var exceptions = night == Today ? TodayExceptions : night == Today.AddDays(-1) ? YesterdayExceptions : Array.Empty<PolicyException>();
        TimeWindow? w = IsSchoolNight(night) ? Base.Bedtime?.SchoolNights : Base.Bedtime?.Weekends;
        foreach (var e in exceptions)
        {
            if (e.NoBedtime || e.Cancel.Contains(EnforcementReason.Bedtime, StringComparer.OrdinalIgnoreCase)) w = null;
            else if (e.Bedtime is not null) w = e.Bedtime;
        }
        return w;
    }

    /// <summary>Sunday–Thursday nights are school nights; Friday and Saturday nights are weekend nights.</summary>
    public static bool IsSchoolNight(DateOnly night) => night.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday);

    /// <summary>Whether bedtime is in force now, and when it lifts.</summary>
    public (bool inForce, DateTimeOffset? liftsAt, DateTimeOffset? nextStart) Bedtime()
    {
        var t = TimeOnly.FromDateTime(Now.LocalDateTime);
        // Last night's window, if it crosses midnight into today.
        var last = BedtimeForNight(Today.AddDays(-1));
        if (last is not null && last.EndTime <= last.StartTime && t < last.EndTime)
            return (true, Today.AtLocal(last.EndTime), null);
        var tonight = BedtimeForNight(Today);
        if (tonight is not null)
        {
            var s = tonight.StartTime; var e = tonight.EndTime;
            if (e <= s)
            {
                if (t >= s) return (true, Today.AddDays(1).AtLocal(e), null);
                return (false, null, Today.AtLocal(s));
            }
            if (t >= s && t < e) return (true, Today.AtLocal(e), null);
            if (t < s) return (false, null, Today.AtLocal(s));
        }
        return (false, null, null);
    }

    /// <summary>Whether the category may be used at this instant, and when the current allowed window ends.</summary>
    public (bool allowed, DateTimeOffset? windowEnds, bool restricted) CategoryHours(string category)
    {
        if (!Base.CategoryHours.TryGetValue(category, out var rules) || rules.Count == 0) return (true, null, false);
        var t = TimeOnly.FromDateTime(Now.LocalDateTime);
        DateTimeOffset? bestEnd = null;
        foreach (var r in rules)
        {
            var s = TimeOnly.Parse(r.Start); var e = TimeOnly.Parse(r.End);
            if (e <= s)
            {
                // Crosses midnight: applies from s today (if rule is for today) or until e today (if rule is for yesterday).
                if (r.AppliesTo(Today.DayOfWeek) && t >= s) bestEnd = Max(bestEnd, Today.AddDays(1).AtLocal(e));
                if (r.AppliesTo(Today.AddDays(-1).DayOfWeek) && t < e) bestEnd = Max(bestEnd, Today.AtLocal(e));
            }
            else if (r.AppliesTo(Today.DayOfWeek) && t >= s && t < e) bestEnd = Max(bestEnd, Today.AtLocal(e));
        }
        return bestEnd is null ? (false, null, true) : (true, bestEnd, true);
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset b) => a is null || b > a ? b : a;

    /// <summary>Start of the next calendar day: when daily limits lift.</summary>
    public DateTimeOffset EndOfDay => Today.AddDays(1).AtLocal(TimeOnly.MinValue);
}
