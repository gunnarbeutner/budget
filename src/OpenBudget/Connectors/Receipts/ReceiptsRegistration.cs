using OpenBudget.Auth;
using OpenBudget.Assist;

namespace OpenBudget.Connectors.Receipts;

public static class ReceiptsRegistration
{
    /// <summary>
    /// Receipts: the store (with the optional AI for item categories), the mailbox reader as a background service and
    /// the owner's receipt service. Needs the OneDrive connection (<c>AddOneDrive</c>) and the Assist client (<c>AddAssist</c>).
    /// </summary>
    public static IServiceCollection AddReceipts(this IServiceCollection services)
    {
        services.AddSingleton<IReceiptItemClassifier, ReceiptItemAiClassifier>();
        services.AddSingleton(sp => new ReceiptStore(sp.GetRequiredService<IReceiptItemClassifier>()));
        services.AddSingleton(sp => new MailReceiptOptions
        {
            UnreadableDirectory = Path.Combine(OpenBudgetPaths.DataDir(sp.GetRequiredService<IHostEnvironment>().ContentRootPath), "receipts-unreadable"),
        });
        services.AddSingleton<MailReceiptSync>();
        services.AddHostedService(sp => sp.GetRequiredService<MailReceiptSync>());
        services.AddScoped<ReceiptService>();
        return services;
    }
}
