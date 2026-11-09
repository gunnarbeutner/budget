using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Compat;
using OpenBudget.Importers;
using OpenBudget.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(options =>
{
    // Keep a disconnected circuit for 30 minutes (default 3) so a tab left in the background
    // can reconnect to its existing state instead of having to reload (see wwwroot/js/reconnect.js).
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
});
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
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<DepotMappingService>();
builder.Services.AddScoped<SecurityTaxService>();
builder.Services.AddScoped<InstrumentMappingService>();
builder.Services.AddScoped<ProjectTimeState>();
// Per-circuit holder for the browser's timezone (seeded from the tz cookie by App.razor) so all
// pages render dates/times and "current period" decisions in the user's zone, not the server's.
builder.Services.AddScoped<ClockService>();
builder.Services.AddImporters();
builder.Services.AddSingleton<PortfolioPerformanceImportService>();
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
// this, UseHttpsRedirection (now removed) would 307-loop, and link / asset URLs would render
// with the wrong scheme.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The proxy isn't on a known network in Docker; clear the allow-list so any upstream is trusted.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configure database paths. Everything lives in budgets.db now — the imported data is
// imported into the same store under namespaced IDs (see SourceScope) via the upload
// endpoint below.
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
var budgetDbPath = Path.Combine(dataDir, "budgets.db");
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
// for the companion cross-circuit refresh signal.
builder.Services.AddDbContextFactory<BudgetDbContext>(options =>
{
    options.UseSqlite($"Data Source={budgetDbPath}");
});

var app = builder.Build();

// Initialize budget database (apply migrations on startup so Docker volumes stay in sync).
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BudgetDbContext>>();
    await using var budgetDb = await dbFactory.CreateDbContextAsync();
    await budgetDb.Database.MigrateAsync();
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

app.UseRequestLocalization();

app.UseRouting();

app.MapStaticAssets();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Lightweight liveness probe for the container HEALTHCHECK. Deliberately doesn't touch the
// database — it answers "is the process up and serving HTTP", which is what a restart fixes.
app.MapGet("/healthz", () => Results.Ok("healthy"));

// The source app (and any future) importer upload endpoints live in the Importers module.
app.MapImporterEndpoints(uploadStagingDir);

// POST /api/portfolioperformance/{name} — uploads a PortfolioPerformance ".portfolio" file and
// imports its security taxonomy (region/asset-class look-through) for an existing source.
// Auth: same "Authorization: Bearer <OPENBUDGET_UPLOAD_TOKEN>" as the importer uploads.
app.MapPost("/api/portfolioperformance/{name}", async (
    string name,
    HttpRequest request,
    PortfolioPerformanceImportService importer,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!SourceData.IsValidSlug(name))
        return Results.BadRequest($"Invalid source name '{name}'. Use lowercase letters, digits, and hyphens; start with letter/digit.");

    return await HandlePortfolioPerformanceUploadAsync(name, request, importer, logger, uploadStagingDir, ct);
});

// DELETE /api/sources/{name} — removes a source. Refused with 409 when any project
// references it, with the offending project list in the response. The old route is kept as an
// alias for the already-installed exporter (see LegacyCompat).
Delegate deleteSourceHandler = async (
    string name,
    IDbContextFactory<BudgetDbContext> dbFactory,
    ChangeNotificationService notifier,
    ILogger<Program> logger,
    HttpRequest request,
    CancellationToken ct) =>
{
    var expectedToken = LegacyCompat.ResolveUploadToken(logger);
    if (string.IsNullOrEmpty(expectedToken))
        return Results.Problem($"Source deletion disabled: {LegacyCompat.UploadTokenVariable} is not set.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!UploadAuth.IsAuthorized(request.Headers.Authorization.ToString(), expectedToken))
        return Results.Unauthorized();

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
app.MapDelete("/api/sources/{name}", deleteSourceHandler);
app.MapDelete(LegacyCompat.LegacyDeleteSourceRoute, deleteSourceHandler);

await app.RunAsync();

static async Task<IResult> HandlePortfolioPerformanceUploadAsync(
    string sourceName,
    HttpRequest request,
    PortfolioPerformanceImportService importer,
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

    var expectedToken = LegacyCompat.ResolveUploadToken(logger);
    if (string.IsNullOrEmpty(expectedToken))
        return Results.Problem($"Upload endpoint disabled: {LegacyCompat.UploadTokenVariable} is not set.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!UploadAuth.IsAuthorized(request.Headers.Authorization.ToString(), expectedToken))
        return Results.Unauthorized();

    Directory.CreateDirectory(stagingDir);
    var tempPath = Path.Combine(stagingDir, $"{sourceName}-{Guid.NewGuid():N}.portfolio");
    try
    {
        await using (var tempStream = File.Create(tempPath))
        {
            await request.Body.CopyToAsync(tempStream, ct);
        }

        // PortfolioPerformance ".portfolio" files are ZIP archives — magic "PK\x03\x04".
        await using (var checkStream = File.OpenRead(tempPath))
        {
            var magic = new byte[4];
            var read = await checkStream.ReadAsync(magic.AsMemory(0, 4), ct);
            if (read < 4 || magic[0] != 'P' || magic[1] != 'K' || magic[2] != 0x03 || magic[3] != 0x04)
                return Results.BadRequest("Uploaded payload is not a PortfolioPerformance (.portfolio) file.");
        }

        var result = await importer.ImportFromFileAsync(sourceName, tempPath, ct);
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
    finally
    {
        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
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
