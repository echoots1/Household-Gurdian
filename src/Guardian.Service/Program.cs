using System.Security.Cryptography.X509Certificates;
using Guardian.Contracts;
using Guardian.Core.Alerts;
using Guardian.Core.Auth;
using Guardian.Core.Backup;
using Guardian.Core.Categorization;
using Guardian.Core.Rollups;
using Guardian.Core.Sampling;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Guardian.Service.Infrastructure;
using Guardian.Service.Web;
using Guardian.Service.Win32;
using Guardian.Service.Workers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(o => o.ServiceName = Names.ServiceName);
if (OperatingSystem.IsWindows()) builder.Logging.AddEventLog(o => o.SourceName = Names.Product);

var paths = new Paths(builder.Configuration["GUARDIAN_DATA"]);
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(paths.LogDir, "service.log")));

// ---- storage & core ----
var db = new Db(paths.DbPath);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<SettingsRepo>();
builder.Services.AddSingleton<PolicyRepo>();
builder.Services.AddSingleton<RuleRepo>();
builder.Services.AddSingleton<ActivityRepo>();
builder.Services.AddSingleton<RollupRepo>();
builder.Services.AddSingleton<AlertRepo>();
builder.Services.AddSingleton<ListRepo>();
builder.Services.AddSingleton<EnforcementRepo>();
builder.Services.AddSingleton<RollupService>();
builder.Services.AddSingleton<ContentFlagger>();
builder.Services.AddSingleton<LoginLimiter>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddSingleton<IEmailSender, MailKitSender>();
builder.Services.AddSingleton(sp => new EmailDigest(sp.GetRequiredService<AlertRepo>(), sp.GetRequiredService<IEmailSender>(), () => Api.Smtp(sp.GetRequiredService<SettingsRepo>())));
builder.Services.AddSingleton(sp =>
{
    var s = sp.GetRequiredService<SettingsRepo>();
    var sc = sp.GetRequiredService<ISessionControl>();
    return new BackupService(db, paths.BackupDir, sp.GetRequiredService<AlertRepo>(),
        () => (s.Get(SettingKeys.BackupPath), s.Get(SettingKeys.BackupUser), Secrets.Unprotect(s.Get(SettingKeys.BackupPassword))),
        (p, u, pw) => sc.ConnectShare(p, u, pw));
});
builder.Services.AddSingleton(sp =>
{
    var rollups = sp.GetRequiredService<RollupService>();
    var settings = sp.GetRequiredService<SettingsRepo>();
    // The categorizer is rebuilt when the rules version changes; cheap enough to check per sample.
    Categorizer? cached = null; var version = -1;
    Categorizer Get() { var v = sp.GetRequiredService<RuleRepo>().Version(); if (cached is null || v != version) { cached = rollups.CurrentCategorizer(); version = v; } return cached; }
    return new SamplerEngine(sp.GetRequiredService<ActivityRepo>(), Get, () => settings.TitleCategories);
});
builder.Services.AddSingleton<GuardianState>();
builder.Services.AddSingleton<Queries>();
builder.Services.AddSingleton<ListRefresh>();
builder.Services.AddSingleton<ExtensionHost>();
if (OperatingSystem.IsWindows()) builder.Services.AddSingleton<ISessionControl, WindowsSessionControl>();
else builder.Services.AddSingleton<ISessionControl, NoopSessionControl>();
builder.Services.AddHttpClient("lists", c => { c.Timeout = TimeSpan.FromSeconds(60); c.DefaultRequestHeaders.UserAgent.ParseAdd("Guardian/1.0"); });

// ---- workers ----
builder.Services.AddSingleton<SchedulerWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerWorker>());
builder.Services.AddHostedService<PipeServerWorker>();
builder.Services.AddHostedService<HousekeepingWorker>();

