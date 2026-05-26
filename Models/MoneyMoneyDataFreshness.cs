namespace OpenBudget.Models;

public class DataFreshness
{
    public int SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    /// <summary>When this source was last uploaded via the /api/moneymoney-db/{name} endpoint. Null when nothing has been imported yet.</summary>
    public DateTime? DbUploadedUtc { get; set; }
    public List<AccountFreshness> Accounts { get; set; } = new();

    /// <summary>True when any account has an unread error message — indicates a refresh failure
    /// that may be masking missing transactions.</summary>
    public IEnumerable<AccountFreshness> AccountsWithErrors =>
        Accounts.Where(a => !string.IsNullOrWhiteSpace(a.Error));
}

public class AccountFreshness
{
    public long AccountId { get; set; }
    public string? Name { get; set; }
    public long? RefreshTimestamp { get; set; }
    public string? Error { get; set; }

    public DateTime? LastRefreshUtc => RefreshTimestamp.HasValue && RefreshTimestamp.Value > 0
        ? DateTimeOffset.FromUnixTimeSeconds(RefreshTimestamp.Value).UtcDateTime
        : null;
}
