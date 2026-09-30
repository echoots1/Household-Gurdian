using System.Diagnostics;

namespace Guardian.Tray;

/// <summary>
/// Sends WM_CLOSE to top-level windows on the service's request. WM_CLOSE is a polite ask: apps get to
/// save and may show their own "unsaved changes" prompt. Terminating stubborn processes and signing the
/// session out are the service's job, not the tray's. Runs on the UI thread; EnumWindows is fast and
/// PostMessage does not block.
/// </summary>
internal sealed class WindowCloser
{
    private static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(5);
    private readonly int _ownPid = Environment.ProcessId;
    private readonly Dictionary<IntPtr, DateTime> _lastClose = new();

    /// <summary>
    /// Closes every visible top-level window in this session when <paramref name="closeAll"/> is set, or
    /// only those owned by a process in <paramref name="processNames"/> (case-insensitive, no ".exe").
    /// Returns the number of windows that were sent WM_CLOSE.
    /// </summary>
    public int Close(bool closeAll, IReadOnlyCollection<string> processNames)
    {
        if (!closeAll && processNames.Count == 0) return 0;

        var wanted = new HashSet<string>(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var nameByPid = new Dictionary<uint, string>();
        var now = DateTime.UtcNow;
        var sent = 0;

        try
        {
            NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == 0 || pid == _ownPid) return true;

                    if (!nameByPid.TryGetValue(pid, out var name))
                        nameByPid[pid] = name = ProcessName(pid);
                    if (name.Length == 0) return true;
                    // Never close the shell or any Guardian component (the service relaunches this tray anyway).
                    if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return true;
                    if (name.StartsWith("Guardian", StringComparison.OrdinalIgnoreCase)) return true;
                    if (!closeAll && !wanted.Contains(name)) return true;

                    if (_lastClose.TryGetValue(hwnd, out var last) && now - last < ResendAfter) return true;
                    if (NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                    {
                        _lastClose[hwnd] = now;
                        sent++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Close window failed", ex);
                }
                return true;   // keep enumerating
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            Log.Error("EnumWindows failed", ex);
        }

        // Forget window handles we have not touched in a while; handles get reused by new windows.
        foreach (var stale in _lastClose.Where(kv => now - kv.Value > TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList())
            _lastClose.Remove(stale);

        return sent;
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";   // exited, or access denied (higher integrity) — either way, not ours to close
        }
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
