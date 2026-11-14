using System.Net.Http.Headers;
using System.Text;

namespace OpenBudget.Connectors.FinTs;

/// <summary>
/// The registration of this application with the German banking industry (the FinTS product ID) and the
/// product version sent along with it. The ID is configuration, never code: environment variable
/// <see cref="ProductIdVariable"/>.
/// </summary>
public sealed record FinTsOptions(string? ProductId, string ProductVersion)
{
    public const string ProductIdVariable = "OPENBUDGET_FINTS_PRODUCT_ID";

    public const string NotConfiguredMessage =
        "FinTS product registration not configured. Set the environment variable " + ProductIdVariable + " to the registered FinTS product ID.";

    public const string SilentScaWindowVariable = "OPENBUDGET_FINTS_SILENT_SCA_DAYS";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProductId);

    /// <summary>
    /// How long after the last strong authentication a background run may still open a dialog when the bank's previous
    /// init asked for a TAN (PSD2 allows 90 days; the default keeps a margin).
    /// </summary>
    public TimeSpan SilentScaWindow { get; init; } = TimeSpan.FromDays(80);

    /// <summary>Reads the product ID from configuration; the product version is the application's version (at most five characters on the wire).</summary>
    public static FinTsOptions FromConfiguration(IConfiguration configuration)
    {
        var version = typeof(FinTsOptions).Assembly.GetName().Version;
        var text = version == null ? "1.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        var options = new FinTsOptions(configuration[ProductIdVariable]?.Trim(), text);
        if (int.TryParse(configuration[SilentScaWindowVariable], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days) && days > 0)
            options = options with { SilentScaWindow = TimeSpan.FromDays(days) };
        return options;
    }
}

/// <summary>What differs between banks: where they live and the few deviations from the standard.</summary>
public sealed record FinTsBankConfig(
    string ConnectorType,
    string DisplayName,
    string Description,
    string Blz,
    Uri Url,
    string? FallbackBic = null,
    bool UpdInventory = false,
    bool Statements = true,
    string? ProbeSecurityFunction = null);
// ProbeSecurityFunction: the security function signed with before the bank has named the user's methods (3920).
// Null means the one-step placeholder 999; ING refuses 999 with "Ungültiger Signaturaufbau" (9010) and no 3920, so it gets 900.
// Statements: false for banks whose documents are not reachable over FinTS (the connector then offers none).
// UpdInventory: the UPD is requested at every login (version 0) because the bank reports a per-account inventory
// marker in it (Atruvia banks); an unchanged marker lets a refresh skip fetching transactions.

/// <summary>Sends one FinTS message and returns the answer (both in the wire form, one char per byte).</summary>
public interface IFinTsTransport
{
    Task<string> ExchangeAsync(string message, CancellationToken ct);
}

/// <summary>FinTS over HTTPS: the message, Base64 encoded, as the POST body; the answer likewise.</summary>
public sealed class FinTsHttpTransport : IFinTsTransport
{
    private readonly IBankHttpClient _http;
    private readonly Uri _url;

    public FinTsHttpTransport(IBankHttpClient http, Uri url)
    {
        _http = http;
        _url = url;
    }

    public async Task<string> ExchangeAsync(string message, CancellationToken ct)
    {
        var body = Convert.ToBase64String(Encoding.Latin1.GetBytes(message));
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(body, Encoding.ASCII, new MediaTypeHeaderValue("text/plain")),
        };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendWithoutSessionAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            // the cause (DNS, TLS, connection reset, …) is named: it carries no credentials
            var cause = (ex.InnerException ?? ex).Message;
            throw new ConnectorException($"The bank server could not be reached ({cause.TrimEnd('.')}).", ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException("The bank server did not answer in time.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ConnectorException($"The bank server answered HTTP {(int)response.StatusCode}.");
            var text = await response.Content.ReadAsStringAsync(ct);
            try
            {
                return Encoding.Latin1.GetString(Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c)))));
            }
            catch (FormatException)
            {
                throw new FinTsProtocolException("The bank server's answer is not a FinTS message.");
            }
        }
    }
}

/// <summary>A failure the user should see; <see cref="CredentialsRejected"/> stops automatic retries.</summary>
public sealed class FinTsException : Exception
{
    public FinTsException()
    {
    }

    public FinTsException(string message) : base(message)
    {
    }

    public FinTsException(string message, bool credentialsRejected) : base(message)
    {
        CredentialsRejected = credentialsRejected;
    }

    public FinTsException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public bool CredentialsRejected { get; }
}

/// <summary>Where a client call can hand a question to the user and wait for the answer.</summary>
public interface IChallengeAsker
{
    Task<ChallengeAnswer> AskAsync(ConnectorChallenge challenge);
}

/// <summary>
/// Lets straight-line code ask the user questions in the middle: the body runs until it asks, the caller gets a
/// challenge step, and the next call with the answer resumes the body. Fits the connector contract (the app calls the
/// same method again with the answer) without turning every protocol sequence into a state machine.
/// </summary>
internal sealed class ChallengeCoroutine<T> : IChallengeAsker
{
    private readonly Func<IChallengeAsker, CancellationToken, Task<T>> _body;
    private Task<T>? _running;
    private TaskCompletionSource<ConnectorChallenge> _asked = NewSource<ConnectorChallenge>();
    private TaskCompletionSource<ChallengeAnswer>? _answer;
    private ConnectorChallenge? _open;

    private readonly bool _silent;

    /// <summary>A coroutine over <paramref name="body"/>.</summary>
    /// <param name="body">The protocol sequence; it asks through the <see cref="IChallengeAsker"/> it gets.</param>
    /// <param name="silent">A background run: asking ends the body with <see cref="SilentAbortException"/> instead.</param>
    public ChallengeCoroutine(Func<IChallengeAsker, CancellationToken, Task<T>> body, bool silent = false)
    {
        _body = body;
        _silent = silent;
    }

    private static TaskCompletionSource<TResult> NewSource<TResult>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ChallengeAnswer> AskAsync(ConnectorChallenge challenge)
    {
        if (_silent) throw new SilentAbortException(ConnectorStep.SilentReason(challenge));
        _open = challenge;
        // the answer can arrive (and ResumeAsync clear _answer) as soon as _asked is set, so return this source's task
        var answer = NewSource<ChallengeAnswer>();
        _answer = answer;
        _asked.TrySetResult(challenge);
        return answer.Task;
    }

    public async Task<ConnectorStep<T>> ResumeAsync(ChallengeAnswer? answer, CancellationToken ct)
    {
        if (_running == null)
        {
            _running = _body(this, ct);
        }
        else if (_answer != null && _open != null && answer != null)
        {
            var pending = _answer;
            _asked = NewSource<ConnectorChallenge>();
            _open = null;
            _answer = null;
            pending.SetResult(answer);
        }
        else if (_open != null)
        {
            return ConnectorStep.Ask<T>(_open);
        }

        var finished = await Task.WhenAny(_running, _asked.Task);
        if (finished == _asked.Task) return ConnectorStep.Ask<T>(await _asked.Task);
        try
        {
            return ConnectorStep.Done(await _running);
        }
        catch (SilentAbortException ex)
        {
            return ConnectorStep.NeedsUser<T>(ex.Message);
        }
        catch (FinTsException ex)
        {
            return ConnectorStep.Fail<T>(ex.Message, ex.CredentialsRejected);
        }
        catch (FinTsProtocolException ex)
        {
            return ConnectorStep.Fail<T>(ex.Message);
        }
        catch (ConnectorException ex)
        {
            return ConnectorStep.Fail<T>(ex.Message);
        }
    }
}
