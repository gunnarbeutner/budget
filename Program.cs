using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddScoped<BudgetService>();

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

// Configure database paths
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
var moneyMoneyDbPath = Path.Combine(dataDir, "moneymoney.db");
var budgetDbPath = Path.Combine(dataDir, "budgets.db");

// Expose the MoneyMoney DB path so services can stat the file (e.g. to report upload freshness).
builder.Services.AddSingleton(new MoneyMoneyDbInfo(moneyMoneyDbPath));

// Configure MoneyMoney database (plain SQLite, read-only)
builder.Services.AddDbContext<MoneyMoneyDbContext>(options =>
{
    options.UseSqlite($"Data Source={moneyMoneyDbPath};Mode=ReadOnly", sqliteOptions =>
    {
        sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    });
}, ServiceLifetime.Scoped);

// Configure Budget database (our data, read-write)
builder.Services.AddDbContext<BudgetDbContext>(options =>
{
    options.UseSqlite($"Data Source={budgetDbPath}");
}, ServiceLifetime.Scoped);

var app = builder.Build();

// Initialize budget database (apply migrations on startup so Docker volumes stay in sync).
using (var scope = app.Services.CreateScope())
{
    var budgetDb = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();
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

// POST /api/moneymoney-db — uploads a fresh MoneyMoney SQLite file from a separate machine.
// Auth: requires "Authorization: Bearer <MONEYMONEY_DB_UPLOAD_TOKEN env var>".
// Implementation: stream body to a temp file next to the live DB, validate SQLite magic,
// then atomic-rename over it. On POSIX, in-flight queries keep reading the old inode until
// they close, so we never serve a half-written file.
app.MapPost("/api/moneymoney-db", async (HttpRequest request, ILogger<Program> logger) =>
{
    // SQLite files can run to hundreds of MB; lift Kestrel's default request size cap for this
    // endpoint by clearing the per-request limit feature before we touch the body.
    var sizeFeature = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature != null && !sizeFeature.IsReadOnly)
    {
        sizeFeature.MaxRequestBodySize = null;
    }

    var expectedToken = Environment.GetEnvironmentVariable("MONEYMONEY_DB_UPLOAD_TOKEN");
    if (string.IsNullOrEmpty(expectedToken))
    {
        return Results.Problem(
            "Upload endpoint disabled: MONEYMONEY_DB_UPLOAD_TOKEN is not set.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!request.Headers.TryGetValue("Authorization", out var authHeader)
        || !string.Equals(authHeader.ToString(), $"Bearer {expectedToken}", StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    Directory.CreateDirectory(Path.GetDirectoryName(moneyMoneyDbPath)!);
    var tempPath = moneyMoneyDbPath + ".upload-" + Guid.NewGuid().ToString("N");
    try
    {
        await using (var tempStream = File.Create(tempPath))
        {
            await request.Body.CopyToAsync(tempStream);
        }

        // SQLite file header is "SQLite format 3\0" (16 bytes).
        await using (var checkStream = File.OpenRead(tempPath))
        {
            var magic = new byte[16];
            var read = await checkStream.ReadAsync(magic.AsMemory(0, 16));
            var header = System.Text.Encoding.ASCII.GetString(magic, 0, read);
            if (read < 16 || header != "SQLite format 3\0")
            {
                File.Delete(tempPath);
                return Results.BadRequest("Uploaded payload is not a SQLite database.");
            }
        }

        File.Move(tempPath, moneyMoneyDbPath, overwrite: true);
        logger.LogInformation("MoneyMoney DB replaced via upload endpoint.");
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
        logger.LogError(ex, "MoneyMoney DB upload failed.");
        return Results.Problem("Upload failed: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();
