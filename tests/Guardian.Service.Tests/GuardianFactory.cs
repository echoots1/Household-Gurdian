using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Guardian.Service.Tests;

/// <summary>
/// Boots the real service (workers included) against a temp data folder. TestServer has no real sockets, so a
/// header "X-Test-Remote" (loopback|lan) decides what the loopback filter sees; default is a LAN client.
/// </summary>
public sealed class GuardianFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "guardian-svc-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataDir);
        builder.UseSetting("GUARDIAN_DATA", DataDir);
        builder.UseEnvironment("Production");
        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, FakeConnectionFilter>());
    }

    private sealed class FakeConnectionFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                var loop = ctx.Request.Headers["X-Test-Remote"].ToString() == "loopback";
                ctx.Connection.RemoteIpAddress = loop ? IPAddress.Loopback : IPAddress.Parse("192.168.1.50");
                ctx.Connection.LocalIpAddress = loop ? IPAddress.Loopback : IPAddress.Parse("192.168.1.10");
                await n();
            });
            next(app);
        };
    }

    public HttpClient Lan() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    public HttpClient Local()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-Test-Remote", "loopback");
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch { }
    }
}
