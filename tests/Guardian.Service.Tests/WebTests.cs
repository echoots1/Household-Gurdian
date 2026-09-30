using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Guardian.Contracts;
using Guardian.Core.Storage;
using Guardian.Service.Infrastructure;
using Guardian.Service.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Guardian.Service.Tests;

public class WebTests : IDisposable
{
    private readonly GuardianFactory _f = new();
    public void Dispose() => _f.Dispose();
    private const string Password = "correct horse battery";

    private static async Task<string> Csrf(HttpClient c, string page)
    {
        var html = await c.GetStringAsync(page);
        return Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } v ? v : Regex.Match(html, "name=\"csrf\" content=\"([^\"]+)\"").Groups[1].Value;
    }

    private async Task Setup()
    {
        var local = _f.Local();
        var token = await Csrf(local, "/setup");
        var r = await local.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["userOther"] = "kid", ["password"] = Password, ["password2"] = Password, ["smtpPort"] = "587",
        }));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
    }

    private async Task<HttpClient> ParentLoggedIn()
    {
        var lan = _f.Lan();
        var r = await lan.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = Password }));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.EndsWith("/", r.Headers.Location!.ToString());
        return lan;
    }

    [Fact]
    public async Task Before_setup_lan_gets_503_and_loopback_gets_the_wizard()
    {
        var lan = _f.Lan();
        var r = await lan.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var local = _f.Local();
        r = await local.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/setup", r.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await lan.GetAsync("/setup")).StatusCode);
    }

    [Fact]
    public async Task Setup_then_login_then_pages()
    {
        await Setup();
        var lan = _f.Lan();
        var r = await lan.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/login", r.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("/api/v1/today")).StatusCode);

        var wrong = await lan.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = "nope" }));
        Assert.Contains("Wrong", Uri.UnescapeDataString(wrong.Headers.Location!.ToString()));

        var parent = await ParentLoggedIn();
        foreach (var p in new[] { "/", "/week", "/activity", "/sites", "/alerts", "/policy", "/settings", "/api/v1/today", "/api/v1/policy", "/api/v1/status", "/api/v1/export", "/api/v1/cert", "/api/v1/rules" })
        {
            var pr = await parent.GetAsync(p);
            Assert.True(pr.StatusCode == HttpStatusCode.OK, $"{p} → {pr.StatusCode}");
        }
        // The setup wizard is gone once complete (loopback shows the "done" page, LAN is forbidden).
        Assert.Equal(HttpStatusCode.Forbidden, (await lan.GetAsync("/setup")).StatusCode);
    }

    [Fact]
    public async Task Stale_setup_form_gets_a_readable_error_not_a_400()
    {
        var local = _f.Local();
        await local.GetAsync("/setup"); // sets the antiforgery cookie
        var r = await local.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = "stale-token", ["userOther"] = "kid", ["password"] = Password, ["password2"] = Password,
        }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("expired", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await local.GetAsync("/")).StatusCode); // still not set up
    }

    [Fact]
    public async Task Lockout_after_five_failures()
    {
        await Setup();
        var lan = _f.Lan();
        for (var i = 0; i < 5; i++) await lan.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = "nope" }));
        var r = await lan.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = Password }));
        Assert.Contains("Too many", Uri.UnescapeDataString(r.Headers.Location!.ToString()));
    }

    [Fact]
    public async Task Child_view_is_loopback_only_and_needs_no_login()
    {
        await Setup();
        var local = _f.Local();
        foreach (var p in new[] { "/me", "/me/schedule", "/me/collected", "/me/request", "/me/api/today", "/ext/update.xml", "/healthz" })
            Assert.True((await local.GetAsync(p)).StatusCode == HttpStatusCode.OK, p);
        var lan = _f.Lan();
        foreach (var p in new[] { "/me", "/me/schedule", "/me/collected", "/ext/update.xml" })
            Assert.Equal(HttpStatusCode.Forbidden, (await lan.GetAsync(p)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lan.PostAsJsonAsync("/tab", new TabReport { Domain = "x.example" })).StatusCode);
        var html = await local.GetStringAsync("/me/collected");
        Assert.Contains("kid", html);
        Assert.Contains("never records", html);
    }

    [Fact]
    public async Task Time_request_and_content_flag_land_as_alerts_and_email_digest_formats()
    {
        await Setup();
        var local = _f.Local();
        Assert.Equal(HttpStatusCode.OK, (await local.PostAsJsonAsync("/me/time-request", new { reason = "one more level" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await local.PostAsJsonAsync("/tab", new TabReport { Domain = "pornhub.com", Title = "x", ActiveSeconds = 30, Browser = "chrome" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await local.PostAsJsonAsync("/tab", new TabReport { Domain = "khanacademy.org", Title = "Algebra", ActiveSeconds = 30, Browser = "chrome" })).StatusCode);
        var parent = await ParentLoggedIn();
        var alerts = await parent.GetFromJsonAsync<JsonElement>("/api/v1/alerts?unread=1");
        var types = alerts.EnumerateArray().Select(a => a.GetProperty("type").GetString()).ToList();
        Assert.Contains(AlertType.TimeRequest, types);
        Assert.Contains(AlertType.ContentFlag, types);
        Assert.Equal(2, types.Count);
        var count = await parent.GetFromJsonAsync<JsonElement>("/api/v1/alerts/count");
        Assert.Equal(2, count.GetProperty("unread").GetInt32());
        var page = await parent.GetStringAsync("/alerts");
        Assert.Contains("pornhub.com", page);
        Assert.Contains("one more level", page);
    }

    [Fact]
    public async Task Mutations_need_csrf_and_policy_round_trips()
    {
        await Setup();
        var parent = await ParentLoggedIn();
        var policy = Policy.Default();
        policy.DailyLimits["Gaming"] = 45;
        var noToken = await parent.PutAsJsonAsync("/api/v1/policy", policy);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var token = await Csrf(parent, "/policy");
        parent.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        var ok = await parent.PutAsJsonAsync("/api/v1/policy", policy);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var saved = await parent.GetFromJsonAsync<Policy>("/api/v1/policy", Policy.JsonOptions);
        Assert.Equal(2, saved!.Version);
        Assert.Equal(45, saved.DailyLimits["Gaming"]);

        var bad = await parent.PutAsJsonAsync("/api/v1/policy", new { bedtime = new { schoolNights = new { start = "25:99", end = "06:00" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ex = await parent.PostAsJsonAsync("/api/v1/actions/add-minutes", new { minutes = 30 });
        Assert.Equal(HttpStatusCode.OK, ex.StatusCode);
        var exceptions = await parent.GetFromJsonAsync<JsonElement>("/api/v1/exceptions");
        Assert.Single(exceptions.EnumerateArray());
        var today = await parent.GetFromJsonAsync<JsonElement>("/api/v1/today");
        var total = today.GetProperty("meters").EnumerateArray().First(m => m.GetProperty("key").GetString() == "total");
        Assert.Equal(270, total.GetProperty("limitMinutes").GetInt32());

        var rules = await parent.PostAsJsonAsync("/api/v1/rules/reclassify", new { kind = "app", name = "blender", category = "School" });
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
        var all = await parent.GetFromJsonAsync<JsonElement>("/api/v1/rules");
        Assert.Contains(all.EnumerateArray(), r => r.GetProperty("pattern").GetString() == "blender");

        var pw = await parent.PostAsJsonAsync("/api/v1/settings/password", new { current = "wrong", next = "another long password" });
        Assert.Equal(HttpStatusCode.BadRequest, pw.StatusCode);
        pw = await parent.PostAsJsonAsync("/api/v1/settings/password", new { current = Password, next = "another long password" });
        Assert.Equal(HttpStatusCode.OK, pw.StatusCode);
    }

    [Fact]
    public async Task Pipe_protocol_records_samples_only_after_notice_is_accepted()
    {
        await Setup();
        var pipe = _f.Services.GetServices<IHostedService>().OfType<PipeServerWorker>().Single();
        var settings = _f.Services.GetRequiredService<SettingsRepo>();
        var state = _f.Services.GetRequiredService<GuardianState>();
        var now = DateTimeOffset.Now;

        var resp = pipe.Handle(new PipeRequest { Type = "sample", User = "kid", SessionId = 2, Sample = new Sample { At = now, User = "kid", SessionId = 2, Process = "steam", Title = "Steam", IdleSeconds = 1 } });
        Assert.False(resp.Status!.NoticeAccepted);
        Assert.True(state.SessionActive(now));
        Assert.Null(state.Sampler.LastProcess); // nothing recorded before acceptance

        // Another account's tray is told it is not monitored and nothing is stored.
        var other = pipe.Handle(new PipeRequest { Type = "sample", User = "mom", SessionId = 3, Sample = new Sample { At = now, User = "mom", SessionId = 3, Process = "winword" } });
        Assert.Contains("not monitored", other.Status!.Tooltip);

        pipe.Handle(new PipeRequest { Type = "ack_notice", User = "kid", SessionId = 2 });
        Assert.NotNull(settings.NoticeAcceptedAt);
        resp = pipe.Handle(new PipeRequest { Type = "sample", User = "kid", SessionId = 2, Sample = new Sample { At = now, User = "kid", SessionId = 2, Process = "steam", Title = "Steam", IdleSeconds = 1 } });
        Assert.True(resp.Status!.NoticeAccepted);
        Assert.Equal("steam", state.Sampler.LastProcess);
        Assert.Equal(Categories.Gaming, state.Sampler.LastCategory);

        var sched = _f.Services.GetRequiredService<SchedulerWorker>();
        sched.RunOnce();
        Assert.NotEqual(TrayState.WaitingForNotice, state.Status.State);
        var local = _f.Local();
        var today = await local.GetFromJsonAsync<JsonElement>("/me/api/today");
        Assert.True(today.GetProperty("noticeAccepted").GetBoolean());
        Assert.Equal("steam", today.GetProperty("currentName").GetString());
    }
}
