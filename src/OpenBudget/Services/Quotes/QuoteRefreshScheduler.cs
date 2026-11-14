namespace OpenBudget.Services.Quotes;

public sealed class QuoteRefreshOptions
{
    public const string EnabledVariable = "OPENBUDGET_QUOTE_REFRESH";

    /// <summary>False switches the scheduled refresh off; the manual one keeps working.</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(3);
}

/// <summary>Refreshes every user's quotes (<see cref="QuoteRefreshService"/>) shortly after start and then every <see cref="QuoteRefreshOptions.Interval"/>.</summary>
public sealed class QuoteRefreshScheduler : BackgroundService
{
    private readonly QuoteRefreshService _refresh;
    private readonly QuoteRefreshOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<QuoteRefreshScheduler> _logger;

    public QuoteRefreshScheduler(QuoteRefreshService refresh, QuoteRefreshOptions options, TimeProvider clock, ILogger<QuoteRefreshScheduler> logger)
    {
        _refresh = refresh;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), _clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var owner in await _refresh.OwnersAsync(stoppingToken))
                {
                    try
                    {
                        await _refresh.RefreshAsync(owner, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Refreshing the quotes of user {OwnerUserId} failed.", owner);
                    }
                }
                await Task.Delay(_options.Interval, _clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
