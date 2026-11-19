using OpenBudget.Auth;
using OpenBudget.Browser;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.FinTs;
using OpenBudget.Connectors.Openbank;
using OpenBudget.Connectors.PayPal;
using OpenBudget.Connectors.Ing;
using OpenBudget.Connectors.Kraken;
using OpenBudget.Connectors.LidlPlus;
using OpenBudget.Connectors.Scalable;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;

namespace OpenBudget.Connectors;

public static class BankConnectorsRegistration
{
    public const string StatementsDirVariable = "OPENBUDGET_STATEMENTS_DIR";

    private const string BrowserLauncherClientName = "browser-launcher";

    /// <summary>Offers the made-up <see cref="FakeBankConnector"/> (Development runs, test hosts).</summary>
    public static IServiceCollection AddFakeBank(this IServiceCollection services) =>
        services.AddSingleton(FakeBankConnector.Registration);

    /// <summary>Registers the connector framework: secrets, registry, sync engine and the per-user service.</summary>
    public static IServiceCollection AddBankConnectors(this IServiceCollection services, IHostEnvironment env, string contentRoot)
    {
        // Everything is resolved lazily from the final configuration (environment variables, test overrides).
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new SecretsProtector(sp.GetRequiredService<IConfiguration>()[SecretsProtector.KeyVariable]));
        if (env.IsDevelopment()) services.AddFakeBank();
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            // FinTS banks are always offered; without the product registration (OPENBUDGET_FINTS_PRODUCT_ID) a sync says so.
            var fints = FinTsOptions.FromConfiguration(configuration);
            List<ConnectorRegistration> registrations =
            [
                ScalableConnector.CreateRegistration(new ScalableOptions()),
                OpenbankConnector.CreateRegistration(new OpenbankOptions()),
                PayPalConnector.CreateRegistration(new PayPalOptions()),
                AmazonConnector.CreateRegistration(new AmazonOptions()),
                LidlPlusConnector.CreateRegistration(new LidlPlusOptions()),
                KrakenConnector.CreateRegistration(new KrakenOptions()),
                IngConnector.Registration(fints),
                BaaderConnector.Registration(fints),
                .. FinTsBanks.Registrations(fints),
            ];
            // Further connectors registered as services: the made-up bank of Development runs and test hosts.
            registrations.AddRange(sp.GetServices<ConnectorRegistration>());
            return new ConnectorRegistry(registrations);
        });
        services.AddSingleton(sp => new BankStorageOptions(
            sp.GetRequiredService<IConfiguration>()[StatementsDirVariable] is { Length: > 0 } custom
                ? custom
                : Path.Combine(OpenBudgetPaths.DataDir(contentRoot), "statements")));
        // Web logins: one Chrome per login on the browser sidecar.
        services.AddSingleton(new BrowserOptions());
        // Long-lived browser profiles of bank logins, encrypted on the data volume.
        services.AddSingleton(sp => new BrowserProfileStore(Path.Combine(OpenBudgetPaths.DataDir(contentRoot), "browser-profiles"),
            sp.GetRequiredService<SecretsProtector>(), sp.GetRequiredService<ILogger<BrowserProfileStore>>()));
        // The browser sidecar: web logins run there and are shown through its stream. Without it the launcher and the
        // screen resolve to null (GetService) and web logins fail with BrowserService.NotConfiguredMessage.
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(RemoteBrowserOptions.Section).Get<RemoteBrowserOptions>()
            ?? new RemoteBrowserOptions());
        services.AddHttpClient(BrowserLauncherClientName, c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddHttpClient(DesktopProxy.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IBrowserLauncher>(sp => sp.GetRequiredService<RemoteBrowserOptions>() is { IsEnabled: true } remote
            ? new BrowserLauncherClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(BrowserLauncherClientName), remote)
            : null!);
        services.AddSingleton<IRemoteScreenBackend>(sp => sp.GetService<IBrowserLauncher>() is { } launcher
            ? new SidecarScreenBackend(launcher, sp.GetRequiredService<IHttpClientFactory>().CreateClient(BrowserLauncherClientName),
                sp.GetRequiredService<RemoteBrowserOptions>())
            : null!);
        services.AddSingleton(sp => sp.GetService<IRemoteScreenBackend>() is { } backend
            ? new RemoteScreen(backend, sp.GetRequiredService<ILogger<RemoteScreen>>())
            : null!);
        services.AddSingleton(sp => new BrowserService(sp.GetRequiredService<ILogger<BrowserService>>(), sp.GetService<IBrowserLauncher>(),
            sp.GetRequiredService<BrowserOptions>(), sp.GetRequiredService<BrowserProfileStore>()));
        services.AddSingleton<IBrowserHost>(sp => sp.GetRequiredService<BrowserService>());
        services.AddSingleton(new WebLoginOptions());
        services.AddSingleton<IWebLoginService, WebLoginService>();
        services.AddSingleton(new BankSyncOptions());
        services.AddSingleton<BankStatementStore>();
        services.AddSingleton<BankTransactionStore>();
        services.AddSingleton<BankSyncRunner>();
        services.AddSingleton<BankSyncCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<BankSyncCoordinator>());
        services.AddSingleton(sp => new BackgroundSyncOptions
        {
            Enabled = !string.Equals(sp.GetRequiredService<IConfiguration>()[BackgroundSyncOptions.EnabledVariable], "false", StringComparison.OrdinalIgnoreCase),
        });
        services.AddSingleton<BackgroundSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundSyncService>());
        services.AddScoped<BankLoginService>();
        return services;
    }
}