// ---- web ----
var cert = Certificates.LoadOrCreate(paths.CertPath, LoggerFactory.Create(b => b.AddConsole()).CreateLogger("cert"));
builder.Services.AddSingleton(cert);
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.ListenLocalhost(Ports.Local);                       // http://127.0.0.1:47130 — extension, tray, child view
    k.ListenAnyIP(Ports.Web, l => l.UseHttps(cert));      // https://0.0.0.0:47131 — parent dashboard (and /me over loopback)
});
// Cookie/CSRF keys persist in the data folder (DPAPI-protected on Windows) so sessions survive a service restart.
var keysDir = new DirectoryInfo(Path.Combine(paths.DataDir, "keys"));
keysDir.Create();
var dp = builder.Services.AddDataProtection().SetApplicationName(Names.Product).PersistKeysToFileSystem(keysDir);
if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
// Cookie names carry an id generated with the key ring. A reinstall gets new keys, and a browser that still holds the
// previous install's cookies for "localhost" would otherwise send them first (a Secure cookie cannot be overwritten by
// the http setup page), so every login and form submit would fail with "token could not be decrypted".
var installId = InstallId.LoadOrCreate(keysDir);
builder.Services.AddAuthentication(Auth.Scheme).AddCookie(o => { Auth.Configure(o); o.Cookie.Name = "guardian.parent." + installId; });
builder.Services.AddAuthorization(o => o.FallbackPolicy = null);
builder.Services.AddAntiforgery(o => { o.HeaderName = "RequestVerificationToken"; o.Cookie.Name = "guardian.xsrf." + installId; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.HttpOnly = true; });
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToFolder("/Me");
    o.Conventions.AllowAnonymousToPage("/Setup");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AddFolderApplicationModelConvention("/Me", m => m.Filters.Add(new LoopbackOnlyPageFilter()));
    o.Conventions.AddPageApplicationModelConvention("/Setup", m => m.Filters.Add(new LoopbackOnlyPageFilter()));
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// ---- first-start seeding ----
{
    var settings = app.Services.GetRequiredService<SettingsRepo>();
    var rules = app.Services.GetRequiredService<RuleRepo>();
    var lists = app.Services.GetRequiredService<ListRepo>();
    var state = app.Services.GetRequiredService<GuardianState>();
    var yamlPath = Path.Combine(paths.ListsDir, "defaults.yaml");
    var yaml = File.Exists(yamlPath) ? File.ReadAllText(yamlPath) : ListRefresh.EmbeddedText("lists.defaults.yaml");
    rules.SeedIfEmpty(DefaultRules.Parse(yaml));
    ListLoader.LoadDirectory(lists, paths.ListsDir, DateTimeOffset.Now, onlyIfEmpty: true);
    foreach (var name in ListLoader.ShippedLists.Append("custom"))
        if (!lists.Counts().ContainsKey(name)) { var text = ListRefresh.EmbeddedText("lists." + name + ".txt"); if (text.Length > 0) lists.ReplaceList(name, ListLoader.ParseDomains(text), DateTimeOffset.Now); }
    settings.Set(SettingKeys.CertThumbprint, cert.Thumbprint);
    settings.Set(SettingKeys.InstalledVersion, state.InstalledVersion);
    state.CertFingerprint = Certificates.Fingerprint(cert);
    app.Services.GetRequiredService<PolicyRepo>().Current();
    if (OperatingSystem.IsWindows()) DataFolderAcl.Apply(paths.DataDir, app.Logger);
    try
    {
        var protector = app.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector("startup-check");
        var ok = protector.Unprotect(protector.Protect("ok")) == "ok";
        app.Logger.LogInformation("Data protection keys in {Dir}: {Count} key file(s), round-trip {Ok}", keysDir.FullName, keysDir.GetFiles("*.xml").Length, ok ? "ok" : "FAILED");
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Data protection is not working; logins and form submissions will fail"); }
}

app.UseExceptionHandler("/Error");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot"), OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "public,max-age=86400" });
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
    // Until setup is complete, everything on this machine goes to the wizard; the LAN gets a plain "not set up" page.
    var settings = ctx.RequestServices.GetRequiredService<SettingsRepo>();
    var path = ctx.Request.Path;
    if (!settings.SetupCompleted && !path.StartsWithSegments("/setup") && !path.StartsWithSegments("/css") && !path.StartsWithSegments("/js") && !path.StartsWithSegments("/tab") && !path.StartsWithSegments("/ext") && !path.StartsWithSegments("/healthz"))
    {
        if (Loopback.IsLoopback(ctx)) { ctx.Response.Redirect("/setup"); return; }
        ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("Guardian is not set up yet. Finish setup on the child's PC."); return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
Api.MapLocal(app);
Api.MapParent(app);
app.MapRazorPages();

app.Run();

public partial class Program { }
