using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddSingleton<MoneyMoneyImportService>();

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
// imported into the same store under namespaced IDs (see SourceScope). The legacy
// data/moneymoney.db file is absorbed once on first boot then renamed to *.legacy.
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
var legacyMoneyMoneyDbPath = Path.Combine(dataDir, "moneymoney.db");
var budgetDbPath = Path.Combine(dataDir, "budgets.db");
var uploadStagingDir = Path.Combine(dataDir, "uploads");

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

    // Legacy bootstrap: when a pre-multi-source moneymoney.db exists on disk and our mm_*
    // tables are still empty for the default source, absorb it into source 1 (identity
    // namespacing → existing budgets.db references stay valid) and rename the file.
    AbsorbLegacyMoneyMoneyDb(budgetDb, legacyMoneyMoneyDbPath, app.Logger);
}

Directory.CreateDirectory(uploadStagingDir);

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
    BudgetDbContext db,
    ILogger<Program> logger,
    HttpRequest request,
    CancellationToken ct) =>
{
    var expectedToken = Environment.GetEnvironmentVariable("MONEYMONEY_DB_UPLOAD_TOKEN");
    if (string.IsNullOrEmpty(expectedToken))
        return Results.Problem("Source deletion disabled: MONEYMONEY_DB_UPLOAD_TOKEN is not set.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!request.Headers.TryGetValue("Authorization", out var authHeader)
        || !string.Equals(authHeader.ToString(), $"Bearer {expectedToken}", StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

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
    db.ImportSources.Remove(source);
    await db.SaveChangesAsync(ct);

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
    // SQLite files can run to hundreds of MB; lift Kestrel's default request size cap for this
    // endpoint by clearing the per-request limit feature before we touch the body.
    var sizeFeature = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature != null && !sizeFeature.IsReadOnly)
        sizeFeature.MaxRequestBodySize = null;

    var expectedToken = Environment.GetEnvironmentVariable("MONEYMONEY_DB_UPLOAD_TOKEN");
    if (string.IsNullOrEmpty(expectedToken))
        return Results.Problem("Upload endpoint disabled: MONEYMONEY_DB_UPLOAD_TOKEN is not set.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!request.Headers.TryGetValue("Authorization", out var authHeader)
        || !string.Equals(authHeader.ToString(), $"Bearer {expectedToken}", StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

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
            result.CategoryGroupsImported,
            result.CategoriesImported,
            result.TransactionsImported,
            result.FileSizeBytes
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "MoneyMoney DB upload failed for source '{Source}'.", sourceName);
        return Results.Problem("Upload failed: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
    finally
    {
        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }
}

static void AbsorbLegacyMoneyMoneyDb(BudgetDbContext db, string legacyPath, ILogger logger)
{
    // Pre-multi-source installs had a single read-only data/moneymoney.db file. The migration
    // creates the empty mm_* tables; this bootstrap copies the legacy file's rows into source 1
    // (identity namespacing → existing FK references in budgets.db stay valid), updates the
    // source's LastUploadAt to the file's mtime, then renames the file out of the way so we
    // don't import it again on the next boot.
    if (!File.Exists(legacyPath)) return;
    bool hasAnyRows = db.Accounts.Any(a => a.SourceId == SourceScope.LegacySourceId)
        || db.Categories.Any(c => c.SourceId == SourceScope.LegacySourceId)
        || db.CategoryGroups.Any(cg => cg.SourceId == SourceScope.LegacySourceId)
        || db.Transactions.Any(t => t.SourceId == SourceScope.LegacySourceId);
    if (hasAnyRows) return;

    logger.LogInformation("Absorbing legacy MoneyMoney DB at {Path} into source 1 (default).", legacyPath);
    try
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = legacyPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        using var src = new SqliteConnection(connStr);
        src.Open();

        using var destConn = (SqliteConnection)db.Database.GetDbConnection();
        if (destConn.State != System.Data.ConnectionState.Open) destConn.Open();

        // MoneyMoney's own DB has dangling rowid references (e.g. category_groups with
        // group_key=0 pointing at a non-existent parent). Our mm_* tables declare FKs because EF
        // generated them from the model, so we have to silence FK enforcement on this connection
        // for the bulk copy to succeed. Reset afterwards.
        using (var pragmaOff = destConn.CreateCommand())
        {
            pragmaOff.CommandText = "PRAGMA foreign_keys = OFF";
            pragmaOff.ExecuteNonQuery();
        }

        using var tx = destConn.BeginTransaction();

        CopyTable(src, destConn, tx,
            "SELECT rowid, name, iban, currency, type, portfolio, balances, sort, refresh_tsp, error FROM accounts",
            "INSERT INTO mm_accounts (Id, SourceId, name, iban, currency, type, portfolio, balances, sort, refresh_tsp, error) VALUES ($id, $sid, $a, $b, $c, $d, $e, $f, $g, $h, $i)",
            9);
        CopyTable(src, destConn, tx,
            "SELECT rowid, name, group_key, sort, color FROM category_groups",
            "INSERT INTO mm_category_groups (Id, SourceId, name, group_key, sort, color) VALUES ($id, $sid, $a, $b, $c, $d)",
            4);
        CopyTable(src, destConn, tx,
            "SELECT rowid, name, type, color, rules, group_key, sort FROM categories",
            "INSERT INTO mm_categories (Id, SourceId, name, type, color, rules, group_key, sort) VALUES ($id, $sid, $a, $b, $c, $d, $e, $f)",
            6);
        CopyTable(src, destConn, tx,
            "SELECT rowid, local_account_key, amount, currency, timestamp, name, comment, category_key, checkmark, splitted FROM transactions",
            "INSERT INTO mm_transactions (Id, SourceId, local_account_key, amount, currency, timestamp, name, comment, category_key, checkmark, splitted) VALUES ($id, $sid, $a, $b, $c, $d, $e, $f, $g, $h, $i)",
            9);

        tx.Commit();

        // Stamp LastUploadAt with the legacy file's mtime so the freshness banner shows the
        // right age right after migration.
        var fileMtime = File.GetLastWriteTimeUtc(legacyPath);
        db.Database.ExecuteSqlRaw(
            "UPDATE money_money_sources SET LastUploadAt = {0} WHERE Id = {1}",
            fileMtime, SourceScope.LegacySourceId);

        var renamedPath = legacyPath + ".legacy";
        if (File.Exists(renamedPath)) File.Delete(renamedPath);
        File.Move(legacyPath, renamedPath);
        logger.LogInformation("Legacy MoneyMoney DB absorbed; renamed to {Renamed}.", renamedPath);

        // Re-enable FK enforcement for the lifetime of the connection — EF expects it on.
        using (var pragmaOn = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand())
        {
            pragmaOn.CommandText = "PRAGMA foreign_keys = ON";
            pragmaOn.ExecuteNonQuery();
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Legacy MoneyMoney DB absorption failed — leaving file in place.");
    }
}

static void CopyTable(SqliteConnection src, SqliteConnection dest, SqliteTransaction tx, string selectSql, string insertSql, int valueColumnCount)
{
    // Generic legacy copy: maps SELECT columns (excluding rowid) to INSERT $a, $b, ... in
    // order. Used only by the once-per-install bootstrap, so the verbose mapping is fine.
    using var insert = dest.CreateCommand();
    insert.CommandText = insertSql;
    insert.Transaction = tx;
    insert.Parameters.AddWithValue("$id", 0L);
    insert.Parameters.AddWithValue("$sid", SourceScope.LegacySourceId);
    var letters = new[] { "$a", "$b", "$c", "$d", "$e", "$f", "$g", "$h", "$i", "$j" };
    for (int i = 0; i < valueColumnCount; i++)
        insert.Parameters.Add(new SqliteParameter(letters[i], DBNull.Value));

    using var selectCmd = src.CreateCommand();
    selectCmd.CommandText = selectSql;
    using var reader = selectCmd.ExecuteReader();
    while (reader.Read())
    {
        long rowid = reader.GetInt64(0);
        insert.Parameters["$id"].Value = SourceScope.Namespace(SourceScope.LegacySourceId, rowid);
        for (int i = 0; i < valueColumnCount; i++)
        {
            int colIndex = i + 1;
            insert.Parameters[letters[i]].Value = reader.IsDBNull(colIndex) ? DBNull.Value : reader.GetValue(colIndex);
        }
        insert.ExecuteNonQuery();
    }
}
