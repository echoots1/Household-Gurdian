using Guardian.Contracts;
using Guardian.Core.Policies;
using Guardian.Core.Time;
using Guardian.Core.Storage;

namespace Guardian.Core.Enforcement;

/// <summary>Everything the enforcer wants done this tick. The service carries it out; the tray gets the notice and close requests.</summary>
public sealed class TickResult
{
    public TrayNotice? Notice { get; set; }
    public HashSet<string> CloseProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool CloseAll { get; set; }
    public HashSet<string> TerminateProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool TerminateAll { get; set; }
    public bool Logoff { get; set; }
    public List<(string reason, string step, string? detail)> Events { get; } = new();
    public string? EnforcingReason { get; set; }
    public DateTimeOffset? LiftsAt { get; set; }
    public string? WarningReason { get; set; }
    public DateTimeOffset? WarningDeadline { get; set; }
    public bool ClockJump { get; set; }
    public TrayState State { get; set; } = TrayState.On;
    public string Tooltip { get; set; } = "Guardian is on — click to see today";
}

/// <summary>What the engine needs to know about the moment.</summary>
public sealed class TickInput
{
    public required DateTimeOffset Now { get; init; }
    public required EffectivePolicy Policy { get; init; }
    public required DailyTotals UsageToday { get; init; }
    public bool NoticeAccepted { get; init; }
    /// <summary>True when the monitored user has an interactive session right now (the tray is reporting).</summary>
    public bool SessionActive { get; init; }
    /// <summary>When the current interactive session started (login), for the repeat sign-out rule.</summary>
    public DateTimeOffset? SessionStartedAt { get; init; }
    /// <summary>Process names known to belong to each category (from Process rules), used for category closes.</summary>
    public required Func<string, IReadOnlyCollection<string>> ProcessesInCategory { get; init; }
    public string? ForegroundProcess { get; init; }
    public string? ForegroundCategory { get; init; }
    public bool Idle { get; init; }
}

/// <summary>
/// Pure state machine. Each reason (bedtime, total limit, per-category limit, category hours, lock)
/// walks: warned_5m → warned_1m → closed → killed → logged_off, then repeats the sign-out while in force.
/// It refuses to move the day boundary backward and reports clock jumps.
/// </summary>
public sealed class EnforcerEngine
{
    public static readonly TimeSpan Warn5 = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Warn1 = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CloseToKill = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RepeatLogoffAfter = TimeSpan.FromSeconds(60);

    private sealed class ReasonState
    {
        public int Step;                 // 0 none, 1 warned5, 2 warned1, 3 closed, 4 killed, 5 logged off
        public DateTimeOffset? ClosedAt;
        public DateTimeOffset? LastLogoffAt;
        public DateTimeOffset? RepeatNoticeAt;
        public DateTimeOffset? Deadline;
        public string NoticeId = "";
    }

    private readonly Dictionary<string, ReasonState> _states = new();
    private DateOnly? _today;
    private DateTimeOffset? _lastTick;
    private bool _clockJumpReported;

    public DateOnly CurrentDay(DateTimeOffset now)
    {
        var d = DateOnly.FromDateTime(now.LocalDateTime);
        if (_today is null || d > _today) { _today = d; _clockJumpReported = false; }
        return _today.Value;
    }

