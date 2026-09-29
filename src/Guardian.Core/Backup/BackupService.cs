using Dapper;
using Guardian.Contracts;
using Guardian.Core.Storage;

namespace Guardian.Core.Backup;

/// <summary>Nightly VACUUM INTO a dated copy under backup\ (7 kept), then a file copy to the UNC path if set.</summary>
public sealed class BackupService
{
    public const int KeepLocal = 7;
    private readonly Db _db;
    private readonly string _backupDir;
    private readonly AlertRepo _alerts;
    private readonly Func<(string? path, string? user, string? password)> _remote;
    private readonly Func<string, string, string, IDisposable?> _connectShare;

    public BackupService(Db db, string backupDir, AlertRepo alerts, Func<(string? path, string? user, string? password)> remote, Func<string, string, string, IDisposable?>? connectShare = null)
    {
        _db = db; _backupDir = backupDir; _alerts = alerts; _remote = remote; _connectShare = connectShare ?? ((_, _, _) => null);
    }

    public string? LastFile { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastError { get; private set; }

    public string Run(DateTimeOffset now)
    {
        Directory.CreateDirectory(_backupDir);
        var file = Path.Combine(_backupDir, $"guardian-{now:yyyy-MM-dd}.db");
        if (File.Exists(file)) File.Delete(file);
        using (var c = _db.Open())
        {
            c.Execute("VACUUM INTO @file", new { file });
        }
        foreach (var old in Directory.GetFiles(_backupDir, "guardian-*.db").OrderByDescending(f => f).Skip(KeepLocal))
        {
            try { File.Delete(old); } catch { }
        }
        LastFile = file; LastRunAt = now; LastError = null;

        var (remotePath, user, password) = _remote();
        if (!string.IsNullOrWhiteSpace(remotePath))
        {
            try
            {
                using var conn = string.IsNullOrEmpty(user) ? null : _connectShare(remotePath, user!, password ?? "");
                Directory.CreateDirectory(remotePath);
                File.Copy(file, Path.Combine(remotePath, Path.GetFileName(file)), overwrite: true);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _alerts.Add(AlertType.BackupFailed, new { path = remotePath, error = ex.Message, at = now }, now);
            }
        }
        return file;
    }
}
