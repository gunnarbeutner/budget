namespace OpenBudget.Services;

/// <summary>
/// The shared path of a PortfolioPerformance file into a user's data: staged to a temp file under the upload directory,
/// checked to be a ".portfolio" ZIP, then imported by <see cref="PortfolioPerformanceImportService"/>. Used by the
/// browser upload and by linked cloud files.
/// </summary>
public static class PortfolioPerformanceUpload
{
    /// <summary>The message of the <see cref="InvalidDataException"/> thrown for content that is not a ".portfolio" file.</summary>
    public const string NotAPortfolioFile = "Uploaded payload is not a PortfolioPerformance (.portfolio) file.";

    /// <summary>Largest file a browser upload may send.</summary>
    public const long MaxBrowserUploadBytes = 256L * 1024 * 1024;

    /// <summary>The content of a file picked in the browser, capped at <see cref="MaxBrowserUploadBytes"/>.</summary>
#pragma warning disable S5693 // files with years of prices outgrow the 8 MB default; the cap stays finite
    public static Stream OpenBrowserFile(Microsoft.AspNetCore.Components.Forms.IBrowserFile file) => file.OpenReadStream(MaxBrowserUploadBytes);
#pragma warning restore S5693

    /// <summary>
    /// Copies <paramref name="content"/> to a temp file, verifies the ZIP magic and imports it. Throws
    /// <see cref="InvalidDataException"/> for a wrong format and <see cref="KeyNotFoundException"/> for an unknown user.
    /// </summary>
    public static async Task<PpImportResult> ImportAsync(
        PortfolioPerformanceImportService importer, string ownerUserId, Stream content, string stagingDir, CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDir);
        var tempPath = Path.Combine(stagingDir, $"{ownerUserId}-{Guid.NewGuid():N}.portfolio");
        try
        {
            await using (var tempStream = File.Create(tempPath))
            {
                await content.CopyToAsync(tempStream, ct);
            }

            // PortfolioPerformance ".portfolio" files are ZIP archives — magic "PK\x03\x04".
            await using (var checkStream = File.OpenRead(tempPath))
            {
                var magic = new byte[4];
                var read = await checkStream.ReadAtLeastAsync(magic, 4, throwOnEndOfStream: false, ct);
                if (read < 4 || magic[0] != 'P' || magic[1] != 'K' || magic[2] != 0x03 || magic[3] != 0x04)
                    throw new InvalidDataException(NotAPortfolioFile);
            }

            return await importer.ImportFromFileAsync(ownerUserId, tempPath, ct);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { /* best-effort cleanup */ }
            }
        }
    }
}
