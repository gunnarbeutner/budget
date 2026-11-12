namespace OpenBudget.Connectors;

/// <summary>
/// One implementation per bank, modelled on MoneyMoney's WebBanking lifecycle: log in, list the
/// accounts, refresh each account, optionally fetch statements, log out. A connector only talks to
/// the bank — storage, deduplication, scheduling and UI belong to the app. A fresh instance is
/// created for every sync.
///
/// Any call may answer with a <see cref="ConnectorChallenge"/>; the app collects the user's answer
/// and calls the same method again with it (for approvals: again and again, every few seconds,
/// until the bank reports done or the wait times out).
/// </summary>
public interface IBankConnector
{
    /// <summary>The stable type string this connector is registered under (stored on bank logins).</summary>
    string ConnectorType { get; }

    /// <summary>Authenticates. <paramref name="storedSession"/> is what the previous <see cref="LogoutAsync"/> returned.</summary>
    Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct);

    Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct);

    /// <summary>
    /// Balance, bookings and (depots) positions of one account. <paramref name="since"/> is the first
    /// booking date wanted (inclusive); null means "as far back as you can".
    /// </summary>
    Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct);

    /// <summary>New statements for the given accounts; ones whose identifier is in <paramref name="knownIdentifiers"/> must not be returned again.</summary>
    Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct);

    /// <summary>Ends the session and returns the state to persist for next time (null: nothing to remember).</summary>
    Task<ConnectorSession?> LogoutAsync(CancellationToken ct);
}

/// <summary>
/// A connector that hands over statements in batches (a large first download): after a fetch, <see cref="MoreStatements"/>
/// says whether calling again gets the next batch, and <see cref="StatementsTotal"/> how many new documents there are in all.
/// The sync stores each batch before asking for the next, so an interrupted sync keeps what it got.
/// </summary>
public interface IBatchedStatements
{
    bool MoreStatements { get; }

    int? StatementsTotal { get; }
}

/// <summary>
/// What the app already holds for a login's linked accounts, handed to a connector after the login and before the account
/// listing, so it can tell whether anything new happened. <see cref="LastSuccessUtc"/> is the oldest last successful
/// refresh among the linked accounts; null when one of them was never refreshed, still waits for its switch from
/// MoneyMoney, or none is linked. <see cref="KnownTransactionIds"/> are the bank transaction ids stored for those
/// accounts from a few days before that point on; <see cref="PendingCount"/> counts their stored pending rows.
/// </summary>
public sealed record SyncHistory(DateTime? LastSuccessUtc, IReadOnlySet<string> KnownTransactionIds, int PendingCount);

/// <summary>A connector that uses the <see cref="SyncHistory"/> of its login (to skip a slow download when nothing changed).</summary>
public interface ISyncHistoryConsumer
{
    /// <summary>How many days before <see cref="SyncHistory.LastSuccessUtc"/> the known ids reach back.</summary>
    int HistoryOverlapDays { get; }

    void UseSyncHistory(SyncHistory history);
}
