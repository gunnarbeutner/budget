namespace OpenBudget.Models;

public class DataFreshness
{
    public string OwnerUserId { get; set; } = string.Empty;
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
