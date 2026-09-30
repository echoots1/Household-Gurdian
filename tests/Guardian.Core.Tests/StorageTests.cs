using Guardian.Contracts;
using Guardian.Core.Alerts;
using Guardian.Core.Auth;
using Guardian.Core.Backup;
using Guardian.Core.Storage;
using Xunit;

namespace Guardian.Core.Tests;

public class StorageTests
{
    [Fact]
    public void Policy_is_append_only_and_versioned()
    {
        using var db = new TestDb();
        var p = db.Policies.Current();
        Assert.Equal(1, p.Version);
        p.DailyLimits["Gaming"] = 45;
        var saved = db.Policies.Save(p);
        Assert.Equal(2, saved.Version);
        Assert.Equal(45, db.Policies.Current().DailyLimits["Gaming"]);
        Assert.Equal(2, db.Policies.History().Count);
    }

    [Fact]
    public void Exceptions_round_trip_including_explicit_null_bedtime()
    {
        using var db = new TestDb();
        var e = db.Policies.AddException(new PolicyException { Date = "2026-10-31", NoBedtime = true, DailyLimits = new() { ["total"] = 360 }, Note = "Halloween" });
        var back = db.Policies.ExceptionsFor("2026-10-31").Single();
        Assert.Equal(e.Id, back.Id); Assert.True(back.NoBedtime); Assert.Equal(360, back.DailyLimits!["total"]);
    }

    [Fact]
    public void Content_flag_fires_once_per_domain_and_respects_overrides()
    {
        using var db = new TestDb();
        db.Lists.ReplaceList("custom", new[] { "bad.example" }, DateTimeOffset.Now);
        var f = new ContentFlagger(db.Lists, db.Alerts);
        Assert.Equal(new[] { "custom" }, f.OnDomainSeen("bad.example", "x", 10, DateTimeOffset.Now));
        Assert.Empty(f.OnDomainSeen("bad.example", "x", 10, DateTimeOffset.Now)); // already seen
        Assert.Equal(new[] { "custom" }, f.OnDomainSeen("www.bad.example", null, 1, DateTimeOffset.Now)); // subdomain matches too
        Assert.Empty(f.OnDomainSeen("fine.example", null, 1, DateTimeOffset.Now));
        db.Lists.SetOverride("bad.example", "allow");
        Assert.Empty(f.Check("bad.example", null, 1, DateTimeOffset.Now));
        db.Lists.SetOverride("meh.example", "flag");
        Assert.Equal(new[] { "always-flag" }, f.Check("meh.example", null, 1, DateTimeOffset.Now));
        Assert.Equal(3, db.Alerts.UnreadCount());
        var a = db.Alerts.List(unreadOnly: true).First();
        Assert.Equal(AlertType.ContentFlag, a.Type);
        db.Alerts.Acknowledge(a.Id);
        Assert.Equal(2, db.Alerts.UnreadCount());
    }

    [Fact]
    public async Task Email_digest_batches_and_marks_sent()
    {
        using var db = new TestDb();
        db.Alerts.Add(AlertType.TimeRequest, new { reason = "please" });
        db.Alerts.Add(AlertType.ContentFlag, new { domain = "bad.example", category = "custom" });
        var sender = new FakeSender();
        var d = new EmailDigest(db.Alerts, sender, () => new SmtpSettings("smtp.example", 587, null, null, "g@example", "p@example", true));
        Assert.Equal(2, await d.FlushAsync(default));
        Assert.Single(sender.Sent);
        Assert.Contains("bad.example", sender.Sent[0].body);
        Assert.Equal(0, await d.FlushAsync(default));
    }

    private sealed class FakeSender : IEmailSender
    {
        public List<(string subject, string body)> Sent { get; } = new();
        public Task SendAsync(SmtpSettings s, string subject, string body, CancellationToken ct) { Sent.Add((subject, body)); return Task.CompletedTask; }
    }

    [Fact]
    public void Password_hash_verifies_and_rejects()
    {
        var h = PasswordHasher.Hash("correct horse battery", iterations: 1, memoryKb: 8192, parallelism: 1);
        Assert.StartsWith("argon2id$", h);
        Assert.True(PasswordHasher.Verify("correct horse battery", h));
        Assert.False(PasswordHasher.Verify("wrong", h));
        Assert.NotNull(PasswordHasher.Validate("short"));
        Assert.Null(PasswordHasher.Validate("twelve chars!"));
    }

    [Fact]
    public void Login_limiter_locks_after_five_failures()
    {
        using var db = new TestDb();
        var l = new LoginLimiter(db.Db);
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 5; i++) { Assert.False(l.IsLockedOut("1.2.3.4", now)); l.Record("1.2.3.4", false, now); }
        Assert.True(l.IsLockedOut("1.2.3.4", now));
        Assert.False(l.IsLockedOut("1.2.3.4", now.AddMinutes(16)));
        Assert.False(l.IsLockedOut("5.6.7.8", now));
    }

    [Fact]
    public void Backup_writes_dated_copy_and_flags_failed_remote()
    {
        using var db = new TestDb();
        db.Settings.Set("x", "y");
        var dir = Path.Combine(Path.GetTempPath(), "guardian-tests", Guid.NewGuid().ToString("N"));
        var blocker = Path.Combine(Path.GetTempPath(), "guardian-tests", Guid.NewGuid().ToString("N") + ".file");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!); File.WriteAllText(blocker, "not a directory");
        var b = new BackupService(db.Db, dir, db.Alerts, () => (Path.Combine(blocker, "share"), null, null));
        var file = b.Run(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero));
        Assert.True(File.Exists(file));
        Assert.EndsWith("guardian-2026-10-05.db", file);
        Assert.Equal("y", new SettingsRepo(new Db(file)).Get("x"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Contains(db.Alerts.List(), a => a.Type == AlertType.BackupFailed);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Export_zip_contains_csvs()
    {
        using var db = new TestDb();
        db.Activity.Insert(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now, "app", "steam", "Steam", null, null, false, null, "Gaming");
        var zip = Export.ToZip(db.Db);
        using var z = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(z.Entries, e => e.Name == "activity.csv");
        using var r = new StreamReader(z.GetEntry("activity.csv")!.Open());
        var text = r.ReadToEnd();
        Assert.Contains("steam", text);
    }

    [Fact]
    public void Secrets_round_trip()
    {
        var p = Secrets.Protect("hunter2hunter2");
        Assert.NotNull(p);
        Assert.NotEqual("hunter2hunter2", p);
        Assert.Equal("hunter2hunter2", Secrets.Unprotect(p));
        Assert.Null(Secrets.Unprotect("garbage"));
    }

    [Fact]
    public void List_loader_parses_hosts_style()
    {
        var d = ListLoader.ParseDomains("# c\n0.0.0.0 bad.example\nother.example\n\n127.0.0.1 localhost\n").ToList();
        Assert.Equal(new[] { "bad.example", "other.example" }, d);
    }
}
