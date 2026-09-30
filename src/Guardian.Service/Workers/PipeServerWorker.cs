using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Guardian.Contracts;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Guardian.Service.Infrastructure;

namespace Guardian.Service.Workers;

/// <summary>
/// Named pipe \\.\pipe\GuardianTray. The tray connects from the user session and exchanges one JSON line per request.
/// Samples come in; TrayStatus goes out. The tray cannot change policy or data through this channel.
/// </summary>
public sealed class PipeServerWorker : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly GuardianState _state;
    private readonly SettingsRepo _settings;
    private readonly EnforcementRepo _events;
    private readonly IClock _clock;
    private readonly ILogger<PipeServerWorker> _log;

    public PipeServerWorker(GuardianState state, SettingsRepo settings, EnforcementRepo events, IClock clock, ILogger<PipeServerWorker> log)
    {
        _state = state; _settings = settings; _events = events; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try { server = Create(); }
            catch (Exception ex) { _log.LogError(ex, "Cannot create pipe"); await Task.Delay(5000, ct); continue; }
            try { await server.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) { server.Dispose(); break; }
            catch (Exception ex) { _log.LogWarning(ex, "Pipe accept failed"); server.Dispose(); await Task.Delay(1000, ct); continue; }
            _ = Task.Run(() => Serve(server, ct), ct);
        }
    }

    private static NamedPipeServerStream Create()
    {
        if (OperatingSystem.IsWindows())
        {
            // Everyone may connect (the tray runs as the child); only SYSTEM/admins could create the server side.
            var security = new PipeSecurity();
            // Clients open the pipe with GENERIC_READ|GENERIC_WRITE, which includes SYNCHRONIZE; without it a standard user gets "access denied".
            security.AddAccessRule(new PipeAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(Names.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
        }
        return new NamedPipeServerStream(Names.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            try
            {
                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    PipeResponse resp;
                    try { resp = Handle(JsonSerializer.Deserialize<PipeRequest>(line, Json) ?? new PipeRequest()); }
                    catch (Exception ex) { resp = new PipeResponse { Ok = false, Error = ex.Message, Status = _state.Status }; }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(resp, Json).AsMemory(), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex) { _log.LogDebug(ex, "Pipe session ended"); }
        }
    }

    public PipeResponse Handle(PipeRequest req)
    {
        var now = _clock.Now;
        var monitored = _settings.MonitoredUser;
        var user = req.User ?? req.Sample?.User ?? "";
        // Only the monitored account is recorded or enforced. Anyone else's tray (and every tray before setup) just sees "on".
        var isMonitored = monitored.Length > 0 && string.Equals(user, monitored, StringComparison.OrdinalIgnoreCase);
        if (!isMonitored)
            return new PipeResponse { Status = new TrayStatus { State = TrayState.On, Tooltip = monitored.Length == 0 ? "Guardian is on (setup not finished)" : "Guardian is on (this account is not monitored)", NoticeAccepted = true, MonitoredUser = monitored } };
        switch (req.Type)
        {
            case "sample":
                if (req.Sample is { } s)
                {
                    s.At = now; s.User = user;
                    _state.OnSample(s, now);
                    if (_settings.NoticeAcceptedAt is not null) _state.Sampler.OnSample(s);
                }
                break;
            case "ack_notice":
                if (_settings.NoticeAcceptedAt is null)
                {
                    _settings.Set(SettingKeys.NoticeAcceptedAt, now.ToUnix().ToString());
                    _events.AddSession(now, "notice_accepted", req.User);
                    _log.LogInformation("Notice accepted by {User}", req.User);
                }
                break;
            case "dismiss_notice":
                if (req.NoticeId is not null) _state.Dismiss(req.NoticeId);
                break;
            case "closed_windows":
            case "status":
                break;
        }
        var status = _state.Status;
        status.NoticeAccepted = _settings.NoticeAcceptedAt is not null;
        status.MonitoredUser = monitored;
        status.ExtensionActive = _state.Sampler.ExtensionActive(now);
        if (status.Notice is { } n && _state.WasDismissed(n.Id)) status.Notice = null;
        return new PipeResponse { Status = status };
    }
}
