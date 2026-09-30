namespace Guardian.Contracts;

/// <summary>What the browser extension posts to the loopback listener when an active-tab interval ends.</summary>
public sealed class TabReport
{
    /// <summary>Registrable domain (eTLD+1). Path and query are never sent.</summary>
    public string Domain { get; set; } = "";
    /// <summary>Full host, kept in the raw record only.</summary>
    public string? Subdomain { get; set; }
    public string? Title { get; set; }
    public int ActiveSeconds { get; set; }
    /// <summary>"chrome" or "edge" (or "tray" for the address-bar fallback).</summary>
    public string Browser { get; set; } = "";
    /// <summary>Client-side interval end, optional; the service uses receipt time when absent.</summary>
    public DateTimeOffset? EndedAt { get; set; }
}

public static class EnforcementReason
{
    public const string Bedtime = "bedtime";
    public const string TotalLimit = "total";
    public const string Lock = "lock";
    public static string CategoryLimit(string category) => $"limit:{category}";
    public static string CategoryHours(string category) => $"hours:{category}";

    public static bool IsSignOut(string reason) => reason is Bedtime or TotalLimit or Lock;
    public static string? Category(string reason)
    {
        var i = reason.IndexOf(':');
        return i < 0 ? null : reason[(i + 1)..];
    }
    public static string Describe(string reason) => reason switch
    {
        Bedtime => "Bedtime",
        TotalLimit => "Daily screen time limit",
        Lock => "Locked by parent",
        _ when reason.StartsWith("limit:") => $"{Category(reason)} time limit",
        _ when reason.StartsWith("hours:") => $"{Category(reason)} hours",
        _ => reason,
    };
}

public static class EnforcementStep
{
    public const string Warned5 = "warned_5m";
    public const string Warned1 = "warned_1m";
    public const string Closed = "closed";
    public const string Killed = "killed";
    public const string LoggedOff = "logged_off";
    public const string Cancelled = "cancelled";
    public const string Lifted = "lifted";
}

public static class AlertType
{
    public const string ContentFlag = "content_flag";
    public const string TimeRequest = "time_request";
    public const string TrayRelaunch = "tray_relaunch";
    public const string ClockJump = "clock_jump";
    public const string BackupFailed = "backup_failed";
    public const string ExtensionMissing = "extension_missing";
}

public static class SettingKeys
{
    public const string MonitoredUser = "monitored_user";
    public const string MonitoredUserIsAdmin = "monitored_user_is_admin";
    public const string ParentPasswordHash = "parent_password_hash";
    public const string NoticeAcceptedAt = "notice_accepted_at";
    public const string SmtpHost = "smtp_host";
    public const string SmtpPort = "smtp_port";
    public const string SmtpUser = "smtp_user";
    public const string SmtpPassword = "smtp_password"; // DPAPI-protected
    public const string SmtpFrom = "smtp_from";
    public const string SmtpTo = "smtp_to";
    public const string SmtpUseTls = "smtp_tls";
    public const string BackupPath = "backup_path";
    public const string BackupUser = "backup_user";
    public const string BackupPassword = "backup_password"; // DPAPI-protected
    public const string RetentionRawDays = "retention_raw_days";
    public const string RetentionItemDays = "retention_item_days";
    public const string RetentionAlertDays = "retention_alert_days";
    public const string CertThumbprint = "cert_thumbprint";
    public const string TitleCategories = "title_categories";
    public const string SetupCompleted = "setup_completed";
    public const string ListsRefreshedAt = "lists_refreshed_at";
    public const string ListSources = "list_sources";
    public const string SchemaVersion = "schema_version";
    public const string InstalledVersion = "installed_version";
    public const string FullPathCategories = "full_path_categories";
}