    public TickResult Tick(TickInput input)
    {
        var r = new TickResult();
        var now = input.Now;
        if (_lastTick is { } lt && now < lt - TimeSpan.FromSeconds(60) && !_clockJumpReported) { r.ClockJump = true; _clockJumpReported = true; }
        _lastTick = now;
        var p = input.Policy;

        if (!input.NoticeAccepted)
        {
            r.State = TrayState.WaitingForNotice; r.Tooltip = "Please read the Guardian notice";
            CancelAll(r, "notice not accepted");
            return r;
        }
        if (p.Paused)
        {
            CancelAll(r, $"paused until {p.PausedUntil:t}");
            r.Tooltip = $"Limits paused until {p.PausedUntil:t}";
            return r;
        }

        var seen = new HashSet<string>();

        // Lock now (parent action): 1-minute warning then sign-out; treated like bedtime.
        {
            var until = p.LockedUntil;
            Evaluate(r, input, EnforcementReason.Lock, inForce: until is not null, deadline: until is null ? p.NextLockStart : now, liftsAt: until, signOut: true, seen);
        }

        // Bedtime.
        {
            var (inForce, lifts, next) = p.Bedtime();
            if (p.IsCancelled(EnforcementReason.Bedtime)) { inForce = false; next = null; }
            Evaluate(r, input, EnforcementReason.Bedtime, inForce, inForce ? now : next, lifts, signOut: true, seen);
        }

        // Total daily limit (School excluded).
        if (p.Limit("total") is { } total && !p.IsCancelled(EnforcementReason.TotalLimit))
        {
            var remaining = total * 60 - input.UsageToday.CountedSeconds;
            var counting = input.SessionActive && !input.Idle;
            DateTimeOffset? deadline = remaining <= 0 ? now : counting ? now.AddSeconds(remaining) : null;
            Evaluate(r, input, EnforcementReason.TotalLimit, remaining <= 0, deadline, p.EndOfDay, signOut: true, seen);
        }

        // Per-category limits.
        foreach (var cat in Categories.All)
        {
            var reason = EnforcementReason.CategoryLimit(cat);
            if (p.Limit(cat) is not { } lim || p.IsCancelled(reason)) continue;
            var remaining = lim * 60 - input.UsageToday.Seconds(cat);
            var counting = input.SessionActive && !input.Idle && string.Equals(input.ForegroundCategory, cat, StringComparison.OrdinalIgnoreCase);
            DateTimeOffset? deadline = remaining <= 0 ? now : counting || remaining <= Warn5.TotalSeconds ? now.AddSeconds(remaining) : null;
            Evaluate(r, input, reason, remaining <= 0, deadline, p.EndOfDay, signOut: false, seen, cat);
        }

        // Category hours.
        foreach (var cat in Categories.All)
        {
            var reason = EnforcementReason.CategoryHours(cat);
            var (allowed, ends, restricted) = p.CategoryHours(cat);
            if (!restricted || p.IsCancelled(reason)) continue;
            var lifts = allowed ? null : NextAllowedStart(p, cat);
            Evaluate(r, input, reason, !allowed, allowed ? ends : now, lifts, signOut: false, seen, cat);
        }

        // Anything we tracked before that is gone now was cancelled by a policy change.
        foreach (var key in _states.Keys.Except(seen).ToList())
        {
            if (_states[key].Step > 0) r.Events.Add((key, EnforcementStep.Cancelled, "policy changed"));
            _states.Remove(key);
        }

        // Summaries for the tray.
        var enforcing = _states.Where(kv => kv.Value.Step >= 3).OrderBy(kv => kv.Value.Deadline).FirstOrDefault();
        if (enforcing.Key is not null)
        {
            r.State = TrayState.Enforcing; r.EnforcingReason = enforcing.Key;
            r.Tooltip = r.LiftsAt is { } l ? $"{EnforcementReason.Describe(enforcing.Key)} — lifts at {l:t}" : $"{EnforcementReason.Describe(enforcing.Key)} in force";
        }
        else
        {
            var warning = _states.Where(kv => kv.Value.Step is 1 or 2).OrderBy(kv => kv.Value.Deadline).FirstOrDefault();
            if (warning.Key is not null)
            {
                r.State = TrayState.Warning; r.WarningReason = warning.Key; r.WarningDeadline = warning.Value.Deadline;
                var mins = Math.Max(0, (int)Math.Ceiling(((warning.Value.Deadline ?? now) - now).TotalMinutes));
                r.Tooltip = $"{EnforcementReason.Describe(warning.Key)} in {mins} min";
            }
        }
        return r;
    }

    private static DateTimeOffset? NextAllowedStart(EffectivePolicy p, string cat)
    {
        if (!p.Base.CategoryHours.TryGetValue(cat, out var rules)) return null;
        var t = TimeOnly.FromDateTime(p.Now.LocalDateTime);
        DateTimeOffset? best = null;
        for (var dayOffset = 0; dayOffset < 8; dayOffset++)
        {
            var day = p.Today.AddDays(dayOffset);
            foreach (var r in rules.Where(r => r.AppliesTo(day.DayOfWeek)))
            {
                var s = TimeOnly.Parse(r.Start);
                if (dayOffset == 0 && s <= t) continue;
                var cand = day.AtLocal(s);
                if (best is null || cand < best) best = cand;
            }
            if (best is not null) return best;
        }
        return best;
    }

