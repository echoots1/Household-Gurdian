using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Guardian.Service.Win32;

/// <summary>P/Invoke into wtsapi32 / advapi32 / userenv / mpr. Runs as LocalSystem in session 0.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSessionControl : ISessionControl
{
    private readonly ILogger<WindowsSessionControl> _log;
    public WindowsSessionControl(ILogger<WindowsSessionControl> log) => _log = log;

    /// <summary>Processes we never terminate inside the user session.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "sihost", "ctfmon", "taskhostw", "runtimebroker", "startmenuexperiencehost",
        "shellexperiencehost", "searchhost", "searchui", "textinputhost", "fontdrvhost", "logonui", "lockapp", "userinit",
        "svchost", "conhost", "guardiantray", "guardiansvc", "smartscreen", "securityhealthsystray", "applicationframehost",
        "systemsettings", "wininit", "services", "lsass", "audiodg", "spoolsv",
    };

    public IReadOnlyList<UserSession> Sessions()
    {
        var list = new List<UserSession>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pSessions, out var count)) return list;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(pSessions + i * size);
                var user = QueryString(si.SessionId, WTS_INFO_CLASS.WTSUserName);
                if (string.IsNullOrEmpty(user)) continue;
                list.Add(new UserSession(si.SessionId, user, si.State == WTS_CONNECTSTATE_CLASS.WTSActive));
            }
        }
        finally { WTSFreeMemory(pSessions); }
        return list;
    }

    public UserSession? FindUserSession(string userName) =>
        Sessions().Where(s => string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Active).FirstOrDefault();

    public bool LaunchInSession(int sessionId, string exePath, string? arguments = null)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var userToken)) { _log.LogWarning("WTSQueryUserToken failed: {Err}", Marshal.GetLastWin32Error()); return false; }
        try
        {
            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, out var primary))
                throw new Win32Exception();
            try
            {
                if (!CreateEnvironmentBlock(out var env, primary, false)) env = IntPtr.Zero;
                try
                {
                    var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                    var cmd = "\"" + exePath + "\"" + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
                    var ok = CreateProcessAsUser(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE, env, Path.GetDirectoryName(exePath), ref si, out var pi);
                    if (!ok) { _log.LogWarning("CreateProcessAsUser failed: {Err}", Marshal.GetLastWin32Error()); return false; }
                    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                    return true;
                }
                finally { if (env != IntPtr.Zero) DestroyEnvironmentBlock(env); }
            }
            finally { CloseHandle(primary); }
        }
        catch (Exception ex) { _log.LogWarning(ex, "LaunchInSession failed"); return false; }
        finally { CloseHandle(userToken); }
    }

    public bool Logoff(int sessionId)
    {
        var ok = WTSLogoffSession(IntPtr.Zero, (uint)sessionId, false);
        if (!ok) _log.LogWarning("WTSLogoffSession({Session}) failed: {Err}", sessionId, Marshal.GetLastWin32Error());
        return ok;
    }

    public int TerminateProcesses(int sessionId, IReadOnlyCollection<string> names)
    {
        var set = new HashSet<string>(names.Select(Strip), StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != sessionId || !set.Contains(p.ProcessName) || Protected.Contains(p.ProcessName)) continue;
                p.Kill(entireProcessTree: true); n++;
            }
            catch (Exception ex) { _log.LogDebug(ex, "Kill {Name} failed", p.ProcessName); }
            finally { p.Dispose(); }
        }
        return n;
    }

    public int TerminateAllUserProcesses(int sessionId)
    {
        var n = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != sessionId || Protected.Contains(p.ProcessName)) continue;
                p.Kill(entireProcessTree: false); n++;
            }
            catch (Exception ex) { _log.LogDebug(ex, "Kill {Name} failed", p.ProcessName); }
            finally { p.Dispose(); }
        }
        return n;
    }

    public bool IsProcessRunningInSession(int sessionId, string processName)
    {
        var name = Strip(processName);
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.SessionId == sessionId && string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    public IReadOnlyList<(string name, bool isAdmin)> LocalUsers()
    {
        var result = new List<(string, bool)>();
        try
        {
            using var ctx = new System.DirectoryServices.AccountManagement.PrincipalContext(System.DirectoryServices.AccountManagement.ContextType.Machine);
            using var searcher = new System.DirectoryServices.AccountManagement.PrincipalSearcher(new System.DirectoryServices.AccountManagement.UserPrincipal(ctx));
            foreach (var p in searcher.FindAll())
            {
                if (p is not System.DirectoryServices.AccountManagement.UserPrincipal u || u.SamAccountName is null) continue;
                if (u.Enabled == false) continue;
                if (u.SamAccountName.EndsWith('$') || u.SamAccountName is "DefaultAccount" or "WDAGUtilityAccount" or "Guest") continue;
                result.Add((u.SamAccountName, IsAdministrator(u.SamAccountName)));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not enumerate local users"); }
        return result;
    }

    public bool IsAdministrator(string userName)
    {
        try
        {
            using var ctx = new System.DirectoryServices.AccountManagement.PrincipalContext(System.DirectoryServices.AccountManagement.ContextType.Machine);
            using var user = System.DirectoryServices.AccountManagement.UserPrincipal.FindByIdentity(ctx, userName);
            if (user is null) return false;
            using var admins = System.DirectoryServices.AccountManagement.GroupPrincipal.FindByIdentity(ctx, new System.Security.Principal.SecurityIdentifier("S-1-5-32-544").Value);
            return admins is not null && user.IsMemberOf(admins);
        }
        catch { return false; }
    }

    public IDisposable? ConnectShare(string uncPath, string user, string password)
    {
        var root = uncPath.TrimEnd('\\');
        var parts = root.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) root = @"\\" + parts[0] + @"\" + parts[1];
        var nr = new NETRESOURCE { dwType = 1, lpRemoteName = root };
        var err = WNetAddConnection2(ref nr, password, user, 0);
        if (err != 0 && err != 1219 /* already connected */) throw new Win32Exception(err, $"Could not connect to {root}");
        return new ShareHandle(root);
    }

    private sealed class ShareHandle : IDisposable
    {
        private readonly string _root;
        public ShareHandle(string root) => _root = root;
        public void Dispose() { try { WNetCancelConnection2(_root, 0, false); } catch { } }
    }

    private static string Strip(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    private static string? QueryString(int sessionId, WTS_INFO_CLASS cls)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, cls, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); } finally { WTSFreeMemory(buf); }
    }

    // ---- P/Invoke ----
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const uint CREATE_NEW_CONSOLE = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public WTS_CONNECTSTATE_CLASS State; }
    private enum WTS_CONNECTSTATE_CLASS { WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected, WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit }
    private enum WTS_INFO_CLASS { WTSInitialProgram, WTSApplicationName, WTSWorkingDirectory, WTSOEMId, WTSSessionId, WTSUserName, WTSWinStationName, WTSDomainName, WTSConnectState }
    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NETRESOURCE { public int dwScope, dwType, dwDisplayType, dwUsage; public string? lpLocalName, lpRemoteName, lpComment, lpProvider; }

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr pMemory);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, bool bWait);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, SECURITY_IMPERSONATION_LEVEL impersonationLevel, TOKEN_TYPE tokenType, out IntPtr phNewToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref NETRESOURCE lpNetResource, string? lpPassword, string? lpUserName, int dwFlags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2(string lpName, int dwFlags, bool fForce);
}
