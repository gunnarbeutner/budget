using OpenBudget.Auth;
using OpenBudget.Browser;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Scalable;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;

namespace OpenBudget.Connectors;

public static class BankConnectorsRegistration
{
    public const string EnableFakeBankVariable = "OPENBUDGET_ENABLE_FAKE_BANK";
    public const string StatementsDirVariable = "OPENBUDGET_STATEMENTS_DIR";

    /// <summary>Registers the connector framework: secrets, registry, sync engine and the per-user service.</summary>
    public static IServiceCollection AddBankConnectors(this IServiceCollection services, IHostEnvironment env, string contentRoot)
    {
        // Everything is resolved lazily from the final configuration (environment variables, test overrides).
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new SecretsProtector(sp.GetRequiredService<IConfiguration>()[SecretsProtector.KeyVariable]));
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var fakeEnabled = env.IsEnvironment("Test")
                || string.Equals(configuration[EnableFakeBankVariable], "true", StringComparison.OrdinalIgnoreCase);
            var registrations = new List<ConnectorRegistration> { ScalableConnector.CreateRegistration(new ScalableOptions()) };
            if (fakeEnabled) registrations.Add(FakeBankConnector.Registration);
            return new ConnectorRegistry(registrations);
        });
        services.AddSingleton(sp => new BankStorageOptions(
            sp.GetRequiredService<IConfiguration>()[StatementsDirVariable] is { Length: > 0 } custom
                ? custom
                : Path.Combine(OpenBudgetPaths.DataDir(contentRoot), "statements")));
        // Web logins: one lazily started Chromium (when installed), an isolated context per login.
        services.AddSingleton(new BrowserOptions());
        services.AddSingleton<BrowserService>();
        services.AddSingleton<IBrowserHost>(sp => sp.GetRequiredService<BrowserService>());
        services.AddSingleton(new WebLoginOptions());
        services.AddSingleton<IWebLoginService, WebLoginService>();
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
