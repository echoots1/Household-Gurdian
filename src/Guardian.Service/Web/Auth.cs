using System.Security.Claims;
using Guardian.Contracts;
using Guardian.Core.Auth;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Guardian.Service.Web;

/// <summary>One parent login. Password → argon2id hash in settings; cookie session; 5 attempts then 15-minute lockout.</summary>
public static class Auth
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static void Configure(CookieAuthenticationOptions o)
    {
        o.Cookie.Name = "guardian.parent";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.LoginPath = "/login";
        o.LogoutPath = "/auth/logout";
        o.AccessDeniedPath = "/login";
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask;
        };
    }

    public static async Task<string?> TryLoginAsync(HttpContext ctx, string password, SettingsRepo settings, LoginLimiter limiter, IClock clock)
    {
        var ip = Loopback.ClientIp(ctx);
        var now = clock.Now;
        if (limiter.IsLockedOut(ip, now)) return "Too many attempts. Try again in 15 minutes.";
        var hash = settings.Get(SettingKeys.ParentPasswordHash);
        if (hash is null) return "Setup is not complete. Open the dashboard on the child's PC to finish setup.";
        if (!PasswordHasher.Verify(password, hash)) { limiter.Record(ip, false, now); return "Wrong password."; }
        limiter.Record(ip, true, now);
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "parent"), new Claim(ClaimTypes.Role, "parent") }, Scheme);
        await ctx.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true, IssuedUtc = now, ExpiresUtc = now.AddHours(12) });
        return null;
    }
}
