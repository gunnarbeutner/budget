using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>
/// HTTP upload endpoint for MoneyMoney database snapshots. The request is the raw SQLite file as
/// the body (Content-Type application/octet-stream) with "Authorization: Bearer &lt;token&gt;".
/// </summary>
internal static class MoneyMoneyEndpoints
{
    public const string Route = "/api/import/moneymoney/{name}";

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

