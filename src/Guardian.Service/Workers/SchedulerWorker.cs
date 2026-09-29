using Guardian.Contracts;
using Guardian.Core.Enforcement;
using Guardian.Core.Policies;
using Guardian.Core.Rollups;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Guardian.Service.Infrastructure;
using Guardian.Service.Win32;

namespace Guardian.Service.Workers;

/// <summary>15-second tick: effective policy → enforcer engine → actions in the user session. Policy edits apply on the next tick.</summary>
public sealed class SchedulerWorker : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private readonly GuardianState _state;
    private readonly PolicyRepo _policies;
    private readonly RollupRepo _rollups;
    private readonly RollupService _rollupService;
    private readonly RuleRepo _rules;
    private readonly SettingsRepo _settings;
    private readonly EnforcementRepo _events;
    private readonly AlertRepo _alerts;
    private readonly ISessionControl _session;
    private readonly IClock _clock;
    private readonly ILogger<SchedulerWorker> _log;
    public EnforcerEngine Engine { get; } = new();
    private int _lastPolicyVersion = -1;

    public SchedulerWorker(GuardianState state, PolicyRepo policies, RollupRepo rollups, RollupService rollupService, RuleRepo rules, SettingsRepo settings,
        EnforcementRepo events, AlertRepo alerts, ISessionControl session, IClock clock, ILogger<SchedulerWorker> log)
    {
        _state = state; _policies = policies; _rollups = rollups; _rollupService = rollupService; _rules = rules; _settings = settings;
        _events = events; _alerts = alerts; _session = session; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        RunOnce();
        while (await timer.WaitForNextTickAsync(ct)) RunOnce();
    }

    public void RunOnce()
    {
        try { RunTick(); }
        catch (Exception ex) { _log.LogError(ex, "Scheduler tick failed"); }
    }

    private void RunTick()
    {
        var now = _clock.Now;
        var today = Engine.CurrentDay(now);
        var policy = _policies.Current();
        if (policy.Version != _lastPolicyVersion) { Engine.Reset(); _lastPolicyVersion = policy.Version; }
        var exceptions = _policies.ExceptionsAround(today);
        var ep = new EffectivePolicy(policy, today, now, exceptions);

        _rollupService.RollDay(today.DateKey());
        var usage = _rollups.Day(today.DateKey());
        var sessionActive = _state.SessionActive(now);
        if (!sessionActive) _state.Sampler.OnNoSession();

        var rules = _rules.All();
        var input = new TickInput
        {
            Now = now, Policy = ep, UsageToday = usage,
            NoticeAccepted = _settings.NoticeAcceptedAt is not null && _settings.MonitoredUser.Length > 0,
            SessionActive = sessionActive, SessionStartedAt = _state.SessionStartedAt,
            ProcessesInCategory = cat => rules.Where(r => r.MatchType == RuleMatch.Process && string.Equals(r.Category, cat, StringComparison.OrdinalIgnoreCase) && !r.Pattern.EndsWith('*')).Select(r => r.Pattern).ToList(),
            ForegroundProcess = _state.Sampler.LastProcess, ForegroundCategory = _state.Sampler.LastCategory, Idle = _state.Sampler.LastIdle,
        };
        var r = Engine.Tick(input);

        foreach (var (reason, step, detail) in r.Events)
        {
            _events.Add(now, reason, step, policy.Version, detail);
            _log.LogInformation("Enforcement {Reason} {Step} {Detail}", reason, step, detail);
        }
        if (r.ClockJump) _alerts.Add(AlertType.ClockJump, new { at = now, note = "System clock moved backwards; the day boundary was kept." }, now);

        var sid = _state.SessionId;
        if (sid is { } id && sessionActive)
        {
            if (r.TerminateAll) _log.LogInformation("Terminated {N} processes", _session.TerminateAllUserProcesses(id));
            else if (r.TerminateProcesses.Count > 0) _log.LogInformation("Terminated {N} of {Names}", _session.TerminateProcesses(id, r.TerminateProcesses), string.Join(",", r.TerminateProcesses));
            if (r.Logoff)
            {
                _events.AddSession(now, "logoff", r.EnforcingReason);
                _state.LastLogoffAt = now;
                if (_session.Logoff(id)) _state.SessionEnded();
            }
        }

        var status = new TrayStatus
        {
            State = r.State, Tooltip = r.Tooltip, Notice = r.Notice ?? KeepCountdown(_state.Status.Notice, now),
            CloseAll = r.CloseAll, EnforcingReason = r.EnforcingReason, LiftsAt = r.LiftsAt,
            TitleCategories = _settings.TitleCategories.ToList(), MonitoredUser = _settings.MonitoredUser,
            NoticeAccepted = _settings.NoticeAcceptedAt is not null, ExtensionActive = _state.Sampler.ExtensionActive(now),
        };
        foreach (var p in r.CloseProcesses) status.CloseProcesses.Add(p);
        if (ep.Paused && status.State == TrayState.On) status.Tooltip = r.Tooltip;
        _state.Status = status;
    }

    /// <summary>A non-dismissible countdown notice stays until its deadline passes, even if this tick produced no new notice.</summary>
    private static TrayNotice? KeepCountdown(TrayNotice? previous, DateTimeOffset now) =>
        previous is { Dismissible: false, CountdownTo: { } to } && to > now ? previous : null;
}
