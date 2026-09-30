using Guardian.Core.Time;

namespace Guardian.Core.Storage;

/// <summary>Nightly purge by the windows in Settings. Rollup_daily is kept forever.</summary>
public sealed class RetentionService
{
    private readonly SettingsRepo _settings;
    private readonly ActivityRepo _activity;
    private readonly RollupRepo _rollups;
    private readonly AlertRepo _alerts;
    private readonly EnforcementRepo _enforcement;

    public RetentionService(SettingsRepo settings, ActivityRepo activity, RollupRepo rollups, AlertRepo alerts, EnforcementRepo enforcement)
    {
        _settings = settings; _activity = activity; _rollups = rollups; _alerts = alerts; _enforcement = enforcement;
    }

    public (int raw, int items, int alerts, int events) Run(DateTimeOffset now)
    {
        var raw = _activity.Purge(now.AddDays(-_settings.RetentionRawDays));
        var items = _rollups.PurgeItems(now.LocalDate().AddDays(-_settings.RetentionItemDays));
        var alerts = _alerts.Purge(now.AddDays(-_settings.RetentionAlertDays));
        var events = _enforcement.Purge(now.AddDays(-_settings.RetentionAlertDays));
        return (raw, items, alerts, events);
    }
}
