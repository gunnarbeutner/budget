namespace OpenBudget.Connectors;

/// <summary>How a bank asks the user to take part in a login or request.</summary>
public enum ChallengeKind
{
    /// <summary>Pick one of a list (for instance a TAN method).</summary>
    Choice,

    /// <summary>Approve in another app; the app polls the connector until the bank reports done.</summary>
    Approval,

    /// <summary>Type a code (TAN, SMS code, ...).</summary>
    Code,

    /// <summary>Look at an image (photoTAN, captcha) and type what it encodes.</summary>
    Image,

    /// <summary>Sign in on the bank's own web page, shown in a remote browser (see <see cref="WebLoginSpec"/>).</summary>
    WebLogin,
}

public sealed record ChallengeOption(string Id, string Label);

/// <summary>A request for user interaction. Created by connectors via the static factories.</summary>
public sealed class ConnectorChallenge
{
    private ConnectorChallenge(ChallengeKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    public ChallengeKind Kind { get; }

    /// <summary>Text shown to the user.</summary>
    public string Message { get; }

    /// <summary>Opaque bank-side correlation (for instance a TAN order reference) echoed back with the answer.</summary>
    public string? Reference { get; private init; }

    public IReadOnlyList<ChallengeOption> Options { get; private init; } = [];

    /// <summary>Expected code length, when known.</summary>
    public int? Length { get; private init; }

    public bool Numeric { get; private init; }

    public byte[]? Image { get; private init; }

    public string? ImageContentType { get; private init; }

    /// <summary>For approvals: how long to wait between polls; the app default applies when null.</summary>
    public TimeSpan? PollInterval { get; private init; }

    /// <summary>For web logins: where to sign in and what counts as done.</summary>
    public WebLoginSpec? WebLoginSpec { get; private init; }

    public static ConnectorChallenge WebLogin(string message, WebLoginSpec spec, string? reference = null) =>
        new(ChallengeKind.WebLogin, message) { WebLoginSpec = spec, Reference = reference };

    public static ConnectorChallenge Choice(string message, IReadOnlyList<ChallengeOption> options, string? reference = null) =>
        new(ChallengeKind.Choice, message) { Options = options, Reference = reference };

    public static ConnectorChallenge Approval(string message, TimeSpan? pollInterval = null, string? reference = null) =>
        new(ChallengeKind.Approval, message) { PollInterval = pollInterval, Reference = reference };

    public static ConnectorChallenge Code(string message, int? length = null, bool numeric = false, string? reference = null) =>
        new(ChallengeKind.Code, message) { Length = length, Numeric = numeric, Reference = reference };

    public static ConnectorChallenge ImageCode(string message, byte[] image, string contentType, int? length = null, bool numeric = false, string? reference = null) =>
        new(ChallengeKind.Image, message) { Image = image, ImageContentType = contentType, Length = length, Numeric = numeric, Reference = reference };

    /// <summary>True when <paramref name="other"/> is the same open question (used to keep an approval's timeout across polls).</summary>
    public bool IsSameQuestion(ConnectorChallenge other) =>
        Kind == other.Kind
        && string.Equals(Reference, other.Reference, StringComparison.Ordinal)
        && string.Equals(Message, other.Message, StringComparison.Ordinal);
}

/// <summary>
/// The user's reply to a <see cref="ConnectorChallenge"/> (a chosen option id, or the typed code).
/// For approvals <see cref="Value"/> is null: the connector is merely being asked again.
/// </summary>
public sealed record ChallengeAnswer(ConnectorChallenge Challenge, string? Value)
{
    /// <summary>
    /// After a web login whose spec has <see cref="WebLoginSpec.KeepBrowser"/>: the still signed-in browser. The sync
    /// closes it when the current step (login, refresh or statements) ends; the connector never disposes it.
    /// </summary>
    public IBrowserFetcher? Browser { get; init; }

    /// <summary>After a web login whose spec has <see cref="WebLoginSpec.CaptureRedirect"/>: the address the page was sent to (it carries a one-time code; never log it).</summary>
    public Uri? CallbackUrl { get; init; }

    /// <summary>After a web login whose spec has <see cref="WebLoginSpec.ReportFailure"/>: why the sign-in did not complete (null when it did).</summary>
    public string? Failure { get; init; }
}
