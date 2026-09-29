using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guardian.Contracts;

/// <summary>
/// The parent's policy. One JSON document, versioned on every save.
/// Times are local wall-clock "HH:mm"; minutes are per calendar day.
/// </summary>
public sealed class Policy
{
    public int Version { get; set; }
    public BedtimePolicy? Bedtime { get; set; } = new();
    /// <summary>Keys: "total" plus category names. Minutes per day. School never counts toward total.</summary>
    public Dictionary<string, int> DailyLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Category → list of allowed windows. A category with no entry is allowed at any hour.</summary>
    public Dictionary<string, List<CategoryHoursRule>> CategoryHours { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int GraceMinutes { get; set; } = 5;
    public List<PolicyException> Exceptions { get; set; } = new();

    public static Policy Default() => new()
    {
        Version = 1,
        Bedtime = new BedtimePolicy
        {
            SchoolNights = new TimeWindow { Start = "21:00", End = "06:30" },
            Weekends = new TimeWindow { Start = "22:30", End = "07:30" },
        },
        DailyLimits = new(StringComparer.OrdinalIgnoreCase) { ["total"] = 240, [Categories.Gaming] = 90, [Categories.Other] = 120 },
        CategoryHours = new(StringComparer.OrdinalIgnoreCase),
        GraceMinutes = 5,
    };

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static Policy FromJson(string json) => JsonSerializer.Deserialize<Policy>(json, JsonOptions) ?? Default();

    public Policy Clone() => FromJson(ToJson());
}

public sealed class BedtimePolicy
{
    /// <summary>Sunday–Thursday nights.</summary>
    public TimeWindow? SchoolNights { get; set; }
    /// <summary>Friday and Saturday nights.</summary>
    public TimeWindow? Weekends { get; set; }
}

public sealed class TimeWindow
{
    /// <summary>"HH:mm" local.</summary>
    public string Start { get; set; } = "21:00";
    /// <summary>"HH:mm" local. May be earlier than Start, meaning the window crosses midnight.</summary>
    public string End { get; set; } = "06:30";

    public TimeOnly StartTime => TimeOnly.Parse(Start);
    public TimeOnly EndTime => TimeOnly.Parse(End);
}

public sealed class CategoryHoursRule
{
    /// <summary>Day abbreviations: Mon, Tue, Wed, Thu, Fri, Sat, Sun. Empty = every day.</summary>
    public List<string> Days { get; set; } = new();
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "20:00";

    public bool AppliesTo(DayOfWeek day) => Days.Count == 0 || Days.Any(d => DayNames.Parse(d) == day);
}

/// <summary>
/// A one-off override for a single date, or a live action from the dashboard.
/// Any field left null leaves the base policy untouched.
/// </summary>
public sealed class PolicyException
{
    public long Id { get; set; }
    /// <summary>"yyyy-MM-dd" local date the exception applies to.</summary>
    public string Date { get; set; } = "";
    /// <summary>Set to an explicit null in JSON ("bedtime": null) to lift bedtime for the night that starts on this date.</summary>
    public TimeWindow? Bedtime { get; set; }
    /// <summary>True when the JSON explicitly contained "bedtime": null.</summary>
    public bool NoBedtime { get; set; }
    public Dictionary<string, int>? DailyLimits { get; set; }
    /// <summary>Extra minutes added to the total and every category limit for the day.</summary>
    public int AddMinutes { get; set; }
    /// <summary>All limits and bedtime are suspended until this instant (ISO-8601, local).</summary>
    public DateTimeOffset? PauseUntil { get; set; }
    /// <summary>Lock the session from this instant until LockUntil (a "lock now" action).</summary>
    public DateTimeOffset? LockFrom { get; set; }
    public DateTimeOffset? LockUntil { get; set; }
    /// <summary>Reasons (see EnforcementReason) cancelled for the rest of the day.</summary>
    public List<string> Cancel { get; set; } = new();
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class DayNames
{
    public static DayOfWeek Parse(string s) => s.Trim().ToLowerInvariant() switch
    {
        "mon" or "monday" => DayOfWeek.Monday,
        "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
        "wed" or "wednesday" => DayOfWeek.Wednesday,
        "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
        "fri" or "friday" => DayOfWeek.Friday,
        "sat" or "saturday" => DayOfWeek.Saturday,
        "sun" or "sunday" => DayOfWeek.Sunday,
        _ => throw new FormatException($"Unknown day '{s}'"),
    };

    public static string Short(DayOfWeek d) => d.ToString()[..3];
}
