using Dapper;
using Microsoft.Data.Sqlite;

namespace Guardian.Core.Storage;

/// <summary>Owns the SQLite file: connection strings, WAL mode, and schema migrations.</summary>
public sealed class Db
{
    public string Path { get; }
    private readonly string _cs;

    public Db(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate(c);
    }

    public static Db InMemory()
    {
        // A unique shared-cache in-memory db so tests get isolation while every connection sees the same data.
        var name = "guardian-" + Guid.NewGuid().ToString("N");
        return new Db($"file:{name}?mode=memory&cache=shared", memory: true);
    }

    private readonly SqliteConnection? _keepAlive;
    private Db(string uri, bool memory)
    {
        Path = uri;
        _cs = new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared }.ToString();
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open();
        Migrate(_keepAlive);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        c.Execute("PRAGMA busy_timeout=5000;");
        return c;
    }

    public const int CurrentSchema = 1;

    private static void Migrate(SqliteConnection c)
    {
        c.Execute("""
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);
            """);
        var v = c.ExecuteScalar<int?>("SELECT CAST(value AS INTEGER) FROM settings WHERE key='schema_version'") ?? 0;
        if (v < 1)
        {
            c.Execute("""
                CREATE TABLE IF NOT EXISTS policy (id INTEGER PRIMARY KEY AUTOINCREMENT, version INTEGER NOT NULL, json TEXT NOT NULL, saved_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS exception (id INTEGER PRIMARY KEY AUTOINCREMENT, date TEXT NOT NULL, json TEXT NOT NULL, created_at INTEGER NOT NULL, note TEXT);
                CREATE INDEX IF NOT EXISTS ix_exception_date ON exception(date);
                CREATE TABLE IF NOT EXISTS activity_event (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    started_at INTEGER NOT NULL, ended_at INTEGER NOT NULL,
                    kind TEXT NOT NULL, process TEXT, title TEXT, domain TEXT, subdomain TEXT,
                    idle INTEGER NOT NULL DEFAULT 0, browser TEXT, category TEXT NOT NULL DEFAULT 'Other',
                    date TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_activity_date ON activity_event(date, started_at);
                CREATE INDEX IF NOT EXISTS ix_activity_domain ON activity_event(domain);
                CREATE TABLE IF NOT EXISTS rollup_daily (date TEXT NOT NULL, category TEXT NOT NULL, seconds INTEGER NOT NULL, rules_version INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(date, category));
                CREATE TABLE IF NOT EXISTS rollup_item (date TEXT NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL, category TEXT NOT NULL, seconds INTEGER NOT NULL, PRIMARY KEY(date, kind, name));
                CREATE TABLE IF NOT EXISTS rule (id INTEGER PRIMARY KEY AUTOINCREMENT, match_type TEXT NOT NULL, pattern TEXT NOT NULL, category TEXT NOT NULL, priority INTEGER NOT NULL DEFAULT 100, created_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS rules_meta (id INTEGER PRIMARY KEY CHECK (id=1), version INTEGER NOT NULL, rebuild_pending INTEGER NOT NULL DEFAULT 0);
                INSERT OR IGNORE INTO rules_meta(id, version, rebuild_pending) VALUES (1, 1, 0);
                CREATE TABLE IF NOT EXISTS category_list (domain TEXT NOT NULL, list_name TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(domain, list_name));
                CREATE TABLE IF NOT EXISTS domain_override (domain TEXT PRIMARY KEY, mode TEXT NOT NULL, created_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS domain_seen (domain TEXT PRIMARY KEY, first_seen INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS alert (id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL, payload TEXT NOT NULL, created_at INTEGER NOT NULL, acknowledged_at INTEGER, emailed INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS ix_alert_unread ON alert(acknowledged_at, created_at);
                CREATE TABLE IF NOT EXISTS enforcement_event (id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, reason TEXT NOT NULL, step TEXT NOT NULL, policy_version INTEGER NOT NULL, detail TEXT, date TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_enforcement_date ON enforcement_event(date, at);
                CREATE TABLE IF NOT EXISTS login_attempt (at INTEGER NOT NULL, ip TEXT NOT NULL, ok INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS session_event (id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT, date TEXT NOT NULL);
                INSERT OR REPLACE INTO settings(key, value) VALUES ('schema_version', '1');
                """);
        }
    }
}
