using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddScoped<CategoryHierarchyService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<BudgetEntryService>();
builder.Services.AddScoped<BudgetPredictionService>();
builder.Services.AddScoped<CashflowProjectionService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<FreshnessService>();
builder.Services.AddScoped<TransactionQueryService>();
builder.Services.AddScoped<BrowseService>();
builder.Services.AddScoped<ProjectTimeState>();
builder.Services.AddSingleton<MoneyMoneyImportService>();
builder.Services.AddSingleton<ChangeNotificationService>();

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

// Configure database paths. Everything lives in budgets.db now — the MoneyMoney data is
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
    using var budgetDb = dbFactory.CreateDbContext();
    budgetDb.Database.Migrate();
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

// POST /api/moneymoney-db/{name} — uploads a fresh MoneyMoney SQLite file and imports it into
// the named source. First upload to a new name auto-registers the source.
// Auth: requires "Authorization: Bearer <MONEYMONEY_DB_UPLOAD_TOKEN env var>".
app.MapPost("/api/moneymoney-db/{name}", async (
    string name,
    HttpRequest request,
    MoneyMoneyImportService importer,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!MoneyMoneyImportService.IsValidSlug(name))
        return Results.BadRequest($"Invalid source name '{name}'. Use lowercase letters, digits, and hyphens; start with letter/digit.");

    return await HandleUploadAsync(name, request, importer, logger, uploadStagingDir, ct);
});

// DELETE /api/moneymoney-sources/{name} — removes a source. Refused with 409 when any project
// references it, with the offending project list in the response.
app.MapDelete("/api/moneymoney-sources/{name}", async (
    string name,
    IDbContextFactory<BudgetDbContext> dbFactory,
    ChangeNotificationService notifier,
    ILogger<Program> logger,
    HttpRequest request,
    CancellationToken ct) =>
{
    var expectedToken = Environment.GetEnvironmentVariable("MONEYMONEY_DB_UPLOAD_TOKEN");
    if (string.IsNullOrEmpty(expectedToken))
        return Results.Problem("Source deletion disabled: MONEYMONEY_DB_UPLOAD_TOKEN is not set.", statusCode: StatusCodes.Status503ServiceUnavailable);
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

    await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_transactions WHERE SourceId = {0}", new object[] { source.Id }, ct);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_categories WHERE SourceId = {0}", new object[] { source.Id }, ct);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_category_groups WHERE SourceId = {0}", new object[] { source.Id }, ct);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_accounts WHERE SourceId = {0}", new object[] { source.Id }, ct);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_account_groups WHERE SourceId = {0}", new object[] { source.Id }, ct);
    db.ImportSources.Remove(source);
    await db.SaveChangesAsync(ct);

    notifier.Publish(new DataChange(DataArea.Sources, SourceId: source.Id));
    notifier.Publish(new DataChange(DataArea.MoneyMoneyData, SourceId: source.Id));

    logger.LogInformation("Deleted MoneyMoney source '{Name}' (id={Id}).", name, source.Id);
    return Results.Ok(new { status = "deleted", name });
});

app.Run();

static async Task<IResult> HandleUploadAsync(
    string sourceName,
    HttpRequest request,
    MoneyMoneyImportService importer,
    ILogger<Program> logger,
    string stagingDir,
    CancellationToken ct)
{
    // SQLite files can run to hundreds of MB, so lift Kestrel's ~30 MB default for this endpoint.
    // Raise it to a generous-but-finite ceiling rather than clearing it (null = unbounded): the
    // body is streamed to disk on the same volume as budgets.db before validation, so an
    // unbounded cap is a trivial disk-fill DoS for anyone holding the upload token.
    const long maxUploadBytes = 2L * 1024 * 1024 * 1024; // 2 GiB
    var sizeFeature = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature != null && !sizeFeature.IsReadOnly)
        sizeFeature.MaxRequestBodySize = maxUploadBytes;

    var expectedToken = Environment.GetEnvironmentVariable("MONEYMONEY_DB_UPLOAD_TOKEN");
    if (string.IsNullOrEmpty(expectedToken))
        return Results.Problem("Upload endpoint disabled: MONEYMONEY_DB_UPLOAD_TOKEN is not set.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!UploadAuth.IsAuthorized(request.Headers.Authorization.ToString(), expectedToken))
        return Results.Unauthorized();

    Directory.CreateDirectory(stagingDir);
    var tempPath = Path.Combine(stagingDir, $"{sourceName}-{Guid.NewGuid():N}.sqlite");
    try
    {
        await using (var tempStream = File.Create(tempPath))
        {
            await request.Body.CopyToAsync(tempStream, ct);
        }

        // SQLite file header is "SQLite format 3\0" (16 bytes).
        await using (var checkStream = File.OpenRead(tempPath))
        {
            var magic = new byte[16];
            var read = await checkStream.ReadAsync(magic.AsMemory(0, 16), ct);
            var header = System.Text.Encoding.ASCII.GetString(magic, 0, read);
            if (read < 16 || header != "SQLite format 3\0")
            {
                return Results.BadRequest("Uploaded payload is not a SQLite database.");
            }
        }

        var result = await importer.ImportFromFileAsync(sourceName, tempPath, ct);
        return Results.Ok(new
        {
            status = "ok",
            result.SourceId,
            result.SourceName,
            result.AccountsImported,
            result.AccountGroupsImported,
            result.CategoryGroupsImported,
            result.CategoriesImported,
            result.TransactionsImported,
            result.FileSizeBytes
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "MoneyMoney DB upload failed for source '{Source}'.", sourceName);
        // Don't echo ex.Message to the caller — it can carry file paths / SQLite internals. The
        // detail is in the server log above; the client just needs to know it failed.
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
