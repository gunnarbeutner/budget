using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBudget.Auth;
using OpenBudget.Browser;
using OpenBudget.Cli;
using OpenBudget.Compat;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Connectors.OneDrive;
using System.Security.Claims;
using OpenBudget.Importers;
using OpenBudget.Services;
using System.Globalization;

// `OpenBudget users ...` is the operator CLI (run inside the container); it never starts the web
// server. Handled before the web host is built so its flags aren't parsed as host configuration.
if (args.Length > 0 && args[0].Equals("users", StringComparison.OrdinalIgnoreCase))
    return await UserCli.RunAsync(args[1..]);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(options =>
{
    // Keep a disconnected circuit for 30 minutes (default 3) so a tab left in the background
    // can reconnect to its existing state instead of having to reload (see wwwroot/js/reconnect.js).
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CategoryHierarchyService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<BudgetEntryService>();
builder.Services.AddScoped<BudgetPredictionService>();
builder.Services.AddScoped<CashflowProjectionService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<FreshnessService>();
builder.Services.AddScoped<TransactionQueryService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<BrowseService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<ScheduledPaymentService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<DepotMappingService>();
builder.Services.AddScoped<SecurityTaxService>();
builder.Services.AddScoped<InstrumentMappingService>();
builder.Services.AddScoped<ProjectTimeState>();
builder.Services.AddScoped<CurrentProjectState>();
builder.Services.AddScoped<SharingService>();
builder.Services.AddScoped<SourceService>();
builder.Services.AddScoped<StructureTakeoverService>();
builder.Services.AddScoped<AccountSwitchService>();
builder.Services.AddScoped<CategoryColorService>();
builder.Services.AddScoped<StructureService>();
builder.Services.AddScoped<TransactionEditService>();
builder.Services.AddScoped<ManualTransactionService>();
builder.Services.AddScoped<SplitService>();
builder.Services.AddScoped<CategoryRuleService>();
builder.Services.AddScoped<CategorizationService>();
// Per-circuit holder for the browser's timezone (seeded from the tz cookie by App.razor) so all
// pages render dates/times and "current period" decisions in the user's zone, not the server's.
builder.Services.AddScoped<ClockService>();
builder.Services.AddImporters();
// Importers run for token-authenticated uploads (no logged-in user), so they use the explicit
// unrestricted factory rather than the user-scoped IDbContextFactory.
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<PortfolioPerformanceImportService>(
    sp, sp.GetRequiredService<SystemDbContextFactory>()));
builder.Services.AddSingleton<UploadAuthorizer>();
builder.Services.AddSingleton<ChangeNotificationService>();
// Per-circuit origin id, stamped onto changes this circuit publishes so its own components can
// ignore the echo of their commits (see ChangeOrigin / DataChangeAwareComponent).
builder.Services.AddScoped<ChangeOrigin>();

// Localization: resx files live under /Resources mirroring the namespace
// (e.g. Pages/Index.razor → Resources/Pages/Index.de.resx). Culture is picked from the
// browser's Accept-Language header by default; add CookieRequestCultureProvider later if a
// manual language picker is needed.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

var supportedCultures = new[] { new CultureInfo("en"), new CultureInfo("de") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

// Behind a TLS-terminating reverse proxy: trust X-Forwarded-Proto / X-Forwarded-For so the
// app knows requests are HTTPS even though Kestrel only ever sees plain HTTP on :8080. Without
// this, link / asset URLs would render with the wrong scheme.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // XForwardedHost: the passkey relying-party id defaults to the request host, which must be the
    // public name (budget.example.org), not the proxy's backend address.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // The proxy isn't on a known network in Docker; clear the allow-list so any upstream is trusted.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configure database paths. Everything lives in budgets.db — the imported data is
// imported into the same store under namespaced IDs (see SourceScope) via the upload
// endpoint below.
var dataDir = OpenBudgetPaths.DataDir(builder.Environment.ContentRootPath);
var budgetDbPath = OpenBudgetPaths.DatabasePath(builder.Environment.ContentRootPath);
var uploadStagingDir = Path.Combine(dataDir, "uploads");

// Ensure the data dir exists before EF opens the SQLite file — fresh checkouts and Docker
// volumes both arrive without it, and SQLite errors with "unable to open database file"
// rather than auto-creating the parent directory.
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(uploadStagingDir);

// Use a context *factory* rather than a scoped DbContext. In Blazor Server "scoped" means the
// lifetime of the SignalR circuit, so a scoped context would live for the whole browser session
// and serve stale, change-tracker-cached entities after another session writes. Short-lived
// factory contexts (one per operation) always read current DB state. See ChangeNotificationService
// for the companion cross-circuit refresh signal. The factory services receive is user-scoped
// (see AuthRegistration.AddOpenBudgetDatabase): its contexts filter reads and guard writes.
builder.Services.AddOpenBudgetDatabase(budgetDbPath);
builder.Services.AddOpenBudgetAuth(builder.Environment, builder.Environment.ContentRootPath);
builder.Services.AddBankConnectors(builder.Environment, builder.Environment.ContentRootPath);
builder.Services.AddOneDrive(builder.Environment.ContentRootPath);

// PortfolioPerformance portfolios as accounts: recomputed from each source's current file, which is the uploaded one.
var portfolioFiles = new UploadedPortfolioFileSource(Path.Combine(dataDir, "portfolioperformance"));
builder.Services.AddSingleton(portfolioFiles);
builder.Services.AddSingleton<IPortfolioFileSource>(portfolioFiles);
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PortfolioAccountSync>();
builder.Services.AddScoped<PortfolioAccountSettingsService>();

var app = builder.Build();

// Initialize budget database (apply migrations on startup so Docker volumes stay in sync).
await using (var budgetDb = await app.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync())
{
    await budgetDb.Database.MigrateAsync();
    // Category rules of sources uploaded before rules were imported (idempotent).
    await OpenBudget.Importers.MoneyMoney.MoneyMoneyRuleImport.BackfillAsync(budgetDb, CancellationToken.None);
    var ruleLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenBudget.Rules");
    OpenBudget.Services.RuleExpression.UnknownFieldLog = field => ruleLog.LogWarning("Rule expression uses unknown field '{Field}'; such terms never match", field);
}

// ForwardedHeaders must be the first middleware so downstream components see the right scheme.
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// HTTPS redirection is handled by the reverse proxy out front; the container itself only
// listens on plain HTTP on :8080, so an in-app redirect here would loop or 404 framework
// assets like /_framework/blazor.server.js.

app.UseStaticFiles();

// The remote browser of web logins (bank sign-in pages) streams over a WebSocket.
app.UseWebSockets();

app.UseRequestLocalization();

// Until the first user exists every page answers with only the setup instruction.
app.UseMiddleware<NoUsersMiddleware>();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Everything below requires a logged-in user via the fallback authorization policy unless it opts
// out with AllowAnonymous (static assets, health probe, the token-authenticated upload API; the
// login page opts out itself).
app.MapStaticAssets().AllowAnonymous();

app.MapBlazorHub();
app.MapRazorPages();
app.MapFallbackToPage("/_Host");

// Lightweight liveness probe for the container HEALTHCHECK. Deliberately doesn't touch the
// database — it answers "is the process up and serving HTTP", which is what a restart fixes.
app.MapWebLoginEndpoints();
app.MapOneDriveEndpoints();

app.MapGet("/healthz", () => Results.Ok("healthy")).AllowAnonymous();

// The source app (and any future) importer upload endpoints live in the Importers module.
app.MapImporterEndpoints(uploadStagingDir);

// POST /api/portfolioperformance/{name} — uploads a PortfolioPerformance ".portfolio" file and
// imports its security taxonomy (region/asset-class look-through) for an existing source.
// Auth: same "Authorization: Bearer <OPENBUDGET_UPLOAD_TOKEN>" as the importer uploads.
app.MapPost("/api/portfolioperformance/{name}", async (
    string name,
    HttpRequest request,
    PortfolioPerformanceImportService importer,
    UploadAuthorizer authorizer,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!SourceData.IsValidSlug(name))
        return Results.BadRequest($"Invalid source name '{name}'. Use lowercase letters, digits, and hyphens; start with letter/digit.");

    return await HandlePortfolioPerformanceUploadAsync(name, request, importer, authorizer, logger, uploadStagingDir, ct);
}).AllowAnonymous();

// GET /statements/{id} — a downloaded bank statement (PDF). Authorization is the account's visibility:
// the lookup runs through a context scoped to the signed-in user, so documents of accounts they cannot
// see (or another user's account-less documents) are simply "not found".
app.MapGet("/statements/{id:long}", async (
    long id,
    bool? download,
    HttpContext http,
    SystemDbContextFactory system,
    BankStatementStore store,
    CancellationToken ct) =>
{
    var scope = await AccessScopeProvider.ComputeAsync(system, http.User.FindFirstValue(ClaimTypes.NameIdentifier), ct);
    if (!scope.IsAuthenticated) return Results.NotFound();
    await using var db = await system.CreateDbContextAsync(ct);
    db.Scope = scope;
    var file = await BankLoginService.OpenStatementAsync(db, store, id, ct);
    if (file == null) return Results.NotFound();
    return download == true
        ? Results.File(file.Value.Path, "application/pdf", file.Value.DownloadName)
        : Results.File(file.Value.Path, "application/pdf");
});

// POST /statements/zip (form field "ids", repeated or comma-separated) — several statements as one ZIP,
// streamed. Same access rule as the single download: ids the user cannot see are skipped; none visible → 404.
app.MapPost("/statements/zip", async (
    HttpContext http,
    SystemDbContextFactory system,
    BankStatementStore store,
    CancellationToken ct) =>
{
    var scope = await AccessScopeProvider.ComputeAsync(system, http.User.FindFirstValue(ClaimTypes.NameIdentifier), ct);
    if (!scope.IsAuthenticated) return Results.NotFound();
    if (!http.Request.HasFormContentType) return Results.BadRequest();
    var form = await http.Request.ReadFormAsync(ct);
    var ids = form["ids"].SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(v => long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
        .Where(id => id > 0).Distinct().Take(5000).ToList();
    await using var db = await system.CreateDbContextAsync(ct);
    db.Scope = scope;
    var entries = await StatementArchive.ResolveAsync(db, store, ids, ct);
    if (entries.Count == 0) return Results.NotFound();
    var name = $"Statements {DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.zip";
    return Results.Stream(body => StatementArchive.WriteAsync(body, entries, ct), "application/zip", name);
});

// DELETE /api/sources/{name} — removes a source. Refused with 409 when any project
// references it, with the offending project list in the response. The old route is kept as an
// alias for the already-installed exporter (see LegacyCompat).
Delegate deleteSourceHandler = async (
    string name,
    SystemDbContextFactory dbFactory,
    UploadAuthorizer authorizer,
    ChangeNotificationService notifier,
    ILogger<Program> logger,
    HttpRequest request,
    CancellationToken ct) =>
{
    var denied = UploadAuthorizer.ToFailure(await authorizer.AuthorizeAsync(request.Headers.Authorization.ToString(), name, ct));
    if (denied != null) return denied;

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var source = await db.ImportSources.FirstOrDefaultAsync(s => s.Name == name, ct);
    if (source == null) return Results.NotFound();

    var blockingProjects = await db.Projects
        .Where(p => p.SourceId == source.Id)
        .Select(p => new { p.Id, p.Name })
        .ToListAsync(ct);
    if (blockingProjects.Count > 0)
    {
        return Results.Conflict(new
        {
            error = "Source still referenced by projects. Delete or rebind them first.",
            projects = blockingProjects
        });
    }

    await SourceData.DeleteSourceDataAsync(db, source.Id, ct);
    db.ImportSources.Remove(source);
    await db.SaveChangesAsync(ct);

    notifier.Publish(new DataChange(DataArea.Sources, SourceId: source.Id));
    notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: source.Id));

    logger.LogInformation("Deleted source '{Name}' (id={Id}).", name, source.Id);
    return Results.Ok(new { status = "deleted", name });
};
app.MapDelete("/api/sources/{name}", deleteSourceHandler).AllowAnonymous();
app.MapDelete(LegacyCompat.LegacyDeleteSourceRoute, deleteSourceHandler).AllowAnonymous();

