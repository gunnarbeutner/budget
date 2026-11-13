using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenBudget.Assist;

public static class AssistRegistration
{
    /// <summary>
    /// The AI suggestion services. The API key comes from the environment only; the HTTP client logs no headers, so the
    /// Authorization header never reaches a log.
    /// </summary>
    public static IServiceCollection AddAssist(this IServiceCollection services, IConfiguration configuration)
    {
        var options = AssistOptions.From(name => configuration[name]);
        services.AddSingleton(options);
        services.AddHttpClient(AssistOptions.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(_ => true);
        services.TryAddSingleton<IAssistClient, OpenAiAssistClient>();
        services.TryAddSingleton<IDecisionsClient, OpenAiDecisionsClient>();
        services.AddScoped<AssistSettingsService>();
        services.AddScoped<CategorySuggestionService>();
        services.AddScoped<BudgetSuggestionService>();
        return services;
    }
}
