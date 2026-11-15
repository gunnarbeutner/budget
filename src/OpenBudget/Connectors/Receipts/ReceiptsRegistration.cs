using OpenBudget.Auth;
using OpenBudget.Assist;

namespace OpenBudget.Connectors.Receipts;

public static class ReceiptsRegistration
{
    /// <summary>
    /// Receipts: the store (with the optional AI for item categories, and product identities after each match), the
    /// mailbox reader and the product identity worker as background services and the owner's receipt service. Needs the
    /// OneDrive connection (<c>AddOneDrive</c>) and the Assist client (<c>AddAssist</c>).
    /// </summary>
    public static IServiceCollection AddReceipts(this IServiceCollection services)
    {
        services.AddSingleton<IReceiptItemClassifier, ReceiptItemAiClassifier>();
        services.AddSingleton<ProductIdentityResolver>();
        services.AddSingleton<ProductIdentityWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ProductIdentityWorker>());
        services.AddSingleton(sp => new ReceiptStore(sp.GetRequiredService<IReceiptItemClassifier>(), owner => sp.GetRequiredService<ProductIdentityWorker>().Request(owner)));
        services.AddSingleton(sp => new MailReceiptOptions
        {
            UnreadableDirectory = Path.Combine(OpenBudgetPaths.DataDir(sp.GetRequiredService<IHostEnvironment>().ContentRootPath), "receipts-unreadable"),
        });
        services.AddSingleton<MailReceiptSync>();
        services.AddHostedService(sp => sp.GetRequiredService<MailReceiptSync>());
        services.AddScoped<ReceiptService>();
        services.AddSingleton<PriceHistoryCache>();
        services.AddScoped<PriceHistoryService>();
        return services;
    }
}
