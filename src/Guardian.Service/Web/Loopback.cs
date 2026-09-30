using System.Net;

namespace Guardian.Service.Web;

public static class Loopback
{
    /// <summary>True only when both ends of the connection are on this machine. The /me, /setup, /tab and /ext routes require it.</summary>
    public static bool IsLoopback(HttpContext ctx)
    {
        var local = ctx.Connection.LocalIpAddress;
        var remote = ctx.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote) && (local is null || IPAddress.IsLoopback(local));
    }

    public static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Endpoint filter: 403 unless loopback.</summary>
public sealed class LoopbackOnlyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        if (!Loopback.IsLoopback(ctx.HttpContext)) return Results.StatusCode(403);
        return await next(ctx);
    }
}

/// <summary>Page filter for the Razor pages under /Me and /Setup.</summary>
public sealed class LoopbackOnlyPageFilter : Microsoft.AspNetCore.Mvc.Filters.IPageFilter
{
    public void OnPageHandlerSelected(Microsoft.AspNetCore.Mvc.Filters.PageHandlerSelectedContext context) { }
    public void OnPageHandlerExecuting(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context)
    {
        if (!Loopback.IsLoopback(context.HttpContext)) context.Result = new Microsoft.AspNetCore.Mvc.StatusCodeResult(403);
    }
    public void OnPageHandlerExecuted(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutedContext context) { }
}
