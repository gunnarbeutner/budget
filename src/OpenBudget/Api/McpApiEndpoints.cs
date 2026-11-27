using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OpenBudget.Auth;
using OpenBudget.Services;

namespace OpenBudget.Api;

/// <summary>
/// The read-only MCP API under <c>/api/mcp/v1/</c> (docs/mcp-api.md), for the MCP gateway that exposes OpenBudget to
/// Claude. Off unless <c>McpApi:Enabled</c> is true (then every <c>/api/mcp/</c> path answers 404). Authenticated only
/// by a personal API token (<see cref="ApiTokenAuthenticationHandler"/>), never by the login cookie; each request acts
/// for the token's user (<see cref="ActingUser"/>), so the user-scoped services read exactly what that user sees.
/// </summary>
public static class McpApiEndpoints
{
    public const string EnabledSetting = "McpApi:Enabled";
    public const string BasePath = "/api/mcp/v1";

    public static IServiceCollection AddMcpApi(this IServiceCollection services)
    {
        services.AddScoped<McpApiService>();
        return services;
    }

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>(EnabledSetting);

    /// <summary>With the API off, its paths answer 404 before authentication runs. Goes before UseRouting.</summary>
    public static IApplicationBuilder UseMcpApiGate(this WebApplication app)
    {
        if (IsEnabled(app.Configuration)) return app;
        return app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await Error(StatusCodes.Status404NotFound, "not_found", "The MCP API is not enabled.").ExecuteAsync(ctx);
                return;
            }
            await next(ctx);
        });
    }

    public static void MapMcpApi(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration)) return;

        var policy = new AuthorizationPolicyBuilder(MicrosoftSignIn.McpBearerScheme).RequireAuthenticatedUser().Build();
        var api = app.MapGroup(BasePath).RequireAuthorization(policy).DisableAntiforgery();
        // Each request acts for the token's user: the user-scoped services resolve their access scope from it.
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var userId = ctx.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return Error(StatusCodes.Status401Unauthorized, "unauthorized", "A valid API token is required.");
            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenBudget.McpApi")
                .LogInformation("MCP {Path} via {Method}", ctx.HttpContext.Request.Path.Value, ctx.HttpContext.User.FindFirstValue(ApiTokenAuthenticationHandler.MethodClaim));
            ctx.HttpContext.RequestServices.GetRequiredService<ActingUser>().Set(userId);
            return await next(ctx);
        });

        api.MapGet("/accounts", (McpApiService s, CancellationToken ct) => s.GetAccountsAsync(ct));
        api.MapGet("/balances", (McpApiService s, CancellationToken ct) => s.GetBalancesAsync(ct));

        api.MapGet("/transactions", async (HttpRequest req, McpApiService s, CancellationToken ct) =>
        {
            var q = req.Query;
            if (!TryDate(q["from"], out var from)) return InvalidParameter("from");
            if (!TryDate(q["to"], out var to)) return InvalidParameter("to");
            if (from is { } f && to is { } t && t < f) return InvalidParameter("to", "'to' must not be before 'from'.");
            long? accountId = null;
            if (q["accountId"] is { Count: > 0 } rawAccount && !string.IsNullOrWhiteSpace(rawAccount))
            {
                if (!long.TryParse(rawAccount, NumberStyles.None, CultureInfo.InvariantCulture, out var a)) return InvalidParameter("accountId");
                accountId = a;
            }
            var limit = McpApiService.DefaultLimit;
            if (q["limit"] is { Count: > 0 } rawLimit && !string.IsNullOrWhiteSpace(rawLimit))
            {
                if (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)
                    return InvalidParameter("limit", $"'limit' must be between 1 and {McpApiService.MaxLimit}.");
                limit = Math.Min(limit, McpApiService.MaxLimit);
            }
            var text = Trimmed(q["query"]);
            if (text is { Length: > 200 }) return InvalidParameter("query", "'query' is too long.");
            var filter = new McpTransactionFilter(text, from, to?.AddDays(1), accountId, Trimmed(q["category"]), limit);
            return Results.Ok(await s.SearchTransactionsAsync(filter, ct));
        });

        api.MapGet("/spending/summary", async (HttpRequest req, McpApiService s, TimeProvider time, CancellationToken ct) =>
        {
            var q = req.Query;
            if (!TryDate(q["from"], out var from)) return InvalidParameter("from");
            if (!TryDate(q["to"], out var to)) return InvalidParameter("to");
            var today = time.GetUtcNow().UtcDateTime.Date;
            var start = from ?? new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var end = to ?? today;
            if (end < start) return InvalidParameter("to", "'to' must not be before 'from'.");
            if (end > start.AddYears(10)) return InvalidParameter("to", "The period may span at most 10 years.");
            var groupBy = Trimmed(q["groupBy"]) ?? "category";
            if (groupBy is not ("category" or "month")) return InvalidParameter("groupBy", "'groupBy' must be 'category' or 'month'.");
            return Results.Ok(await s.SummarizeSpendingAsync(start, end.AddDays(1), groupBy == "month", ct));
        });

        api.MapGet("/budgets", async (HttpRequest req, McpApiService s, TimeProvider time, CancellationToken ct) =>
        {
            var year = time.GetUtcNow().Year;
            if (Trimmed(req.Query["year"]) is { } rawYear
                && (!int.TryParse(rawYear, NumberStyles.None, CultureInfo.InvariantCulture, out year) || year < 1900 || year > 2200))
                return InvalidParameter("year");
            return Results.Ok(await s.GetBudgetsAsync(year, ct));
        });

        // Anything else under the API answers in the API's own error format rather than the app's pages.
        api.Map("/{**rest}", () => Error(StatusCodes.Status404NotFound, "not_found", "No such endpoint."));
    }

    private static string? Trimmed(Microsoft.Extensions.Primitives.StringValues value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.ToString().Trim();

    /// <summary>An ISO-8601 date (<c>2026-03-01</c>); a full timestamp counts by its UTC date. Missing → null.</summary>
    internal static bool TryDate(Microsoft.Extensions.Primitives.StringValues raw, out DateTime? date)
    {
        date = null;
        var text = Trimmed(raw);
        if (text == null) return true;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            date = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            return true;
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) && text.Contains('T', StringComparison.Ordinal))
        {
            date = DateTime.SpecifyKind(ts.UtcDateTime.Date, DateTimeKind.Utc);
            return true;
        }
        return false;
    }

    private static IResult InvalidParameter(string name, string? message = null) =>
        Error(StatusCodes.Status400BadRequest, "invalid_parameter", message ?? $"'{name}' is not valid (dates are ISO-8601, e.g. 2026-03-01).");

    internal static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = code, message }, statusCode: status);
}
