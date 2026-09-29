using Dapper;
using Guardian.Core.Storage;
using Guardian.Core.Time;

namespace Guardian.Core.Auth;

/// <summary>5 failed attempts from an address, then a 15-minute lockout.</summary>
public sealed class LoginLimiter
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    private readonly Db _db;
    public LoginLimiter(Db db) => _db = db;

    public bool IsLockedOut(string ip, DateTimeOffset now)
    {
        using var c = _db.Open();
        var since = (now - Lockout).ToUnix();
        var fails = c.ExecuteScalar<int>("SELECT COUNT(*) FROM login_attempt WHERE ip=@ip AND ok=0 AND at > @since", new { ip, since });
        return fails >= MaxAttempts;
    }

    public void Record(string ip, bool ok, DateTimeOffset now)
    {
        using var c = _db.Open();
        if (ok) c.Execute("DELETE FROM login_attempt WHERE ip=@ip", new { ip });
        else c.Execute("INSERT INTO login_attempt(at, ip, ok) VALUES(@at, @ip, 0)", new { at = now.ToUnix(), ip });
        c.Execute("DELETE FROM login_attempt WHERE at < @old", new { old = (now - Lockout - Lockout).ToUnix() });
    }
}
