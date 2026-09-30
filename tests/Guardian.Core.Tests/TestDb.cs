using Guardian.Core.Storage;

namespace Guardian.Core.Tests;

public sealed class TestDb : IDisposable
{
    public Db Db { get; }
    public string Path { get; }
    public TestDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guardian-tests", Guid.NewGuid().ToString("N") + ".db");
        Db = new Db(Path);
    }
    public SettingsRepo Settings => new(Db);
    public PolicyRepo Policies => new(Db);
    public RuleRepo Rules => new(Db);
    public ActivityRepo Activity => new(Db);
    public RollupRepo Rollups => new(Db);
    public AlertRepo Alerts => new(Db);
    public ListRepo Lists => new(Db);
    public EnforcementRepo Enforcement => new(Db);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(Path); File.Delete(Path + "-wal"); File.Delete(Path + "-shm"); } catch { }
    }
}

public sealed class FakeClock : Guardian.Core.Time.IClock
{
    public DateTimeOffset Now { get; set; }
    public TimeZoneInfo Zone => TimeZoneInfo.Local;
    public FakeClock(DateTimeOffset start) => Now = start;
    public void Advance(TimeSpan by) => Now += by;
}
