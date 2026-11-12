using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A booking as the manual entry form edits it. <see cref="Currency"/> null means the account's currency.</summary>
public sealed record ManualTransactionInput(
    DateOnly Date,
    decimal Amount,
    string? Currency,
    string? Name,
    string? Purpose,
    long? CategoryId,
    string? Note,
    bool Booked = true);

/// <summary>
/// Adds, edits and deletes bookings of manual accounts (<see cref="AccountProviders.Manual"/>): nobody refreshes such an
/// account, so all its fields - also those of the history MoneyMoney delivered (amount, date, payee, purpose) - are the
/// user's. Only the source's owner may do this. Rows entered by hand have no bank identity (<see cref="Transaction.ExternalId"/>
/// null, like MoneyMoney's rows, so a bank connection linked later can take them over), are never marked new, and get
/// ids in the connector half of the source's id space so MoneyMoney's rowids never collide with them.
///
/// The account's stored balance follows every change: it moves by the difference of the amounts in the account's
/// currency (rows in another currency leave it alone). Split bookings keep their amount (their parts must add up);
/// a split booking or a split part is deleted by removing the split first.
/// </summary>
public sealed class ManualTransactionService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _time;

    public ManualTransactionService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, ChangeNotificationService notifier, ChangeOrigin origin,
        TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _system = system;
        _notifier = notifier;
        _origin = origin;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether the current user may enter bookings on the account (a manual account of a source they own).</summary>
    public async Task<bool> CanEnterAsync(long accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => new { a.SourceId, a.Provider }).FirstOrDefaultAsync(ct);
        return account != null && account.Provider == AccountProviders.Manual && (db.Scope == null || db.Scope.OwnsSource(account.SourceId));
    }

    /// <summary>Adds a booking to a manual account; returns its id.</summary>
    public async Task<long> AddAsync(long accountId, ManualTransactionInput input, CancellationToken ct = default)
    {
        var (sourceId, accountCurrency) = await RequireManualAccountAsync(accountId, ct);
        await using var db = await _system.CreateDbContextAsync(ct);
        await ValidateCategoryAsync(db, sourceId, input.CategoryId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var floor = SourceScope.Namespace(sourceId, BankTransactionStore.ConnectorRowBase);
        var next = SourceScope.Namespace(sourceId + 1, 0);
        var maxId = await db.Transactions.Where(t => t.Id >= floor && t.Id < next).MaxAsync(t => (long?)t.Id, ct) ?? floor;
        var row = new Transaction
        {
            Id = maxId + 1,
            SourceId = sourceId,
            AccountId = accountId,
            Splitted = SplitState.Normal,
            IsNew = false,
        };
        Apply(row, input, accountCurrency, amountEditable: true);
        db.Transactions.Add(row);
        if (row.CategoryId == null)
            await CategoryRuleEngine.ApplyToNewAsync(db, accountId, [row], ct);
        await AdjustBalanceAsync(db, accountId, accountCurrency, 0, null, row.Amount, row.Currency, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Publish(sourceId);
        return row.Id;
    }

    /// <summary>Changes a booking of a manual account (also one MoneyMoney delivered).</summary>
    public async Task UpdateAsync(long transactionId, ManualTransactionInput input, CancellationToken ct = default)
    {
        var (row0, accountCurrency) = await RequireManualRowAsync(transactionId, ct);
        await using var db = await _system.CreateDbContextAsync(ct);
        await ValidateCategoryAsync(db, row0.SourceId, input.CategoryId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Transactions.FirstAsync(t => t.Id == transactionId, ct);
        var (oldAmount, oldCurrency) = (row.Amount, row.Currency);
        var isSplit = row.Splitted is SplitState.Parent or SplitState.Child or SplitState.Remainder;
        if (isSplit && BankTransactionStore.ToCents(input.Amount) != row.Amount)
            throw new InvalidOperationException("The amount of a split booking cannot be changed; remove the split first.");
        Apply(row, input, accountCurrency, amountEditable: !isSplit);
        await AdjustBalanceAsync(db, row.AccountId!.Value, accountCurrency, oldAmount, oldCurrency, row.Amount, row.Currency, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Publish(row.SourceId);
    }

    /// <summary>Deletes a booking of a manual account. The other half of a linked transfer becomes a one-sided transfer.</summary>
    public async Task DeleteAsync(long transactionId, CancellationToken ct = default)
    {
        var (_, accountCurrency) = await RequireManualRowAsync(transactionId, ct);
        await using var db = await _system.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Transactions.FirstAsync(t => t.Id == transactionId, ct);
        if (row.Splitted is SplitState.Child || await db.Transactions.AnyAsync(t => t.SplitParentId == row.Id, ct))
            throw new InvalidOperationException("A split booking is deleted after removing the split.");
        await db.Transactions.Where(t => t.TransferPeerId == row.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.TransferPeerId, (long?)null), ct);
        db.Transactions.Remove(row);
        await AdjustBalanceAsync(db, row.AccountId!.Value, accountCurrency, row.Amount, row.Currency, 0, null, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Publish(row.SourceId);
    }

    private void Apply(Transaction row, ManualTransactionInput input, string accountCurrency, bool amountEditable)
    {
        var currency = string.IsNullOrWhiteSpace(input.Currency) ? accountCurrency : input.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter)) throw new ArgumentException("The currency is a three-letter code.", nameof(input));
        if (amountEditable) row.Amount = BankTransactionStore.ToCents(input.Amount);
        row.Currency = currency;
        row.Timestamp = BankTransactionStore.ToTimestamp(input.Date);
        row.Name = Clean(input.Name);
        row.Purpose = Clean(input.Purpose);
        row.Note = Clean(input.Note);
        row.Booked = input.Booked;
        row.IsNew = false;
        if (row.CategoryId != input.CategoryId)
        {
            row.CategoryId = input.CategoryId;
            row.CategorySetBy = TransactionCategorySource.Manual;
            row.RuleId = null;
            row.CategorySetAt = input.CategoryId == null ? null : _time.GetUtcNow().ToUnixTimeSeconds();
        }
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Moves the stored balance by the change of the booking's amount, counting only amounts in the account's currency.</summary>
    private static async Task AdjustBalanceAsync(
        BudgetDbContext db, long accountId, string accountCurrency, long oldCents, string? oldCurrency, long newCents, string? newCurrency, CancellationToken ct)
    {
        static long InAccount(long cents, string? currency, string accountCurrency) =>
            currency == null || string.Equals(currency, accountCurrency, StringComparison.OrdinalIgnoreCase) ? cents : 0;
        var delta = InAccount(newCents, newCurrency, accountCurrency) - InAccount(oldCents, oldCurrency, accountCurrency);
        if (delta == 0) return;
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var balance = account.CurrentBalance ?? 0m;
        account.Balances = BalanceBlob(balance + delta / 100m, accountCurrency);
    }

    /// <summary>The packed balance format the accounts table stores ("11:&lt;amount&gt;,3:&lt;currency&gt;,").</summary>
    public static string BalanceBlob(decimal balance, string currency) =>
        $"11:{balance.ToString("F6", CultureInfo.InvariantCulture)},3:{currency},";

    private async Task<(int SourceId, string Currency)> RequireManualAccountAsync(long accountId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new AccessDeniedException("Account not found.");
        db.Scope?.RequireSourceOwner(account.SourceId);
        if (account.Provider != AccountProviders.Manual)
            throw new InvalidOperationException("Bookings are entered by hand only on manual accounts.");
        return (account.SourceId, string.IsNullOrWhiteSpace(account.Currency) ? "EUR" : account.Currency);
    }

    private async Task<(Transaction Row, string Currency)> RequireManualRowAsync(long transactionId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transactionId, ct)
            ?? throw new AccessDeniedException("Booking not found.");
        if (row.AccountId is not { } accountId) throw new InvalidOperationException("Bookings are entered by hand only on manual accounts.");
        var (_, currency) = await RequireManualAccountAsync(accountId, ct);
        return (row, currency);
    }

    private static async Task ValidateCategoryAsync(BudgetDbContext db, int sourceId, long? categoryId, CancellationToken ct)
    {
        if (categoryId is not { } id) return;
        var category = await db.Categories.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.SourceId, c.Name }).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Unknown category.");
        if (category.SourceId != sourceId) throw new InvalidOperationException("The category belongs to another source.");
        if (string.IsNullOrEmpty(category.Name)) throw new InvalidOperationException("That category cannot be assigned.");
    }

    private void Publish(int sourceId) => _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId), _origin.Id);
}
