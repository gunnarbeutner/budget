using OpenBudget.Connectors;

namespace OpenBudget.Browser;

/// <summary>
/// The signed-in browser of a finished web login, kept for a connector's requests (<see cref="WebLoginSpec.KeepBrowser"/>).
/// Nobody can watch it any more (its web login has ended). Requests run one at a time; disposing it, or reaching
/// <paramref name="lifetime"/>, destroys the browser context. Only method, path and status are logged.
/// </summary>
public sealed class KeptBrowser : IBrowserFetcher, IAsyncDisposable
{
    private readonly IBrowserSession _browser;
    private readonly ILogger _logger;
#pragma warning disable CA2213, S2930 // left undisposed on purpose: a request still running may release or observe them after closing
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
#pragma warning restore CA2213, S2930
    private readonly ITimer _expiry;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;
    private Action<IReadOnlyList<CookieState>>? _cookieSink;
    private int _disposed;

    internal KeptBrowser(IBrowserSession browser, TimeSpan lifetime, TimeProvider clock, ILogger logger)
    {
        _browser = browser;
        _logger = logger;
        _expiry = clock.CreateTimer(Expire, null, lifetime, Timeout.InfiniteTimeSpan);
    }

    private void Expire(object? state)
    {
        if (!IsOpen) return;
        _logger.LogInformation("A kept browser reached its lifetime limit; closing it.");
        _ = DisposeAsync().AsTask();
    }

    public bool IsOpen => Volatile.Read(ref _disposed) == 0;

    /// <summary>Returns the browser's final cookie jar before it is destroyed, including when its lifetime expires.</summary>
    internal void SetCookieSink(Action<IReadOnlyList<CookieState>> sink) => _cookieSink = sink;

    public Task<BrowserFetchResponse> SendAsync(BrowserFetchRequest request, CancellationToken ct) =>
        RunAsync(async token =>
        {
            var response = await _browser.FetchAsync(request, token);
            _logger.LogDebug("Kept browser: {Method} {Path} answered {Status}.", request.Method, request.Url.AbsolutePath, response.Status);
            return response;
        }, ct);

    public Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct) =>
        RunAsync(token => _browser.ResolveRedirectAsync(url, token), ct);

    public Task NavigateAsync(Uri url, CancellationToken ct) =>
        RunAsync(async token => { await _browser.NavigateAsync(url, token); return true; }, ct);

    public Task<BrowserPage> PageAsync(CancellationToken ct) =>
        RunAsync(async _ => new BrowserPage(_browser.Url, await _browser.GetHtmlAsync()), ct);

    public Task<(bool Ok, string Reason)> RunAsync(string function, string argument, CancellationToken ct) =>
        RunAsync(async _ => { var r = await _browser.CheckAsync(function, argument); return (r.Ok, r.Reason); }, ct);

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (!IsOpen) throw SessionEnded();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        try
        {
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw SessionEnded();
        }
        try
        {
            if (!IsOpen) throw SessionEnded();
            return await action(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw SessionEnded();
        }
        catch (BrowserActionException ex)
        {
            throw new ConnectorException(ex.Message, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ConnectorException SessionEnded() => new("The bank's browser session has ended; the next sync signs in again.");

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _expiry.DisposeAsync();
        await _closed.CancelAsync();
        if (_cookieSink != null)
        {
            try
            {
                var cookies = await _browser.GetCookiesAsync().WaitAsync(TimeSpan.FromSeconds(5));
                _cookieSink(cookies);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not collect cookies from the kept browser."); }
        }
        try { await _browser.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Destroying a kept browser failed."); }
        _logger.LogInformation("Closed the kept browser.");
    }
}
