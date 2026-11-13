namespace OpenBudget.Connectors.FakeBank;

/// <summary>
/// Behaviour switches of the fake bank, read from the login's username: everything after a '+' is a
/// flag, for instance "demo+code+pending". The first token is just a label. Unknown flags are
/// ignored. A PIN of "wrong" is always rejected.
/// </summary>
public sealed class FakeBankSettings
{
    public const string RejectedSecret = "wrong";

    /// <summary>Ask for a code at login (answer 123456).</summary>
    public bool Code { get; private init; }

    /// <summary>Ask which TAN method to use at login (any option is accepted).</summary>
    public bool Choice { get; private init; }

    /// <summary>Ask for approval in an app at login; completes after <see cref="ApprovalPolls"/> polls.</summary>
    public bool Approval { get; private init; }

    public int ApprovalPolls { get; private init; } = 2;

    /// <summary>Ask for an image code at login (answer 4711).</summary>
    public bool Image { get; private init; }

    /// <summary>Sign in on a web page in the remote browser (start and success URLs: see <see cref="FakeBankConnector"/>).</summary>
    public bool Web { get; private init; }

    /// <summary>Reject the credentials at login.</summary>
    public bool LoginFail { get; private init; }

    /// <summary>Fail refreshing the first account.</summary>
    public bool SyncError { get; private init; }

    /// <summary>Ask for a code while refreshing the first account (answer 123456).</summary>
    public bool RefreshCode { get; private init; }

    /// <summary>Ask for a code while fetching statements (answer 123456).</summary>
    public bool StatementCode { get; private init; }

    /// <summary>Report a pending booking on the first sync that becomes booked afterwards.</summary>
    public bool Pending { get; private init; }

    /// <summary>Two identical bookings on one day.</summary>
    public bool Duplicates { get; private init; }

    /// <summary>Omit bank transaction ids.</summary>
    public bool NoIds { get; private init; }

    /// <summary>Ask for a code at login only while the stored session holds no earlier sign-in (a remembered session).</summary>
    public bool Session { get; private init; }

    /// <summary>Offer statements (tiny PDFs).</summary>
    public bool Statements { get; private init; }

    public static FakeBankSettings Parse(string? username)
    {
        var flags = (username ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(f => f.ToUpperInvariant())
            .ToList();

        bool Has(string name) => flags.Contains(name, StringComparer.Ordinal);

        var approve = flags.FirstOrDefault(f => f.StartsWith("APPROVE", StringComparison.Ordinal));
        var polls = 2;
        if (approve != null && approve.Length > "APPROVE".Length
            && int.TryParse(approve["APPROVE".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
            polls = n;

        return new FakeBankSettings
        {
            Code = Has("CODE"),
            Choice = Has("CHOICE"),
            Web = Has("WEB"),
            Approval = approve != null,
            ApprovalPolls = polls,
            Image = Has("IMAGE"),
            LoginFail = Has("LOGINFAIL"),
            SyncError = Has("SYNCERROR"),
            RefreshCode = Has("REFRESHCODE"),
            StatementCode = Has("STATEMENTCODE"),
            Pending = Has("PENDING"),
            Duplicates = Has("DUPS"),
            NoIds = Has("NOID"),
            Statements = Has("STATEMENTS"),
            Session = Has("SESSION"),
        };
    }
}
