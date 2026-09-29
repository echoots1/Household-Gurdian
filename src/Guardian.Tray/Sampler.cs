using System.Diagnostics;
using Guardian.Contracts;
using Microsoft.Win32;

namespace Guardian.Tray;

/// <summary>
/// Takes one <see cref="Sample"/> of the interactive session: foreground process, window title, idle time,
/// lock state, and (when no browser extension is reporting) the browser's registrable domain.
/// <para>
/// The window title is always sent. The tray does not know which category the foreground app belongs to,
/// so it cannot apply <see cref="TrayStatus.TitleCategories"/> itself; the service categorises the sample and
/// blanks the title for categories that do not keep titles. The title never touches disk on this side.
/// </para>
/// </summary>
internal sealed class Sampler : IDisposable
{
    private readonly BrowserUrlReader _browser = new();
    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
    private volatile bool _locked;

    public Sampler()
    {
        // SystemEvents needs a message loop; WinForms provides one. SessionLock/SessionUnlock are the
        // only reliable per-session lock signals available to a process running inside that session.
        try { SystemEvents.SessionSwitch += OnSessionSwitch; }
        catch (Exception ex) { Log.Error("SessionSwitch subscription failed", ex); }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _locked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _locked = false;
                break;
        }
    }

    /// <summary>Safe to call from any thread.</summary>
    public Sample Take(bool extensionActive)
    {
        var sample = new Sample
        {
            At = DateTimeOffset.Now,
            User = Environment.UserName,
            SessionId = _sessionId,
            IdleSeconds = NativeMethods.GetIdleSeconds(),
            Locked = _locked,
        };

        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return sample;   // no foreground window (e.g. lock screen, secure desktop)

            sample.Title = NativeMethods.GetWindowTitle(hwnd);

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return sample;

            using var process = Process.GetProcessById((int)pid);
            sample.Process = process.ProcessName;
            try
            {
                // MainModule throws for processes of a higher integrity level / other bitness; the name still stands.
                sample.ExePath = process.MainModule?.FileName;
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log.Error("Foreground sample failed", ex);
        }

        if (!extensionActive && hwnd != IntPtr.Zero && BrowserUrlReader.IsBrowser(sample.Process))
        {
            var (domain, title) = _browser.Read(hwnd, sample.Title);
            sample.BrowserDomain = domain;
            sample.BrowserTitle = title;
        }

        return sample;
    }

    public void Dispose()
    {
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
    }
}
