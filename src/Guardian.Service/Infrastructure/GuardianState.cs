using Guardian.Contracts;
using Guardian.Core.Sampling;

namespace Guardian.Service.Infrastructure;

/// <summary>Runtime state shared between the workers and the web UI. Small and lock-protected.</summary>
public sealed class GuardianState
{
    private readonly object _lock = new();
    private TrayStatus _status = new();

    public SamplerEngine Sampler { get; }
    public GuardianState(SamplerEngine sampler) => Sampler = sampler;

    // ---- interactive session of the monitored user, as reported by the tray ----
    public DateTimeOffset? LastSampleAt { get; private set; }
    public DateTimeOffset? SessionStartedAt { get; private set; }
    public int? SessionId { get; private set; }
    public string? SessionUser { get; private set; }
    public DateTimeOffset? TrayConnectedAt { get; private set; }
    public DateTimeOffset? LastLogoffAt { get; set; }
    public string InstalledVersion { get; set; } = typeof(GuardianState).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string CertFingerprint { get; set; } = "";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public bool ExtensionEverReported { get; set; }

    public void OnSample(Sample s, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (SessionStartedAt is null || LastSampleAt is null || (now - LastSampleAt.Value).TotalSeconds > 90 || SessionId != s.SessionId)
                SessionStartedAt = now;
            LastSampleAt = now; SessionId = s.SessionId; SessionUser = s.User; TrayConnectedAt ??= now;
        }
    }

    /// <summary>The tray reports every 5 s; 20 s of silence means the session (or the tray) is gone.</summary>
    public bool SessionActive(DateTimeOffset now) => LastSampleAt is { } t && (now - t).TotalSeconds <= 20;

    public void SessionEnded()
    {
        lock (_lock) { SessionStartedAt = null; LastSampleAt = null; SessionId = null; }
    }

    public TrayStatus Status
    {
        get { lock (_lock) return _status; }
        set { lock (_lock) _status = value; }
    }

    // ---- notice bookkeeping ----
    private readonly HashSet<string> _dismissed = new();
    public void Dismiss(string id) { lock (_lock) _dismissed.Add(id); }
    public bool WasDismissed(string id) { lock (_lock) return _dismissed.Contains(id); }
}
