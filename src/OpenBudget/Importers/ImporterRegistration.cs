using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Services;

namespace OpenBudget.Importers;

/// <summary>
/// Single entry point wiring the importer modules into the app. Each importer lives in its own
/// folder under Importers/ and is the only place allowed to know its source application's name.
/// </summary>
public static class ImporterRegistration
{
    public static IServiceCollection AddImporters(this IServiceCollection services)
    {
        // Uploads are token-authenticated, not user-authenticated, so the importer uses the explicit
        // unrestricted factory (a singleton can't consume the per-user scoped one anyway).
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<MoneyMoneyImportService>(
            sp, sp.GetRequiredService<SystemDbContextFactory>()));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<MoneyMoneyStatementImport>(
            sp, sp.GetRequiredService<SystemDbContextFactory>()));
        services.AddSingleton<MoneyMoneyBrowserImport>();
        return services;
    }

    public static IEndpointRouteBuilder MapImporterEndpoints(this IEndpointRouteBuilder app, string stagingDir)
    {
        MoneyMoneyEndpoints.Map(app, stagingDir);
        MoneyMoneyUploadEndpoints.Map(app);
        return app;
    }
}
