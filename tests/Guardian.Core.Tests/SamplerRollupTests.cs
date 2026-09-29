using Guardian.Contracts;
using Guardian.Core.Categorization;
using Guardian.Core.Rollups;
using Guardian.Core.Sampling;
using Guardian.Core.Storage;
using Xunit;

namespace Guardian.Core.Tests;

public class SamplerRollupTests
{
    private static (TestDb db, SamplerEngine engine, RollupService rollups) Setup()
    {
        var db = new TestDb();
        db.Rules.SeedIfEmpty(DefaultRules.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lists", "defaults.yaml"))));
        var rollups = new RollupService(db.Activity, db.Rollups, db.Rules);
        var engine = new SamplerEngine(db.Activity, rollups.CurrentCategorizer, () => new[] { Categories.Gaming, Categories.Other });
        return (db, engine, rollups);
    }

    private static Sample S(DateTimeOffset at, string? process, string? title = null, int idle = 0, bool locked = false, string? bd = null) =>
        new() { At = at, User = "kid", SessionId = 1, Process = process, Title = title, IdleSeconds = idle, Locked = locked, BrowserDomain = bd };

    [Fact]
    public void Samples_land_as_intervals_and_idle_is_excluded()
    {
        var (db, engine, rollups) = Setup();
        using (db)
        {
            var t = new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero).ToLocalTime();
            for (var i = 0; i < 12; i++) engine.OnSample(S(t.AddSeconds(i * 5), "steam", "Steam"));      // 60 s gaming
            for (var i = 12; i < 36; i++) engine.OnSample(S(t.AddSeconds(i * 5), "steam", "Steam", idle: 200)); // 120 s idle
            for (var i = 36; i < 48; i++) engine.OnSample(S(t.AddSeconds(i * 5), "winword", "Essay.docx"));    // 60 s school
            for (var i = 48; i < 54; i++) engine.OnSample(S(t.AddSeconds(i * 5), "winword", "Essay.docx", locked: true)); // locked

            rollups.RollDay(t.ToString("yyyy-MM-dd"));
            var day = db.Rollups.Day(t.ToString("yyyy-MM-dd"));
            Assert.Equal(60, day.Seconds(Categories.Gaming));
            Assert.Equal(60, day.Seconds(Categories.School));
            Assert.Equal(0, day.Seconds(Categories.Other));
            Assert.Equal(60, day.CountedSeconds);

            var events = db.Activity.ForDate(t.ToString("yyyy-MM-dd"), includeIdle: true);
            Assert.Contains(events, e => e.Idle);
            // School titles are off by default: the Word title must not be stored.
            Assert.All(events.Where(e => e.Process == "winword"), e => Assert.Null(e.Title));
            Assert.Contains(events, e => e.Process == "steam" && e.Title == "Steam");
        }
    }

    [Fact]
    public void Browser_time_is_charged_to_the_domain_not_the_process()
    {
        var (db, engine, rollups) = Setup();
        using (db)
        {
            var t = new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero).ToLocalTime();
            engine.OnTabReport(new TabReport { Domain = "docs.google.com", Subdomain = "docs.google.com", Title = "Essay", ActiveSeconds = 5, Browser = "chrome" }, t);
            for (var i = 0; i < 12; i++) engine.OnSample(S(t.AddSeconds(i * 5), "chrome", "Essay - Google Docs"));
            // Extension goes quiet; tray fallback reports youtube.
            var t2 = t.AddMinutes(10);
            for (var i = 0; i < 12; i++) engine.OnSample(S(t2.AddSeconds(i * 5), "chrome", "Cats - YouTube", bd: "youtube.com"));
            rollups.RollDay(t.ToString("yyyy-MM-dd"));
            var day = db.Rollups.Day(t.ToString("yyyy-MM-dd"));
            Assert.Equal(60, day.Seconds(Categories.School));
            Assert.Equal(60, day.Seconds(Categories.Other));
            var items = db.Rollups.Items(DateOnly.FromDateTime(t.DateTime), DateOnly.FromDateTime(t.DateTime));
            Assert.DoesNotContain(items, i => i.Name == "chrome");
            Assert.Contains(items, i => i.Kind == "web" && i.Name == "docs.google.com" && i.Seconds == 60);
            Assert.Contains(items, i => i.Kind == "web" && i.Name == "youtube.com" && i.Seconds == 60);
        }
    }

    [Fact]
    public void Rule_change_rebuilds_history()
    {
        var (db, engine, rollups) = Setup();
        using (db)
        {
            var t = new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero).ToLocalTime();
            for (var i = 0; i < 12; i++) engine.OnSample(S(t.AddSeconds(i * 5), "blender", "Blender"));
            rollups.RollDay(t.ToString("yyyy-MM-dd"));
            Assert.Equal(60, db.Rollups.Day(t.ToString("yyyy-MM-dd")).Seconds(Categories.Other));

            db.Rules.Add(new Rule { MatchType = RuleMatch.Process, Pattern = "blender", Category = Categories.School });
            Assert.True(db.Rules.RebuildPending());
            rollups.RebuildIfPending();
            Assert.False(db.Rules.RebuildPending());
            Assert.Equal(60, db.Rollups.Day(t.ToString("yyyy-MM-dd")).Seconds(Categories.School));
            Assert.Equal(0, db.Rollups.Day(t.ToString("yyyy-MM-dd")).Seconds(Categories.Other));
        }
    }
}
