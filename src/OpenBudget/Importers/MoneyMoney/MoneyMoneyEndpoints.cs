using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>
/// HTTP upload endpoints for MoneyMoney, all with "Authorization: Bearer &lt;token&gt;":
/// <list type="bullet">
/// <item><see cref="Route"/>: a database snapshot, the raw SQLite file as the body (application/octet-stream);</item>
/// <item><see cref="StatementsCheckRoute"/>: JSON <c>{"statements":[{"accountKey":1,"sha256":"..."}]}</c> of the
/// statements MoneyMoney holds; answers <c>{"wanted":["..."]}</c>, the hashes still to upload;</item>
/// <item><see cref="StatementsRoute"/>: one statement PDF as the body, with <c>accountKey</c>, <c>date</c>
/// (yyyy-MM-dd) and <c>name</c> as query parameters.</item>
/// </list>
/// </summary>
internal static class MoneyMoneyEndpoints
{
    public const string Route = "/api/import/moneymoney/{name}";
    public const string StatementsCheckRoute = "/api/import/moneymoney/{name}/statements/check";
    public const string StatementsRoute = "/api/import/moneymoney/{name}/statements";

    /// <summary>Largest statement file accepted.</summary>
    public const long MaxStatementBytes = 100L * 1024 * 1024;

    /// <summary>
    /// OLD-EXPORTER SHIM: the route the already-installed MoneyMoneyExport tool posts to. Must
    /// keep working until that tool is retired.
    /// </summary>
    public const string LegacyExporterRoute = "/api/moneymoney-db/{name}";

    public static void Map(IEndpointRouteBuilder app, string stagingDir)
    {
        Delegate handler = (string name, HttpRequest request, MoneyMoneyImportService importer,
            UploadAuthorizer authorizer, ILogger<MoneyMoneyImportService> logger, CancellationToken ct) =>
            HandleUploadAsync(name, request, importer, authorizer, logger, stagingDir, ct);

        // Token-authenticated (see UploadAuthorizer), not user-authenticated: exempt from the
        // app-wide login requirement.
        app.MapPost(Route, handler).AllowAnonymous();
        app.MapPost(LegacyExporterRoute, handler).AllowAnonymous();
        app.MapPost(StatementsCheckRoute, (string name, HttpRequest request, MoneyMoneyStatementImport statements, UploadAuthorizer authorizer, CancellationToken ct) =>
            HandleStatementsCheckAsync(name, request, statements, authorizer, ct)).AllowAnonymous();
        app.MapPost(StatementsRoute, (string name, HttpRequest request, MoneyMoneyStatementImport statements, UploadAuthorizer authorizer, CancellationToken ct) =>
            HandleStatementUploadAsync(name, request, statements, authorizer, ct)).AllowAnonymous();
    }

    private sealed record StatementsCheckRequest(List<StatementsCheckItem>? Statements);

    private sealed record StatementsCheckItem(long AccountKey, string? Sha256);

    internal static async Task<IResult> HandleStatementsCheckAsync(string sourceName, HttpRequest request, MoneyMoneyStatementImport statements, UploadAuthorizer authorizer, CancellationToken ct)
    {
        if (!SourceData.IsValidSlug(sourceName)) return Results.BadRequest("Invalid source name.");
        var denied = UploadAuthorizer.ToFailure(await authorizer.AuthorizeAsync(request.Headers.Authorization.ToString(), sourceName, ct));
        if (denied != null) return denied;
        StatementsCheckRequest? body;
        try { body = await request.ReadFromJsonAsync<StatementsCheckRequest>(ct); }
        catch (System.Text.Json.JsonException) { return Results.BadRequest("Invalid JSON."); }
        var refs = (body?.Statements ?? [])
            .Where(s => s.Sha256 is { Length: 64 } h && h.All(Uri.IsHexDigit))
            .Select(s => new MoneyMoneyStatementRef(s.AccountKey, s.Sha256!))
            .ToList();
        return Results.Ok(new { wanted = await statements.WantedAsync(sourceName, refs, ct) });
    }

    internal static async Task<IResult> HandleStatementUploadAsync(string sourceName, HttpRequest request, MoneyMoneyStatementImport statements, UploadAuthorizer authorizer, CancellationToken ct)
    {
        if (!SourceData.IsValidSlug(sourceName)) return Results.BadRequest("Invalid source name.");
        var denied = UploadAuthorizer.ToFailure(await authorizer.AuthorizeAsync(request.Headers.Authorization.ToString(), sourceName, ct));
        if (denied != null) return denied;
        if (!long.TryParse(request.Query["accountKey"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var accountKey)
            || !DateOnly.TryParseExact(request.Query["date"], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            return Results.BadRequest("accountKey and date (yyyy-MM-dd) are required.");
        if (request.ContentLength > MaxStatementBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxStatementBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var content = buffer.ToArray();
        if (content.Length < 5 || !content.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return Results.BadRequest("The statement is not a PDF.");

        var outcome = await statements.StoreAsync(sourceName, accountKey, date, request.Query["name"].ToString(), content, ct);
        return Results.Ok(new { status = outcome switch { MoneyMoneyStatementOutcome.Stored => "stored", MoneyMoneyStatementOutcome.Duplicate => "duplicate", MoneyMoneyStatementOutcome.NoAccount => "skipped", _ => "skipped" } });
    }

    internal static async Task<IResult> HandleUploadAsync(
        string sourceName,
        HttpRequest request,
        MoneyMoneyImportService importer,
        UploadAuthorizer authorizer,
        ILogger logger,
        string stagingDir,
        CancellationToken ct)
    {
        if (!SourceData.IsValidSlug(sourceName))
            return Results.BadRequest($"Invalid source name '{sourceName}'. Use lowercase letters, digits, and hyphens; start with letter/digit.");

        // SQLite files can run to hundreds of MB, so lift Kestrel's ~30 MB default for this endpoint.
        // Raise it to a generous-but-finite ceiling rather than clearing it (null = unbounded): the
        // body is streamed to disk on the same volume as budgets.db before validation, so an
        // unbounded cap is a trivial disk-fill DoS for anyone holding the upload token.
        const long maxUploadBytes = 2L * 1024 * 1024 * 1024; // 2 GiB
        var sizeFeature = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature != null && !sizeFeature.IsReadOnly)
            sizeFeature.MaxRequestBodySize = maxUploadBytes;

        // Legacy global token (all sources) or this source's own token (this source only).
        var denied = UploadAuthorizer.ToFailure(await authorizer.AuthorizeAsync(request.Headers.Authorization.ToString(), sourceName, ct));
        if (denied != null) return denied;

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
}

