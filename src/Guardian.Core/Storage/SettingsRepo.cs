using Dapper;
using Guardian.Contracts;

namespace Guardian.Core.Storage;

public sealed class SettingsRepo
{
    private readonly Db _db;
    public SettingsRepo(Db db) => _db = db;

    public string? Get(string key)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@key", new { key });
    }

    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;
    public bool GetBool(string key, bool fallback) => Get(key) is { } s ? s is "1" or "true" or "True" : fallback;

    public void Set(string key, string? value)
    {
        using var c = _db.Open();
        if (value is null) c.Execute("DELETE FROM settings WHERE key=@key", new { key });
        else c.Execute("INSERT INTO settings(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", new { key, value });
    }

    public Dictionary<string, string> All()
    {
        using var c = _db.Open();
        return c.Query<(string key, string value)>("SELECT key, value FROM settings").ToDictionary(r => r.key, r => r.value ?? "");
    }

    public string MonitoredUser => Get(SettingKeys.MonitoredUser) ?? "";
    public DateTimeOffset? NoticeAcceptedAt => long.TryParse(Get(SettingKeys.NoticeAcceptedAt), out var s) ? Time.TimeExt.FromUnix(s) : null;
    public bool SetupCompleted => GetBool(SettingKeys.SetupCompleted, false);

    public string[] TitleCategories =>
        (Get(SettingKeys.TitleCategories) ?? $"{Categories.Gaming},{Categories.Other}")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int RetentionRawDays => GetInt(SettingKeys.RetentionRawDays, 90);
    public int RetentionItemDays => GetInt(SettingKeys.RetentionItemDays, 365);
    public int RetentionAlertDays => GetInt(SettingKeys.RetentionAlertDays, 365);
}
