using Guardian.Contracts;
using Guardian.Core.Enforcement;
using Guardian.Core.Policies;
using Guardian.Core.Storage;
using Xunit;

namespace Guardian.Core.Tests;

public class EnforcerTests
{
    // Monday 2026-10-05 is a school night.
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static DateTimeOffset At(int h, int m, int s = 0) => new(Monday.ToDateTime(new TimeOnly(h, m, s)), TimeZoneInfo.Local.GetUtcOffset(Monday.ToDateTime(new TimeOnly(h, m, s))));

    private static TickInput Input(DateTimeOffset now, Contracts.Policy p, IReadOnlyList<PolicyException>? ex = null, int gamingSec = 0, int otherSec = 0, int schoolSec = 0,
        string? fg = null, string? fgCat = null, bool session = true, bool accepted = true, bool idle = false)
    {
        var ep = new EffectivePolicy(p, DateOnly.FromDateTime(now.LocalDateTime), now, ex ?? Array.Empty<PolicyException>());
        var totals = new DailyTotals(now.ToString("yyyy-MM-dd"), new(StringComparer.OrdinalIgnoreCase) { [Categories.Gaming] = gamingSec, [Categories.Other] = otherSec, [Categories.School] = schoolSec });
        return new TickInput
        {
            Now = now, Policy = ep, UsageToday = totals, NoticeAccepted = accepted, SessionActive = session, SessionStartedAt = now.AddHours(-1),
            ProcessesInCategory = c => c == Categories.Gaming ? new[] { "steam", "robloxplayerbeta" } : Array.Empty<string>(),
            ForegroundProcess = fg, ForegroundCategory = fgCat, Idle = idle,
        };
    }

    [Fact]
    public void Bedtime_warns_at_5_and_1_minutes_then_signs_out_at_15_seconds()
    {
        var p = Contracts.Policy.Default(); // school night bedtime 21:00
        var e = new EnforcerEngine();

        var r = e.Tick(Input(At(20, 54, 45), p));
        Assert.Empty(r.Events); Assert.Equal(TrayState.On, r.State);

        r = e.Tick(Input(At(20, 55, 0), p));
        Assert.Contains(r.Events, ev => ev.reason == EnforcementReason.Bedtime && ev.step == EnforcementStep.Warned5);
        Assert.NotNull(r.Notice); Assert.True(r.Notice!.Dismissible); Assert.Contains("Bedtime", r.Notice.Message);
        Assert.Equal(TrayState.Warning, r.State);

        r = e.Tick(Input(At(20, 57, 0), p));
        Assert.Empty(r.Events); // no repeat

        r = e.Tick(Input(At(20, 59, 0), p));
        Assert.Contains(r.Events, ev => ev.step == EnforcementStep.Warned1);
        Assert.False(r.Notice!.Dismissible); Assert.NotNull(r.Notice.CountdownTo);

        r = e.Tick(Input(At(21, 0, 0), p));
        Assert.Contains(r.Events, ev => ev.step == EnforcementStep.Closed);
        Assert.True(r.CloseAll); Assert.False(r.Logoff);
        Assert.Equal(TrayState.Enforcing, r.State);

        r = e.Tick(Input(At(21, 0, 10), p));
        Assert.False(r.Logoff);

        r = e.Tick(Input(At(21, 0, 15), p));
        Assert.True(r.TerminateAll); Assert.True(r.Logoff);
        Assert.Contains(r.Events, ev => ev.step == EnforcementStep.LoggedOff);
        Assert.NotNull(r.LiftsAt);
        Assert.Equal(new TimeOnly(6, 30), TimeOnly.FromDateTime(r.LiftsAt!.Value.LocalDateTime));

        // Child logs back in at 21:05: notice, then sign-out 60 s later, not sooner.
        r = e.Tick(Input(At(21, 5, 0), p, session: true));
        Assert.NotNull(r.Notice); Assert.False(r.Logoff);
        r = e.Tick(Input(At(21, 5, 30), p, session: true));
        Assert.False(r.Logoff);
        r = e.Tick(Input(At(21, 6, 0), p, session: true));
        Assert.True(r.Logoff);
        Assert.Contains(r.Events, ev => ev.step == EnforcementStep.LoggedOff && ev.detail == "repeat");
    }

