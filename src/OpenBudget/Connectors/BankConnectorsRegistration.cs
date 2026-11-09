using OpenBudget.Auth;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;

namespace OpenBudget.Connectors;

public static class BankConnectorsRegistration
{
    public const string EnableFakeBankVariable = "OPENBUDGET_ENABLE_FAKE_BANK";
    public const string StatementsDirVariable = "OPENBUDGET_STATEMENTS_DIR";

    /// <summary>Registers the connector framework: secrets, registry, sync engine and the per-user service.</summary>
    public static IServiceCollection AddBankConnectors(this IServiceCollection services, IConfiguration config, IHostEnvironment env, string contentRoot)
    {
        var fakeEnabled = env.IsEnvironment("Test")
            || string.Equals(config[EnableFakeBankVariable], "true", StringComparison.OrdinalIgnoreCase);
        var statementsDir = config[StatementsDirVariable] is { Length: > 0 } custom
            ? custom
            : Path.Combine(OpenBudgetPaths.DataDir(contentRoot), "statements");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SecretsProtector(config[SecretsProtector.KeyVariable]));
        if (fakeEnabled) services.AddSingleton(FakeBankConnector.Registration);
        services.AddSingleton<ConnectorRegistry>();
        services.AddSingleton(new BankStorageOptions(statementsDir));
        services.AddSingleton(new BankSyncOptions());
        services.AddSingleton<BankStatementStore>();
        services.AddSingleton<BankTransactionStore>();
        services.AddSingleton<BankSyncRunner>();
        services.AddSingleton<BankSyncCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<BankSyncCoordinator>());
        services.AddScoped<BankLoginService>();
        return services;
    }
}
