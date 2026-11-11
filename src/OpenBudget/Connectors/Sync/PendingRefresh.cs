using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

/// <summary>
/// The bank's report for a linked account that MoneyMoney still owns (<see cref="BankLoginAccount.PendingRefreshJson"/>):
/// a sync stores it instead of writing rows, the switch preview compares it with the account's rows and the switch applies it.
/// </summary>
public static class PendingRefresh
{
    /// <summary>How far before MoneyMoney's newest booking the bank's rows are fetched, so the overlap can be matched.</summary>
    public const int OverlapDays = 30;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    public static string Serialize(AccountRefresh refresh) => JsonSerializer.Serialize(refresh, Json);

    public static AccountRefresh? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<AccountRefresh>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The day of the account's newest booked row (where its MoneyMoney history ends), or null without rows.</summary>
    public static async Task<DateOnly?> NewestBookedDayAsync(BudgetDbContext db, long accountId, CancellationToken ct)
    {
        var newest = await db.Transactions.Where(t => t.AccountId == accountId && t.Booked).MaxAsync(t => (long?)t.Timestamp, ct);
        return newest is { } n ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime) : null;
    }

    /// <summary>The booking-date floor of a pending fetch: <see cref="OverlapDays"/> before the newest booked row; null = everything.</summary>
    public static async Task<DateOnly?> SinceAsync(BudgetDbContext db, long accountId, CancellationToken ct) =>
        (await NewestBookedDayAsync(db, accountId, ct))?.AddDays(-OverlapDays);
}