await app.RunAsync();
return 0;

static async Task<IResult> HandlePortfolioPerformanceUploadAsync(
    string sourceName,
    HttpRequest request,
    PortfolioPerformanceImportService importer,
    UploadAuthorizer authorizer,
    ILogger<Program> logger,
    string stagingDir,
    CancellationToken ct)
{
    // PP files are small (single-digit MB), but allow generous headroom over Kestrel's ~30 MB
    // default rather than leaving it unbounded.
    const long maxUploadBytes = 256L * 1024 * 1024;
    var sizeFeature = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature != null && !sizeFeature.IsReadOnly)
        sizeFeature.MaxRequestBodySize = maxUploadBytes;

    var denied = UploadAuthorizer.ToFailure(await authorizer.AuthorizeAsync(request.Headers.Authorization.ToString(), sourceName, ct));
    if (denied != null) return denied;

    try
    {
        var result = await PortfolioPerformanceUpload.ImportAsync(importer, sourceName, request.Body, stagingDir, ct);
        return Results.Ok(new
        {
            status = "ok",
            result.SourceId,
            result.SourceName,
            result.RowsImported,
            result.SecuritiesClassified,
            result.TaxonomiesImported
        });
    }
    catch (KeyNotFoundException ex)
    {
        // Unknown source — the source's data must be imported first.
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidDataException ex) when (ex.Message == PortfolioPerformanceUpload.NotAPortfolioFile)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (InvalidDataException ex)
    {
        // Wrong format (XML / encrypted / not a PP file). The message is safe to surface and is
        // actionable ("save as the default binary format without encryption").
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "PortfolioPerformance upload failed for source '{Source}'.", sourceName);
        return Results.Problem("Upload failed. See server logs for details.", statusCode: StatusCodes.Status500InternalServerError);
    }
}

// Exposes the implicit top-level Program class so WebApplicationFactory<Program> in the E2E
// test project can boot this exact app (same DI/middleware wiring) on a real Kestrel port.
// The private constructor keeps it non-instantiable: top-level statements own the entry point,
// so this type only exists as a reference handle, never an object (S1118).
public partial class Program
{
    private Program() { }
}
