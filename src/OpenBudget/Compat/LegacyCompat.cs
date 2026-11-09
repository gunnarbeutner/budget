namespace OpenBudget.Compat;

/// <summary>
/// COMPATIBILITY SHIMS for deployments and the already-installed upload tool that predate the
/// OpenBudget rebrand (they still use the MoneyMoneyBudget names). Everything legacy-named that
/// lives outside the importer module is gathered here so it can be deleted in one go once the
/// old exporter is retired.
/// </summary>
internal static class LegacyCompat
{
    public const string UploadTokenVariable = "OPENBUDGET_UPLOAD_TOKEN";

    /// <summary>Deprecated name of the upload token variable (old exporter-era deployments).</summary>
    public const string LegacyUploadTokenVariable = "MONEYMONEY_DB_UPLOAD_TOKEN";

    /// <summary>Deprecated route the old exporter uses to delete a source.</summary>
    public const string LegacyDeleteSourceRoute = "/api/moneymoney-sources/{name}";

    private static int _warned;

    /// <summary>
    /// Resolves the expected upload token: <c>OPENBUDGET_UPLOAD_TOKEN</c>, else the deprecated
    /// <c>MONEYMONEY_DB_UPLOAD_TOKEN</c> (logging a one-time deprecation warning).
    /// </summary>
    public static string? ResolveUploadToken(Func<string, string?> getEnv, Microsoft.Extensions.Logging.ILogger? logger)
    {
        var token = getEnv(UploadTokenVariable);
        if (!string.IsNullOrEmpty(token)) return token;

        var legacy = getEnv(LegacyUploadTokenVariable);
        if (string.IsNullOrEmpty(legacy)) return null;

        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger?.LogWarning(
                "{Legacy} is deprecated; set {Current} instead. Falling back to the legacy variable.",
                LegacyUploadTokenVariable, UploadTokenVariable);
        }
        return legacy;
    }

    public static string? ResolveUploadToken(Microsoft.Extensions.Logging.ILogger? logger) =>
        ResolveUploadToken(Environment.GetEnvironmentVariable, logger);
}
