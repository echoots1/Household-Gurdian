using Guardian.Contracts;
using Guardian.Core.Alerts;
using Guardian.Core.Backup;
using Guardian.Core.Rollups;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Guardian.Service.Infrastructure;
using Guardian.Service.Win32;

namespace Guardian.Service.Workers;

/// <summary>Minute rollups and pending rebuilds; 5-minute email digest; nightly backup and retention at 03:00; tray relaunch every 10 s.</summary>
public sealed class HousekeepingWorker : BackgroundService
{
    private readonly RollupService _rollups;
    private readonly RuleRepo _rules;
    private readonly EmailDigest _digest;
    private readonly BackupService _backup;
    private readonly RetentionService _retention;
    private readonly SettingsRepo _settings;
    private readonly AlertRepo _alerts;
    private readonly EnforcementRepo _events;
    private readonly GuardianState _state;
    private readonly ISessionControl _session;
    private readonly Paths _paths;
    private readonly IClock _clock;
    private readonly ILogger<HousekeepingWorker> _log;

    private DateOnly? _lastNightly;
    private DateTimeOffset _lastDigest, _lastRollup, _lastTrayCheck;
    private bool _trayWasReporting;
    private DateTimeOffset? _lastRelaunch;

    public HousekeepingWorker(RollupService rollups, RuleRepo rules, EmailDigest digest, BackupService backup, RetentionService retention, SettingsRepo settings,
        AlertRepo alerts, EnforcementRepo events, GuardianState state, ISessionControl session, Paths paths, IClock clock, ILogger<HousekeepingWorker> log)
    {
        _rollups = rollups; _rules = rules; _digest = digest; _backup = backup; _retention = retention; _settings = settings; _alerts = alerts; _events = events;
        _state = state; _session = session; _paths = paths; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = _clock.Now;
            try
            {
                if (_rules.RebuildPending()) { var n = _rollups.RebuildAll(); _log.LogInformation("Rebuilt rollups for {Days} days after a rule change", n); }
                if (now - _lastRollup >= TimeSpan.FromMinutes(1))
                {
                    _lastRollup = now;
                    _rollups.RollDay(now.DateKey());
                    if (now.LocalDateTime.Hour == 0 && now.LocalDateTime.Minute < 5) _rollups.RollDay(now.AddDays(-1).DateKey());
                }
                if (now - _lastDigest >= EmailDigest.Interval)
                {
                    _lastDigest = now;
                    try { var sent = await _digest.FlushAsync(ct); if (sent > 0) _log.LogInformation("Emailed a digest of {N} alerts", sent); }
                    catch (Exception ex) { _log.LogWarning(ex, "Email digest failed"); }
                }
                var today = now.LocalDate();
                if (now.LocalDateTime.Hour >= 3 && _lastNightly != today)
                {
                    _lastNightly = today;
                    try { var f = _backup.Run(now); _log.LogInformation("Backup written to {File}", f); }
                    catch (Exception ex) { _log.LogError(ex, "Backup failed"); _alerts.Add(AlertType.BackupFailed, new { error = ex.Message, at = now }, now); }
                    try { var (raw, items, alerts, events) = _retention.Run(now); _log.LogInformation("Retention purged raw={Raw} items={Items} alerts={Alerts} events={Events}", raw, items, alerts, events); }
                    catch (Exception ex) { _log.LogError(ex, "Retention failed"); }
                }
                CheckTray(now);
            }
            catch (Exception ex) { _log.LogError(ex, "Housekeeping failed"); }
        }
    }

    /// <summary>If the monitored user is logged in and the tray is not reporting, relaunch it (once per 10 s) and record a tray_relaunch event the parent can see.</summary>
    private void CheckTray(DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (now - _lastTrayCheck < TimeSpan.FromSeconds(10)) return;
        _lastTrayCheck = now;
        var user = _settings.MonitoredUser;
        if (user.Length == 0 || !File.Exists(_paths.TrayExe)) return;
        var reporting = _state.SessionActive(now);
        var session = _session.FindUserSession(user);
        if (session is null) { _trayWasReporting = false; return; }
        if (reporting) { _trayWasReporting = true; return; }
        if (_session.IsProcessRunningInSession(session.SessionId, "GuardianTray")) return;
        if (_lastRelaunch is { } lr && now - lr < TimeSpan.FromSeconds(10)) return;
        _lastRelaunch = now;
        if (_session.LaunchInSession(session.SessionId, _paths.TrayExe))
        {
            if (_trayWasReporting)
            {
                _alerts.Add(AlertType.TrayRelaunch, new { at = now, session = session.SessionId, note = "The tray was not running and was started again. Not treated as an offense." }, now);
                _events.AddSession(now, "tray_relaunch", session.SessionId.ToString());
            }
            else _events.AddSession(now, "tray_started", session.SessionId.ToString());
            _log.LogInformation("Tray launched in session {Session}", session.SessionId);
        }
    }
}
