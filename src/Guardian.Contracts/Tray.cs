using System.Text.Json.Serialization;

namespace Guardian.Contracts;

/// <summary>The five tray icon states. There is deliberately no "offline" state.</summary>
public enum TrayState { On, Warning, Enforcing, WaitingForNotice, ServiceDown }

/// <summary>What the service tells the tray on every exchange.</summary>
public sealed class TrayStatus
{
    public TrayState State { get; set; } = TrayState.On;
    public string Tooltip { get; set; } = "Guardian is on — click to see today";
    /// <summary>Currently active notice to show, if any.</summary>
    public TrayNotice? Notice { get; set; }
    /// <summary>Process names (no extension) whose windows the tray should WM_CLOSE right now.</summary>
    public List<string> CloseProcesses { get; set; } = new();
    /// <summary>True when every user-session window should be closed (bedtime / total limit / lock).</summary>
    public bool CloseAll { get; set; }
    public bool NoticeAccepted { get; set; }
    public string? EnforcingReason { get; set; }
    public DateTimeOffset? LiftsAt { get; set; }
    /// <summary>Which categories capture window titles (the tray blanks titles for the others).</summary>
    public List<string> TitleCategories { get; set; } = new() { Categories.Gaming, Categories.Other };
    public string MonitoredUser { get; set; } = "";
    public string ChildUrl { get; set; } = $"http://localhost:{Ports.Local}/me";
    /// <summary>True when a browser extension has reported in the last 2 minutes; the tray then stops reading address bars.</summary>
    public bool ExtensionActive { get; set; }
}

public sealed class TrayNotice
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>Dismissible for the 5-minute warning, not for the 1-minute countdown.</summary>
    public bool Dismissible { get; set; } = true;
    public DateTimeOffset? CountdownTo { get; set; }
}

/// <summary>One foreground observation from the session agent (the tray), taken every 5 seconds.</summary>
public sealed class Sample
{
    public DateTimeOffset At { get; set; }
    public string User { get; set; } = "";
    public int SessionId { get; set; }
    public string? Process { get; set; }
    public string? ExePath { get; set; }
    public string? Title { get; set; }
    /// <summary>Seconds since the last keyboard/mouse input.</summary>
    public int IdleSeconds { get; set; }
    public bool Locked { get; set; }
    /// <summary>Filled by the tray's address-bar fallback when no extension is active and the foreground app is a browser.</summary>
    public string? BrowserDomain { get; set; }
    public string? BrowserTitle { get; set; }
}

/// <summary>Messages on the tray pipe. JSON, one per line. The tray sends requests; the service answers with a TrayStatus.</summary>
public sealed class PipeRequest
{
    /// <summary>"status", "sample", "ack_notice", "dismiss_notice", "closed_windows".</summary>
    [JsonPropertyName("t")] public string Type { get; set; } = "status";
    public Sample? Sample { get; set; }
    public string? NoticeId { get; set; }
    public string? User { get; set; }
    public int SessionId { get; set; }
}

public sealed class PipeResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public TrayStatus? Status { get; set; }
}