    [Fact]
    public void Exception_saved_at_858_cancels_bedtime()
    {
        var p = Contracts.Policy.Default();
        var e = new EnforcerEngine();
        e.Tick(Input(At(20, 56, 0), p));
        var ex = new[] { new PolicyException { Date = Monday.ToString("yyyy-MM-dd"), NoBedtime = true, Note = "no bedtime tonight" } };
        var r = e.Tick(Input(At(20, 58, 0), p, ex));
        Assert.Contains(r.Events, ev => ev.reason == EnforcementReason.Bedtime && ev.step == EnforcementStep.Cancelled);
        r = e.Tick(Input(At(21, 30, 0), p, ex));
        Assert.Empty(r.Events); Assert.False(r.CloseAll); Assert.Equal(TrayState.On, r.State);
        // Next morning the window from "last night" is also lifted.
        var tue = At(6, 0, 0).AddDays(1);
        r = e.Tick(Input(tue, p, ex));
        Assert.False(r.CloseAll);
    }

    [Fact]
    public void Gaming_limit_closes_steam_and_stays_in_force_until_midnight()
    {
        var p = Contracts.Policy.Default(); // Gaming 90 min
        var e = new EnforcerEngine();
        var r = e.Tick(Input(At(15, 0, 0), p, gamingSec: 85 * 60, fg: "steam", fgCat: Categories.Gaming));
        Assert.Contains(r.Events, ev => ev.reason == "limit:Gaming" && ev.step == EnforcementStep.Warned5);
        Assert.Contains("Gaming", r.Notice!.Message);
        r = e.Tick(Input(At(15, 4, 0), p, gamingSec: 89 * 60, fg: "steam", fgCat: Categories.Gaming));
        Assert.Contains(r.Events, ev => ev.step == EnforcementStep.Warned1);
        r = e.Tick(Input(At(15, 5, 0), p, gamingSec: 90 * 60, fg: "steam", fgCat: Categories.Gaming));
        Assert.Contains(r.Events, ev => ev.step == EnforcementStep.Closed);
        Assert.Contains("steam", r.CloseProcesses); Assert.False(r.CloseAll);
        r = e.Tick(Input(At(15, 5, 15), p, gamingSec: 90 * 60, fg: "steam", fgCat: Categories.Gaming));
        Assert.Contains("steam", r.TerminateProcesses); Assert.False(r.Logoff);
        Assert.Equal(TrayState.Enforcing, r.State);
        Assert.Equal(Monday.AddDays(1), DateOnly.FromDateTime(r.LiftsAt!.Value.LocalDateTime));
        // Steam opened again later: closed again.
        r = e.Tick(Input(At(18, 0, 0), p, gamingSec: 90 * 60, fg: "steam", fgCat: Categories.Gaming));
        Assert.Contains("steam", r.CloseProcesses);
        // Adding minutes lifts it.
        var ex = new[] { new PolicyException { Date = Monday.ToString("yyyy-MM-dd"), AddMinutes = 30 } };
        r = e.Tick(Input(At(18, 1, 0), p, ex, gamingSec: 90 * 60, fg: "steam", fgCat: Categories.Gaming));
        Assert.Contains(r.Events, ev => ev.reason == "limit:Gaming" && ev.step == EnforcementStep.Lifted);
        Assert.Empty(r.CloseProcesses);
    }

    [Fact]
    public void Total_limit_excludes_school_and_signs_out()
    {
        var p = Contracts.Policy.Default(); // total 240
        var e = new EnforcerEngine();
        var r = e.Tick(Input(At(15, 0, 0), p, schoolSec: 300 * 60, gamingSec: 60 * 60, otherSec: 60 * 60, fg: "mspaint", fgCat: Categories.Other));
        Assert.Empty(r.Events);
        r = e.Tick(Input(At(15, 0, 0), p, gamingSec: 120 * 60, otherSec: 120 * 60, fg: "mspaint", fgCat: Categories.Other));
        Assert.Contains(r.Events, ev => ev.reason == EnforcementReason.TotalLimit && ev.step == EnforcementStep.Closed);
        Assert.True(r.CloseAll);
    }