    private void Evaluate(TickResult r, TickInput input, string reason, bool inForce, DateTimeOffset? deadline, DateTimeOffset? liftsAt, bool signOut, HashSet<string> seen, string? category = null)
    {
        var now = input.Now;
        var st = _states.GetValueOrDefault(reason);

        if (!inForce && (deadline is null || deadline > now + Warn5))
        {
            // Nothing pending. If we had warned or enforced before, that was cancelled/lifted.
            if (st is not null)
            {
                r.Events.Add((reason, st.Step >= 3 ? EnforcementStep.Lifted : EnforcementStep.Cancelled, null));
                _states.Remove(reason);
            }
            return;
        }

        seen.Add(reason);
        st ??= _states[reason] = new ReasonState();
        st.Deadline = inForce ? now : deadline;
        var desc = EnforcementReason.Describe(reason);
        var when = deadline ?? now;

        if (!inForce)
        {
            var left = when - now;
            if (left <= Warn1 && st.Step < 2)
            {
                st.Step = 2; st.NoticeId = $"{reason}:1m:{when.ToUnixTimeSeconds()}";
                r.Events.Add((reason, EnforcementStep.Warned1, when.ToString("o")));
                r.Notice ??= new TrayNotice { Id = st.NoticeId, Title = desc, Message = signOut ? $"{desc} in 1 minute ({when:t}). You will be signed out. Save your work now." : $"{desc}: {category} apps close in 1 minute ({when:t}). Save your work now.", Dismissible = false, CountdownTo = when };
            }
            else if (left <= Warn5 && st.Step < 1)
            {
                st.Step = 1; st.NoticeId = $"{reason}:5m:{when.ToUnixTimeSeconds()}";
                r.Events.Add((reason, EnforcementStep.Warned5, when.ToString("o")));
                var mins = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
                r.Notice ??= new TrayNotice { Id = st.NoticeId, Title = desc, Message = signOut ? $"{desc} in {mins} minutes ({when:t}). Save your work." : $"{desc}: {category} apps close in {mins} minutes ({when:t}). Save your work.", Dismissible = true };
            }
            return;
        }

        // In force.
        r.LiftsAt ??= liftsAt;
        if (st.Step < 3)
        {
            st.Step = 3; st.ClosedAt = now;
            r.Events.Add((reason, EnforcementStep.Closed, null));
            if (signOut) r.CloseAll = true;
            else foreach (var pn in ProcessesFor(input, category!)) r.CloseProcesses.Add(pn);
            r.Notice ??= new TrayNotice { Id = $"{reason}:closed:{now.ToUnixTimeSeconds()}", Title = desc, Message = signOut ? $"{desc}. Signing out in 15 seconds." : $"{desc}. {category} apps are being closed." + (liftsAt is { } l ? $" Lifts at {l:t}." : ""), Dismissible = true };
            return;
        }
        if (st.Step == 3)
        {
            if (now - st.ClosedAt!.Value < CloseToKill) { if (signOut) r.CloseAll = true; else foreach (var pn in ProcessesFor(input, category!)) r.CloseProcesses.Add(pn); return; }
            st.Step = 4;
            r.Events.Add((reason, EnforcementStep.Killed, null));
            if (signOut) r.TerminateAll = true; else foreach (var pn in ProcessesFor(input, category!)) r.TerminateProcesses.Add(pn);
            if (signOut && input.SessionActive) { st.Step = 5; st.LastLogoffAt = now; r.Logoff = true; r.Events.Add((reason, EnforcementStep.LoggedOff, null)); }
            return;
        }
        if (!signOut)
        {
            // Category limit stays in force: keep closing anything in the category that reappears.
            if (input.SessionActive && !input.Idle && string.Equals(input.ForegroundCategory, category, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pn in ProcessesFor(input, category!)) r.CloseProcesses.Add(pn);
                if (input.ForegroundProcess is not null) r.CloseProcesses.Add(input.ForegroundProcess);
                if (st.RepeatNoticeAt is null || now - st.RepeatNoticeAt > TimeSpan.FromMinutes(5))
                {
                    st.RepeatNoticeAt = now;
                    r.Notice ??= new TrayNotice { Id = $"{reason}:again:{now.ToUnixTimeSeconds()}", Title = desc, Message = $"{desc} is still in force." + (liftsAt is { } l ? $" Lifts at {l:t}." : ""), Dismissible = true };
                }
            }
            return;
        }
        // Sign-out reason still in force: after each new login, notice then sign out again 60 s later, at most once a minute.
        if (!input.SessionActive) { st.RepeatNoticeAt = null; return; }
        if (st.LastLogoffAt is { } last && now - last < RepeatLogoffAfter) return;
        if (st.RepeatNoticeAt is null)
        {
            st.RepeatNoticeAt = now;
            r.Notice ??= new TrayNotice { Id = $"{reason}:repeat:{now.ToUnixTimeSeconds()}", Title = desc, Message = $"{desc} is in force" + (liftsAt is { } l ? $" until {l:t}" : "") + ". You will be signed out in 60 seconds.", Dismissible = false, CountdownTo = now + RepeatLogoffAfter };
            return;
        }
        if (now - st.RepeatNoticeAt >= RepeatLogoffAfter)
        {
            st.RepeatNoticeAt = null; st.LastLogoffAt = now;
            r.CloseAll = true; r.TerminateAll = true; r.Logoff = true;
            r.Events.Add((reason, EnforcementStep.LoggedOff, "repeat"));
        }
    }

    private static IEnumerable<string> ProcessesFor(TickInput input, string category)
    {
        foreach (var p in input.ProcessesInCategory(category)) yield return p;
        if (input.ForegroundProcess is not null && string.Equals(input.ForegroundCategory, category, StringComparison.OrdinalIgnoreCase)) yield return input.ForegroundProcess;
    }

    private void CancelAll(TickResult r, string why)
    {
        foreach (var kv in _states) if (kv.Value.Step > 0) r.Events.Add((kv.Key, EnforcementStep.Cancelled, why));
        _states.Clear();
    }

    /// <summary>Forget in-memory state (after a policy edit, so the new policy is evaluated fresh).</summary>
    public void Reset() => _states.Clear();
    public bool IsEnforcing(string reason) => _states.TryGetValue(reason, out var s) && s.Step >= 3;
    public IReadOnlyDictionary<string, int> Steps => _states.ToDictionary(kv => kv.Key, kv => kv.Value.Step);
}
