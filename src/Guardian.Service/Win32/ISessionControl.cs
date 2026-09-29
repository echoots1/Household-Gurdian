namespace Guardian.Service.Win32;

public sealed record UserSession(int SessionId, string UserName, bool Active);

/// <summary>The few things the service must do inside the user's session from session 0. Windows-only; a no-op elsewhere.</summary>
public interface ISessionControl
{
    IReadOnlyList<UserSession> Sessions();
    UserSession? FindUserSession(string userName);
    /// <summary>Start an exe in the given session as that session's user (WTSQueryUserToken + CreateProcessAsUser).</summary>
    bool LaunchInSession(int sessionId, string exePath, string? arguments = null);
    bool Logoff(int sessionId);
    /// <summary>Terminate processes in the session whose name (no .exe) is in the set.</summary>
    int TerminateProcesses(int sessionId, IReadOnlyCollection<string> names);
    /// <summary>Terminate every process in the session except shell/system ones and Guardian itself.</summary>
    int TerminateAllUserProcesses(int sessionId);
    bool IsProcessRunningInSession(int sessionId, string processName);
    IReadOnlyList<(string name, bool isAdmin)> LocalUsers();
    bool IsAdministrator(string userName);
    /// <summary>Map a UNC share with a credential for the duration of the returned handle.</summary>
    IDisposable? ConnectShare(string uncPath, string user, string password);
}

public sealed class NoopSessionControl : ISessionControl
{
    public IReadOnlyList<UserSession> Sessions() => Array.Empty<UserSession>();
    public UserSession? FindUserSession(string userName) => null;
    public bool LaunchInSession(int sessionId, string exePath, string? arguments = null) => false;
    public bool Logoff(int sessionId) => false;
    public int TerminateProcesses(int sessionId, IReadOnlyCollection<string> names) => 0;
    public int TerminateAllUserProcesses(int sessionId) => 0;
    public bool IsProcessRunningInSession(int sessionId, string processName) => false;
    public IReadOnlyList<(string name, bool isAdmin)> LocalUsers() => new[] { (Environment.UserName, false) };
    public bool IsAdministrator(string userName) => false;
    public IDisposable? ConnectShare(string uncPath, string user, string password) => null;
}