    [Fact]
    public void Category_hours_close_outside_the_window()
    {
        var p = Contracts.Policy.Default();
        p.CategoryHours[Categories.Gaming] = new() { new CategoryHoursRule { Days = new() { "Mon" }, Start = "16:00", End = "18:00" } };
        var e = new EnforcerEngine();
        var r = e.Tick(Input(At(17, 54, 0), p, fg: "steam", fgCat: Categories.Gaming));
        Assert.Empty(r.Events);
        r = e.Tick(Input(At(17, 55, 0), p, fg: "steam", fgCat: Categories.Gaming));
        Assert.Contains(r.Events, ev => ev.reason == "hours:Gaming" && ev.step == EnforcementStep.Warned5);
        r = e.Tick(Input(At(18, 0, 0), p, fg: "steam", fgCat: Categories.Gaming));
        Assert.Contains(r.Events, ev => ev.step == EnforcementStep.Closed);
        Assert.Contains("steam", r.CloseProcesses);
        Assert.NotNull(r.LiftsAt); // next Monday 16:00
        Assert.Equal(DayOfWeek.Monday, r.LiftsAt!.Value.DayOfWeek);
    }

    [Fact]
    public void Nothing_happens_before_the_notice_is_accepted_or_while_paused()
    {
        var p = Contracts.Policy.Default();
        var e = new EnforcerEngine();
        var r = e.Tick(Input(At(21, 30, 0), p, accepted: false));
        Assert.Equal(TrayState.WaitingForNotice, r.State); Assert.False(r.CloseAll); Assert.Empty(r.Events);
        var ex = new[] { new PolicyException { Date = Monday.ToString("yyyy-MM-dd"), PauseUntil = At(23, 0, 0) } };
        r = e.Tick(Input(At(21, 30, 0), p, ex));
        Assert.False(r.CloseAll); Assert.Equal(TrayState.On, r.State);
    }

    [Fact]
    public void Lock_now_gives_one_minute_then_signs_out()
    {
        var p = Contracts.Policy.Default();
        var e = new EnforcerEngine();
        var ex = new[] { new PolicyException { Date = Monday.ToString("yyyy-MM-dd"), LockFrom = At(15, 1, 0), LockUntil = At(16, 0, 0) } };
        var r = e.Tick(Input(At(15, 0, 0), p, ex));
        Assert.Contains(r.Events, ev => ev.reason == EnforcementReason.Lock && ev.step == EnforcementStep.Warned1);
        Assert.False(r.Notice!.Dismissible);
        r = e.Tick(Input(At(15, 1, 0), p, ex));
        Assert.True(r.CloseAll);
        r = e.Tick(Input(At(15, 1, 15), p, ex));
        Assert.True(r.Logoff);
        r = e.Tick(Input(At(16, 0, 0), p, ex));
        Assert.Contains(r.Events, ev => ev.step == EnforcementStep.Lifted);
    }

    [Fact]
    public void Clock_moving_backwards_is_reported_and_day_does_not_go_back()
    {
        var e = new EnforcerEngine();
        var p = Contracts.Policy.Default();
        Assert.Equal(Monday, e.CurrentDay(At(10, 0, 0)));
        e.Tick(Input(At(10, 0, 0), p));
        var r = e.Tick(Input(At(9, 0, 0), p));
        Assert.True(r.ClockJump);
        Assert.Equal(Monday, e.CurrentDay(At(23, 0, 0).AddDays(-1)));
    }

    [Fact]
    public void Weekend_bedtime_and_midnight_crossing()
    {
        var p = Contracts.Policy.Default();
        var sat = new DateOnly(2026, 10, 10);
        var satNight = new DateTimeOffset(sat.ToDateTime(new TimeOnly(22, 0)), TimeZoneInfo.Local.GetUtcOffset(sat.ToDateTime(new TimeOnly(22, 0))));
        var ep = new EffectivePolicy(p, sat, satNight, Array.Empty<PolicyException>());
        Assert.False(ep.Bedtime().inForce);
        var sunMorning = satNight.AddHours(9); // Sunday 07:00 — weekend window ends 07:30
        ep = new EffectivePolicy(p, sat.AddDays(1), sunMorning, Array.Empty<PolicyException>());
        Assert.True(ep.Bedtime().inForce);
        ep = new EffectivePolicy(p, sat.AddDays(1), sunMorning.AddHours(1), Array.Empty<PolicyException>());
        Assert.False(ep.Bedtime().inForce);
    }
}
