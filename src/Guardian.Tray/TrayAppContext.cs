using Guardian.Contracts;
using Timer = System.Windows.Forms.Timer;

namespace Guardian.Tray;

/// <summary>
/// Owns the tray icon and the two timers. Every 5 s: take a sample off the UI thread, exchange it with the
/// service, and apply the returned status on the UI thread. Every 1 s: tick countdown labels.
/// All UI happens on the UI thread; pipe IO and sampling happen on the thread pool.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private static readonly TrayStatus ServiceDown = new()
    {
        State = TrayState.ServiceDown,
        Tooltip = "Guardian isn't running",
        NoticeAccepted = true,   // never block the desktop while the service is absent
    };

    private readonly PipeClient _pipe = new();
    private readonly Sampler _sampler = new();
    private readonly TrayIcon _tray = new();
    private readonly WindowCloser _closer = new();
    private readonly Timer _sampleTimer = new() { Interval = 5000 };
    private readonly Timer _secondTimer = new() { Interval = 1000 };
    private readonly HashSet<string> _shownNotices = new(StringComparer.Ordinal);
    private readonly string _user = Environment.UserName;
    private readonly int _sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;

    private TrayStatus _status = ServiceDown;
    private NoticeForm? _notice;
    private NoticeScreen? _screen;
    private bool _exchangeInFlight;

    public TrayAppContext()
    {
        Log.Info("Tray started");
        _sampleTimer.Tick += async (_, _) => await SampleTickAsync();
        _secondTimer.Tick += (_, _) => _notice?.Tick();
        _sampleTimer.Start();
        _secondTimer.Start();
        Application.ApplicationExit += (_, _) => Log.Info("Tray exiting");
        _ = SampleTickAsync();   // first exchange right away rather than after 5 s
    }

    private async Task SampleTickAsync()
    {
        if (_exchangeInFlight) return;   // a slow exchange must not stack up requests
        _exchangeInFlight = true;
        try
        {
            var extensionActive = _status.ExtensionActive;
            var response = await Task.Run(() =>
            {
                var sample = _sampler.Take(extensionActive);
                return _pipe.Send(new PipeRequest { Type = "sample", Sample = sample, User = _user, SessionId = _sessionId });
            });
            // The WinForms SynchronizationContext brings us back to the UI thread here.
            Apply(response);
        }
        catch (Exception ex)
        {
            Log.Error("Sample tick failed", ex);
        }
        finally
        {
            _exchangeInFlight = false;
        }
    }

    /// <summary>Sends a request off the UI thread and applies any status the service returns with it.</summary>
    private async Task SendAsync(PipeRequest request)
    {
        try
        {
            var response = await Task.Run(() => _pipe.Send(request));
            if (response?.Status is not null) Apply(response);
        }
        catch (Exception ex)
        {
            Log.Error($"Send {request.Type} failed", ex);
        }
    }

    private void Apply(PipeResponse? response)
    {
        var status = response?.Status;
        if (status is null || response?.Ok == false)
        {
            if (response?.Error is { } err) Log.Error("Service replied with error: " + err);
            status = ServiceDown;
        }
        _status = status;
        _tray.Apply(status);

        ApplyNotice(status.Notice);
        ApplyNoticeScreen(status);
        ApplyCloseRequests(status);
    }

    private void ApplyNotice(TrayNotice? notice)
    {
        if (notice is null || string.IsNullOrEmpty(notice.Id) || _shownNotices.Contains(notice.Id)) return;
        _shownNotices.Add(notice.Id);

        _tray.ShowBalloon(notice.Title, notice.Message);

        if (_notice is { IsDisposed: false })
        {
            // A newer notice supersedes the one on screen (5-minute warning → 1-minute countdown).
            var old = _notice;
            _notice = null;
            try { old.Dispose(); } catch { }
        }
        var form = new NoticeForm(notice, id => _ = SendAsync(new PipeRequest { Type = "dismiss_notice", NoticeId = id, User = _user, SessionId = _sessionId }));
        form.FormClosed += (_, _) => { if (ReferenceEquals(_notice, form)) _notice = null; };
        _notice = form;
        form.Show();
    }

    private void ApplyNoticeScreen(TrayStatus status)
    {
        if (status.NoticeAccepted)
        {
            if (_screen is { IsDisposed: false })
            {
                var s = _screen;
                _screen = null;
                try { s.Close(); s.Dispose(); } catch { }
            }
            return;
        }

        var childUrl = string.IsNullOrWhiteSpace(status.ChildUrl) ? TrayIcon.DefaultChildUrl : status.ChildUrl;
        var text = NoticeText.ToPlainText(NoticeText.Render(status.MonitoredUser, childUrl));

        if (_screen is null || _screen.IsDisposed)
        {
            _screen = new NoticeScreen(text, () => _ = SendAsync(new PipeRequest { Type = "ack_notice", User = _user, SessionId = _sessionId }));
            _screen.Show();
        }
        else
        {
            _screen.SetText(text);
            if (!_screen.Visible) _screen.Show();   // the child closed or hid it; show it again, no fuss
        }
    }

    private void ApplyCloseRequests(TrayStatus status)
    {
        if (!status.CloseAll && status.CloseProcesses.Count == 0) return;
        try
        {
            _closer.Close(status.CloseAll, status.CloseProcesses);
        }
        catch (Exception ex)
        {
            Log.Error("Close requests failed", ex);
        }
        // Fire and forget: the service only needs to know we acted on the request.
        _ = SendAsync(new PipeRequest { Type = "closed_windows", User = _user, SessionId = _sessionId });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sampleTimer.Dispose();
            _secondTimer.Dispose();
            _notice?.Dispose();
            _screen?.Dispose();
            _tray.Dispose();
            _sampler.Dispose();
            _pipe.Dispose();
        }
        base.Dispose(disposing);
    }
}
