using System.Text.Json;
using Guardian.Contracts;
using Guardian.Core.Alerts;
using Guardian.Core.Auth;
using Guardian.Core.Rollups;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Guardian.Service.Infrastructure;
using Guardian.Service.Win32;
using Guardian.Service.Workers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Guardian.Service.Web;

public sealed record LoginRequest(string Password, string? ReturnUrl);
public sealed record ActionRequest(int? Minutes, int? Hours, string? Reason, string? Note);
public sealed record TimeRequest(string? Reason);
public sealed record RuleDto(string MatchType, string Pattern, string Category, int Priority);
public sealed record ReclassifyRequest(string Kind, string Name, string Category);
public sealed record DomainOverrideRequest(string Domain, string? Mode);
public sealed record SettingsUpdate(Dictionary<string, string?> Values);
public sealed record PasswordChange(string Current, string Next);

/// <summary>The JSON API the web UI uses. Parent routes need the session cookie and a CSRF token on mutations; local routes are loopback-only.</summary>
public static class Api
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static void MapLocal(WebApplication app)
    {
        var local = app.MapGroup("").AddEndpointFilter<LoopbackOnlyFilter>();

        // Extension → service
        local.MapPost("/tab", (TabReport r, GuardianState state, ContentFlagger flagger, IClock clock) =>
        {
            var now = clock.Now;
            state.Sampler.OnTabReport(r, now);
            state.ExtensionEverReported = true;
            if (!string.IsNullOrWhiteSpace(r.Domain)) flagger.OnDomainSeen(r.Domain, r.Title, r.ActiveSeconds, now);
            return Results.Ok(new { ok = true });
        });

        local.MapGet("/ext/update.xml", (ExtensionHost ext) => Results.Content(ext.UpdateXml(), "application/xml"));
        local.MapGet("/ext/guardian.crx", (ExtensionHost ext) => ext.Available ? Results.File(ext.CrxPath, "application/x-chrome-extension", "guardian.crx") : Results.NotFound());

        // Child → parent: the one thing the child can send.
        local.MapPost("/me/time-request", (TimeRequest r, AlertRepo alerts, SettingsRepo settings, IClock clock) =>
        {
            var reason = (r.Reason ?? "").Trim();
            if (reason.Length > 200) reason = reason[..200];
            alerts.Add(AlertType.TimeRequest, new { reason, user = settings.MonitoredUser, at = clock.Now }, clock.Now);
            return Results.Ok(new { ok = true, message = "Sent. Your parent will see it on the dashboard." });
        });

        local.MapGet("/me/api/today", (Queries q) => Results.Json(q.Today(), Json));
        local.MapGet("/healthz", (GuardianState s, IClock c) => Results.Json(new { ok = true, version = s.InstalledVersion, uptime = (c.Now - s.StartedAt).ToString(@"d\.hh\:mm\:ss") }));
    }

    public static void MapParent(WebApplication app)
    {
        app.MapPost("/auth/login", async (HttpContext ctx, SettingsRepo settings, LoginLimiter limiter, IClock clock) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var err = await Auth.TryLoginAsync(ctx, form["password"].ToString(), settings, limiter, clock);
            if (err is not null) return Results.Redirect("/login?error=" + Uri.EscapeDataString(err));
            var back = form["returnUrl"].ToString();
            return Results.Redirect(string.IsNullOrEmpty(back) || !back.StartsWith('/') || back.StartsWith("//") ? "/" : back);
        }).DisableAntiforgery();
        app.MapPost("/auth/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(Auth.Scheme); return Results.Redirect("/login"); }).RequireAuthorization();

        var api = app.MapGroup("/api/v1").RequireAuthorization();
        var mut = app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter(async (ctx, next) =>
        {
            var af = ctx.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            try { await af.ValidateRequestAsync(ctx.HttpContext); } catch (AntiforgeryValidationException) { return Results.StatusCode(400); }
            return await next(ctx);
        });

        api.MapGet("/today", (Queries q, string? date) => Results.Json(q.Today(date is null ? null : DateOnly.Parse(date)), Json));
        api.MapGet("/week", (Queries q, string? start) => Results.Json(q.Week(start is null ? null : DateOnly.Parse(start)), Json));
        api.MapGet("/activity", (ActivityRepo a, EnforcementRepo ev, IClock clock, string? from, string? to, string? category, string? q, int? limit) =>
        {
            var (f, t) = Range(clock, from, to);
            return Results.Json(new { events = a.Query(new ActivityQuery(f, t, category, q, false, limit ?? 1000)), enforcement = ev.Range(f, t), sessions = ev.SessionsRange(f, t) }, Json);
        });
        api.MapGet("/sites", (RollupRepo r, ListRepo lists, IClock clock, string? from, string? to) =>
        {
            var (f, t) = Range(clock, from, to);
            var seen = lists.AllFirstSeen();
            var ov = lists.Overrides().ToDictionary(o => o.domain, o => o.mode);
            var items = r.Items(f.LocalDate(), t.LocalDate(), "web", 200).Select(i => new { i.Name, i.Category, i.Seconds, FirstSeen = seen.GetValueOrDefault(i.Name), Override = ov.GetValueOrDefault(i.Name), Lists = lists.Match(i.Name) });
            return Results.Json(items, Json);
        });
        api.MapGet("/policy", (PolicyRepo p) => Results.Json(p.Current(), Policy.JsonOptions));
        mut.MapPut("/policy", (Policy incoming, PolicyRepo p, SchedulerWorker sched) =>
        {
            var err = ValidatePolicy(incoming);
            if (err is not null) return Results.BadRequest(new { error = err });
            var saved = p.Save(incoming);
            sched.RunOnce();
            return Results.Json(saved, Policy.JsonOptions);
        });
        api.MapGet("/exceptions", (PolicyRepo p, IClock c) => Results.Json(p.UpcomingExceptions(c.Now.LocalDate().AddDays(-1)), Policy.JsonOptions));
        mut.MapPost("/exceptions", (PolicyException e, PolicyRepo p, SchedulerWorker sched) =>
        {
            if (!DateOnly.TryParse(e.Date, out _)) return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
            var saved = p.AddException(e); sched.RunOnce();
            return Results.Json(saved, Policy.JsonOptions);
        });
        mut.MapDelete("/exceptions/{id:long}", (long id, PolicyRepo p, SchedulerWorker sched) => { p.DeleteException(id); sched.RunOnce(); return Results.Ok(); });

        mut.MapPost("/actions/{action}", (string action, ActionRequest r, PolicyRepo p, EnforcementRepo ev, SchedulerWorker sched, IClock clock) =>
        {
            var now = clock.Now; var date = now.DateKey();
            PolicyException e = action switch
            {
                "add-minutes" => new PolicyException { Date = date, AddMinutes = Math.Clamp(r.Minutes ?? 30, 1, 720), Note = r.Note ?? $"+{r.Minutes ?? 30} min added by parent" },
                "pause" => new PolicyException { Date = date, PauseUntil = now.AddHours(Math.Clamp(r.Hours ?? 1, 1, 24)), Note = r.Note ?? $"Limits paused for {r.Hours ?? 1} h" },
                "lock-now" => new PolicyException { Date = date, LockFrom = now.AddMinutes(1), LockUntil = now.AddHours(Math.Clamp(r.Hours ?? 1, 1, 24)), Note = r.Note ?? "Locked by parent" },
                "cancel" => new PolicyException { Date = date, Cancel = new() { string.IsNullOrEmpty(r.Reason) ? EnforcementReason.Bedtime : r.Reason }, Note = r.Note ?? $"Cancelled {EnforcementReason.Describe(r.Reason ?? EnforcementReason.Bedtime)}" },
                _ => null!,
            };
            if (e is null) return Results.NotFound();
            e = p.AddException(e);
            ev.Add(now, "parent:" + action, "action", p.Current().Version, e.Note);
            sched.RunOnce();
            return Results.Json(e, Policy.JsonOptions);
        });

        api.MapGet("/rules", (RuleRepo r) => Results.Json(r.All(), Json));
        mut.MapPut("/rules", (List<RuleDto> rules, RuleRepo repo) =>
        {
            var parsed = new List<Rule>();
            foreach (var d in rules)
            {
                if (!Enum.TryParse<RuleMatch>(d.MatchType, true, out var mt)) return Results.BadRequest(new { error = $"bad matchType {d.MatchType}" });
                if (!Categories.IsValid(d.Category)) return Results.BadRequest(new { error = $"bad category {d.Category}" });
                parsed.Add(new Rule { MatchType = mt, Pattern = d.Pattern, Category = d.Category, Priority = d.Priority });
            }
            return Results.Json(new { version = repo.Replace(parsed) });
        });
        mut.MapPost("/rules/reclassify", (ReclassifyRequest r, RuleRepo repo) =>
        {
            if (!Categories.IsValid(r.Category)) return Results.BadRequest();
            var mt = r.Kind == "web" ? RuleMatch.Domain : RuleMatch.Process;
            return Results.Json(new { version = repo.Add(new Rule { MatchType = mt, Pattern = r.Name, Category = r.Category, Priority = 1 }) });
        });
        mut.MapPost("/domains/override", (DomainOverrideRequest r, ListRepo lists, ContentFlagger flagger, IClock clock) =>
        {
            var mode = r.Mode is "allow" or "flag" ? r.Mode : null;
            lists.SetOverride(r.Domain, mode);
            if (mode == "flag") flagger.Check(r.Domain, null, 0, clock.Now);
            return Results.Ok(new { domain = r.Domain, mode });
        });

        api.MapGet("/alerts", (AlertRepo a, int? unread) => Results.Json(a.List(unread == 1), Json));
        api.MapGet("/alerts/count", (AlertRepo a) => Results.Json(new { unread = a.UnreadCount() }));
        mut.MapPost("/alerts/{id:long}/ack", (long id, AlertRepo a) => { a.Acknowledge(id); return Results.Ok(); });
        mut.MapPost("/alerts/ack-all", (AlertRepo a) => { foreach (var al in a.List(true, 1000)) a.Acknowledge(al.Id); return Results.Ok(); });

        api.MapGet("/settings", (SettingsRepo s) => Results.Json(PublicSettings(s)));
        mut.MapPut("/settings", (SettingsUpdate u, SettingsRepo s) =>
        {
            foreach (var (k, v) in u.Values)
            {
                if (!Editable.Contains(k)) continue;
                if (k is SettingKeys.SmtpPassword or SettingKeys.BackupPassword) { if (!string.IsNullOrEmpty(v)) s.Set(k, Secrets.Protect(v)); continue; }
                if (k.StartsWith("retention_") && (!int.TryParse(v, out var n) || n < 1)) continue;
                s.Set(k, string.IsNullOrWhiteSpace(v) ? null : v.Trim());
            }
            return Results.Json(PublicSettings(s));
        });
        mut.MapPost("/settings/password", (PasswordChange c, SettingsRepo s) =>
        {
            if (!PasswordHasher.Verify(c.Current, s.Get(SettingKeys.ParentPasswordHash))) return Results.BadRequest(new { error = "Current password is wrong." });
            if (PasswordHasher.Validate(c.Next) is { } err) return Results.BadRequest(new { error = err });
            s.Set(SettingKeys.ParentPasswordHash, PasswordHasher.Hash(c.Next));
            return Results.Ok();
        });
        mut.MapPost("/settings/monitored-user", (DomainOverrideRequest r, SettingsRepo s, ISessionControl sc, EnforcementRepo ev, SchedulerWorker sched, IClock c) =>
        {
            // Changing the account restarts the trust step: the new account must accept the notice before anything is recorded.
            var name = r.Domain.Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "Account name is required." });
            s.Set(SettingKeys.MonitoredUser, name);
            s.Set(SettingKeys.MonitoredUserIsAdmin, sc.IsAdministrator(name) ? "1" : "0");
            s.Set(SettingKeys.NoticeAcceptedAt, null);
            ev.AddSession(c.Now, "monitored_user_changed", name);
            sched.RunOnce();
            return Results.Ok(new { monitoredUser = name });
        });
        mut.MapPost("/lists/refresh", async (ListRefresh refresh, CancellationToken ct) => Results.Json(await refresh.RefreshAsync(ct)));
        api.MapGet("/lists", (ListRepo l) => Results.Json(new { counts = l.Counts(), custom = l.Domains("custom"), overrides = l.Overrides() }));
        mut.MapPost("/lists/custom", (DomainOverrideRequest r, ListRepo l) => { if (r.Mode == "remove") l.RemoveFromList("custom", r.Domain); else l.AddToList("custom", r.Domain); return Results.Ok(); });
        mut.MapPost("/backup/now", (Core.Backup.BackupService b, IClock c) => { try { return Results.Json(new { file = b.Run(c.Now), error = b.LastError }); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
        mut.MapPost("/email/test", async (EmailDigest d, IEmailSender sender, SettingsRepo s, CancellationToken ct) =>
        {
            var smtp = Smtp(s);
            if (smtp is null) return Results.BadRequest(new { error = "SMTP is not configured." });
            try { await sender.SendAsync(smtp, "[Guardian] Test email", "Guardian can send email from this PC.", ct); return Results.Ok(); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        api.MapGet("/export", (Db db) => Results.File(Export.ToZip(db), "application/zip", $"guardian-export-{DateTime.Now:yyyyMMdd}.zip"));
        api.MapGet("/cert", (System.Security.Cryptography.X509Certificates.X509Certificate2 cert) => Results.File(Certificates.PublicPem(cert), "application/x-pem-file", "guardian.crt"));
        api.MapGet("/status", (GuardianState st, SettingsRepo s, IClock c, ExtensionHost ext) => Results.Json(new
        {
            version = st.InstalledVersion, startedAt = st.StartedAt, sessionActive = st.SessionActive(c.Now), sessionUser = st.SessionUser, tray = st.Status,
            noticeAcceptedAt = s.NoticeAcceptedAt, monitoredUser = s.MonitoredUser, extensionActive = st.Sampler.ExtensionActive(c.Now), extensionPacked = ext.Available, extensionId = ext.Id,
            cert = st.CertFingerprint, lanAddresses = Certificates.LanAddresses().Select(a => a.ToString()), host = System.Net.Dns.GetHostName(),
        }, Json));
    }

    public static readonly HashSet<string> Editable = new()
    {
        SettingKeys.SmtpHost, SettingKeys.SmtpPort, SettingKeys.SmtpUser, SettingKeys.SmtpPassword, SettingKeys.SmtpFrom, SettingKeys.SmtpTo, SettingKeys.SmtpUseTls,
        SettingKeys.BackupPath, SettingKeys.BackupUser, SettingKeys.BackupPassword, SettingKeys.RetentionRawDays, SettingKeys.RetentionItemDays, SettingKeys.RetentionAlertDays,
        SettingKeys.TitleCategories, SettingKeys.ListSources, SettingKeys.FullPathCategories,
    };

    public static Dictionary<string, string?> PublicSettings(SettingsRepo s)
    {
        var all = s.All();
        var d = new Dictionary<string, string?>();
        foreach (var k in Editable) d[k] = k is SettingKeys.SmtpPassword or SettingKeys.BackupPassword ? (all.ContainsKey(k) ? "••••••••" : "") : all.GetValueOrDefault(k);
        d[SettingKeys.MonitoredUser] = all.GetValueOrDefault(SettingKeys.MonitoredUser);
        d[SettingKeys.MonitoredUserIsAdmin] = all.GetValueOrDefault(SettingKeys.MonitoredUserIsAdmin);
        d[SettingKeys.NoticeAcceptedAt] = all.GetValueOrDefault(SettingKeys.NoticeAcceptedAt);
        d[SettingKeys.ListsRefreshedAt] = all.GetValueOrDefault(SettingKeys.ListsRefreshedAt);
        d[SettingKeys.CertThumbprint] = all.GetValueOrDefault(SettingKeys.CertThumbprint);
        return d;
    }

    public static SmtpSettings? Smtp(SettingsRepo s)
    {
        var host = s.Get(SettingKeys.SmtpHost);
        if (string.IsNullOrWhiteSpace(host)) return null;
        return new SmtpSettings(host, s.GetInt(SettingKeys.SmtpPort, 587), s.Get(SettingKeys.SmtpUser), Secrets.Unprotect(s.Get(SettingKeys.SmtpPassword)),
            s.Get(SettingKeys.SmtpFrom) ?? "", s.Get(SettingKeys.SmtpTo) ?? "", s.GetBool(SettingKeys.SmtpUseTls, true));
    }

    private static (DateTimeOffset from, DateTimeOffset to) Range(IClock clock, string? from, string? to)
    {
        var today = clock.Now.LocalDate();
        var f = from is null ? today : DateOnly.Parse(from);
        var t = to is null ? f : DateOnly.Parse(to);
        return (f.AtLocal(TimeOnly.MinValue), t.AddDays(1).AtLocal(TimeOnly.MinValue));
    }

    public static string? ValidatePolicy(Policy p)
    {
        try
        {
            if (p.Bedtime?.SchoolNights is { } s) { _ = s.StartTime; _ = s.EndTime; }
            if (p.Bedtime?.Weekends is { } w) { _ = w.StartTime; _ = w.EndTime; }
            foreach (var (k, v) in p.DailyLimits) if (k != "total" && !Categories.IsValid(k)) return $"Unknown limit key {k}"; else if (v < 0) return "Limits cannot be negative";
            foreach (var (k, rules) in p.CategoryHours)
            {
                if (!Categories.IsValid(k)) return $"Unknown category {k}";
                foreach (var r in rules) { _ = TimeOnly.Parse(r.Start); _ = TimeOnly.Parse(r.End); foreach (var d in r.Days) DayNames.Parse(d); }
            }
            p.DailyLimits = new(p.DailyLimits.Where(kv => kv.Value > 0), StringComparer.OrdinalIgnoreCase);
            p.CategoryHours = new(p.CategoryHours.Where(kv => kv.Value.Count > 0), StringComparer.OrdinalIgnoreCase);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
