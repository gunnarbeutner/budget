using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using OpenBudget.AccountPages;
using OpenBudget.Services;
using System.Net;

namespace OpenBudget.Auth;

/// <summary>
/// While no user exists yet, every page answers with only the setup instruction (HTTP 503) — no
/// login form, no app shell, nothing else. The token-authenticated upload API and the health probe
/// are unaffected; static assets are served before this middleware runs.
/// </summary>
public sealed class NoUsersMiddleware
{
    /// <summary>The exact command that creates the first admin (documented in the README).</summary>
    public const string SetupCommand = "docker exec openbudget dotnet OpenBudget.dll users add <username> --admin";

    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    private readonly RequestDelegate _next;
    private readonly SystemDbContextFactory _system;
    private long _lastPositiveCheckTicks;

    public NoUsersMiddleware(RequestDelegate next, SystemDbContextFactory system)
    {
        _next = next;
        _system = system;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase) || await HasUsersAsync(context.RequestAborted))
        {
            await _next(context);
            return;
        }

        var l = context.RequestServices.GetRequiredService<IStringLocalizer<LoginModel>>();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        var title = WebUtility.HtmlEncode(l["Setup_Title"].Value);
        var message = WebUtility.HtmlEncode(l["Setup_Message"].Value);
        var command = WebUtility.HtmlEncode(SetupCommand);
        await context.Response.WriteAsync(
            $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            $"<title>{title}</title></head><body style=\"font-family: system-ui, sans-serif; max-width: 40rem; margin: 4rem auto; padding: 0 1rem\">" +
            $"<h1>{title}</h1><p>{message}</p><pre><code>{command}</code></pre></body></html>");
    }

    private async Task<bool> HasUsersAsync(CancellationToken ct)
    {
        var last = Interlocked.Read(ref _lastPositiveCheckTicks);
        if (last != 0 && TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond - last) < RecheckInterval)
            return true;

        await using var db = await _system.CreateDbContextAsync(ct);
        if (!await db.Users.AnyAsync(ct)) return false;
        Interlocked.Exchange(ref _lastPositiveCheckTicks, Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
        return true;
    }
}
