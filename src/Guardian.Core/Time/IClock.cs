namespace Guardian.Core.Time;

public interface IClock
{
    DateTimeOffset Now { get; }
    TimeZoneInfo Zone { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public TimeZoneInfo Zone => TimeZoneInfo.Local;
}

public static class TimeExt
{
    public static long ToUnix(this DateTimeOffset t) => t.ToUnixTimeSeconds();
    public static DateTimeOffset FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime();
    public static string DateKey(this DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd");
    public static string DateKey(this DateOnly d) => d.ToString("yyyy-MM-dd");
    public static DateOnly LocalDate(this DateTimeOffset t) => DateOnly.FromDateTime(t.ToLocalTime().DateTime);
    public static DateTimeOffset AtLocal(this DateOnly d, TimeOnly t) => new(d.ToDateTime(t), TimeZoneInfo.Local.GetUtcOffset(d.ToDateTime(t)));
}
