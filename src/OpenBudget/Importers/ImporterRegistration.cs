using OpenBudget.Importers.MoneyMoney;

namespace OpenBudget.Importers;

/// <summary>
/// Single entry point wiring the importer modules into the app. Each importer lives in its own
/// folder under Importers/ and is the only place allowed to know its source application's name.
/// </summary>
public static class ImporterRegistration
{
    public static IServiceCollection AddImporters(this IServiceCollection services)
    {
        services.AddSingleton<MoneyMoneyImportService>();
        return services;
    }

    public static IEndpointRouteBuilder MapImporterEndpoints(this IEndpointRouteBuilder app, string stagingDir)
    {
        MoneyMoneyEndpoints.Map(app, stagingDir);
        return app;
    }
}
