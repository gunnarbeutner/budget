using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using OpenBudget.Connectors;

namespace OpenBudget.Models;

/// <summary>How often a bank login is refreshed automatically (only while its owner has OpenBudget open).</summary>
public enum RefreshInterval
{
    EveryTwoHours = 0,
    FourTimesDaily = 1,
    Daily = 2,
    Weekly = 3,

    /// <summary>Never automatic, but included in "Sync all".</summary>
    ManualOnly = 4,

    /// <summary>Never automatic and excluded even from "Sync all"; only its own "Sync now" runs it.</summary>
    Never = 5,
}

/// <summary>
/// A user's login at a bank, served by the connector registered under <see cref="ConnectorType"/>.
/// Strictly personal: only the owner can see, edit, sync or delete it (enforced by a query filter
/// and the SaveChanges guard in <see cref="Services.BudgetDbContext"/>). Credentials and session
/// state are stored AES-GCM encrypted (see <see cref="SecretsProtector"/>).
/// </summary>
[Table("bank_logins")]
public class BankLogin
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string ConnectorType { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Encrypted JSON of the username and PIN/password.</summary>
    public string? EncryptedCredentials { get; set; }

    /// <summary>Encrypted JSON of the connector's <see cref="ConnectorSession"/>.</summary>
    public string? EncryptedSession { get; set; }

    public RefreshInterval RefreshInterval { get; set; } = RefreshInterval.EveryTwoHours;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the most recent sync attempt finished (successful or not); drives the automatic schedule.</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>Why the most recent attempt failed, or null when it succeeded.</summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>
    /// The bank rejected the stored credentials, or they can no longer be decrypted: no automatic
    /// syncs run (they would only risk a lockout) until the owner re-enters them.
    /// </summary>
    public bool CredentialsInvalid { get; set; }

    public ICollection<BankLoginAccount> Accounts { get; set; } = new List<BankLoginAccount>();
}

/// <summary>How often an account's statements are downloaded (when switched on).</summary>
public enum StatementsInterval
{
    /// <summary>At the start of a month; retried daily until a statement for the current month arrived.</summary>
    Monthly = 0,
    Weekly = 1,
    Daily = 2,
    ManualOnly = 3,
}

/// <summary>An account a bank login lists, and what it is linked to locally.</summary>
[Table("bank_login_accounts")]
public class BankLoginAccount
{
    [Key]
    public int Id { get; set; }

    public int BankLoginId { get; set; }

    public BankLogin BankLogin { get; set; } = null!;

    /// <summary>The bank's stable id for the account.</summary>
    [Required]
    [MaxLength(200)]
    public string RemoteId { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    public RemoteAccountType Type { get; set; }

    [MaxLength(50)]
    public string? Iban { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    /// <summary>The local <see cref="Account"/> this remote account feeds, or null while unlinked.</summary>
    public long? LinkedAccountId { get; set; }

    /// <summary>True when the local account was created for this remote account (rather than an existing one taken over).</summary>
    public bool CreatedByConnector { get; set; }

    /// <summary>
    /// Booking-date floor of the account's first sync after linking (the day of its newest booked
    /// transaction at that time). Later syncs never reach back before it, so rows imported earlier
    /// by MoneyMoney are not matched against again.
    /// </summary>
    public DateTime? SyncFloorUtc { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>
    /// When a linked account that MoneyMoney owned was switched to this connection (its provider became the connector's).
    /// Null while the link waits for the switch (the account is still MoneyMoney's), for accounts the connector created,
    /// and for links switched before this was recorded.
    /// </summary>
    public DateTime? SwitchedAt { get; set; }

    /// <summary>
    /// While the linked account is still MoneyMoney's: what the bank reported in the most recent sync (JSON of the
    /// connector's account refresh), kept only for the switch preview and applied by the switch. Never shown as rows.
    /// </summary>
    public string? PendingRefreshJson { get; set; }

    /// <summary>When <see cref="PendingRefreshJson"/> was fetched.</summary>
    public DateTime? PendingRefreshAt { get; set; }

    /// <summary>The booking-date floor <see cref="PendingRefreshJson"/> was fetched from (null = the bank's full history).</summary>
    public DateTime? PendingSinceUtc { get; set; }

    /// <summary>The bank no longer lists this account (closed): it cannot be linked or have statements switched on.</summary>
    public bool Closed { get; set; }

    /// <summary>Statement download for this account is switched on (default off; needs a link).</summary>
    public bool StatementsEnabled { get; set; }

    public StatementsInterval StatementsInterval { get; set; } = StatementsInterval.Monthly;

    /// <summary>When statements were last attempted for this account (drives the retry schedule).</summary>
    public DateTime? LastStatementsAttemptAt { get; set; }

    /// <summary>When a new statement last arrived for this account.</summary>
    public DateTime? LastStatementAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>Why the last sync could not read the account's standing orders and scheduled transfers (null: it could, or the bank offers none).</summary>
    [MaxLength(1000)]
    public string? ScheduledError { get; set; }
}

/// <summary>A downloaded statement document (indexed here, stored as a file on the data volume).</summary>
[Table("bank_statements")]
public class BankStatement
{
    [Key]
    public long Id { get; set; }

    /// <summary>The login that downloaded the document, or null for documents imported from MoneyMoney.</summary>
    public int? BankLoginId { get; set; }

    /// <summary>The login's (or the importing user's) owner; always sees the document, even without an account.</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>
    /// The account the document belongs to (viewers of a shared account may read it), or null for
    /// documents of the login as a whole (e.g. tax certificates), visible to the owner only.
    /// </summary>
    public long? AccountId { get; set; }

    /// <summary>
    /// The bank's identifier of the document; unique per login so each is downloaded once. Documents imported from
    /// MoneyMoney use "mm:" plus their <see cref="Sha256"/>.
    /// </summary>
    [Required]
    [MaxLength(300)]
    public string Identifier { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Path relative to the statements directory.</summary>
    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// The bank the document came from, as the importer named it (e.g. MoneyMoney's bank folder); set for imported
    /// documents whose account is not in OpenBudget, so they can be told apart until they are assigned to an account.
    /// </summary>
    [MaxLength(200)]
    public string? Institution { get; set; }

    public long SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A further bank identifier of a stored document: the bank offered the same file (same content, same date) under
/// another identifier, and only one copy is kept. The sync treats the identifier as known, so it is not downloaded again.
/// </summary>
[Table("bank_statement_aliases")]
public class BankStatementAlias
{
    [Key]
    public long Id { get; set; }

    /// <summary>The kept document.</summary>
    public long StatementId { get; set; }

    /// <summary>The login that offers the document under <see cref="Identifier"/>.</summary>
    public int BankLoginId { get; set; }

    [Required]
    [MaxLength(300)]
    public string Identifier { get; set; } = string.Empty;
}
