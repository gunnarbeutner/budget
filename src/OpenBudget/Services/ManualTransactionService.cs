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

/// <summary>Why an account's initial balance cannot be edited; <see cref="None"/> when it can.</summary>
public enum InitialBalanceLock { None, Bank, Portfolio, NotOwner }

/// <summary>An account's initial balance (cents), its stored current balance and whether the user may change it.</summary>
public sealed record InitialBalanceState(long Cents, decimal CurrentBalance, string Currency, InitialBalanceLock Lock);

/// <summary>A manual account an offsetting entry can be booked on.</summary>
public sealed record OffsetTarget(long AccountId, string Name, string Icon, string? Color);

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

    public ManualTransactionService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _system = system;
        _notifier = notifier;
        _origin = origin;
    }

    /// <summary>Whether the current user may enter bookings on the account (a manual account of a source they own).</summary>
    public async Task<bool> CanEnterAsync(long accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => new { a.SourceId, a.Provider }).FirstOrDefaultAsync(ct);
        return account != null && account.Provider == AccountProviders.Manual && (db.Scope == null || db.Scope.OwnsSource(account.SourceId));
    }

    /// <summary>
    /// The account's initial balance and whether the user may edit it. Every balance shown for an account is derived
    /// backwards from its stored current balance, so the initial balance only means something where that stored balance
    /// is the sum of the initial balance and the bookings: on manual accounts. A bank connection or upload overwrites the
    /// stored balance with the one the bank reports (<see cref="InitialBalanceLock.Bank"/>); depots and portfolio-file
    /// accounts are valued from holdings or the portfolio file (<see cref="InitialBalanceLock.Portfolio"/>).
    /// </summary>
    public async Task<InitialBalanceState?> GetInitialBalanceAsync(long accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account == null) return null;
        var lockReason = account.Portfolio == 1 || account.Type == 7 || account.Provider == AccountProviders.PortfolioPerformance
            ? InitialBalanceLock.Portfolio
            : account.Provider != AccountProviders.Manual ? InitialBalanceLock.Bank
            : db.Scope != null && !db.Scope.OwnsSource(account.SourceId) ? InitialBalanceLock.NotOwner
            : InitialBalanceLock.None;
        return new InitialBalanceState(account.InitialBalance, account.CurrentBalance ?? 0m,
            string.IsNullOrWhiteSpace(account.Currency) ? "EUR" : account.Currency, lockReason);
    }

    /// <summary>
    /// The initial balance (cents) that makes the account's balance at the end of <paramref name="day"/> equal
    /// <paramref name="balance"/>: the balance there, derived from the current balance and the booked flows like the
    /// transaction list's day balances, moved by the difference.
    /// </summary>
    public static long CalibratedInitialBalance(
        long initialCents, decimal currentBalance, IEnumerable<(DateTime Date, decimal Amount)> bookedFlows, DateOnly day, decimal balance,
        Func<DateTime, DateOnly> toLocalDay)
    {
        var atDay = new RunningBalance(currentBalance, bookedFlows, toLocalDay).AtEndOf(day);
        return initialCents + (long)Math.Round((balance - atDay) * 100m, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Sets a manual account's initial balance; its stored current balance moves by the same difference, so every
    /// balance derived from it (sidebar, day balances, charts, cash flow) follows. Only the source's owner may do this.
    /// </summary>
    public async Task SetInitialBalanceAsync(long accountId, long cents, CancellationToken ct = default)
    {
        var state = await GetInitialBalanceAsync(accountId, ct) ?? throw new AccessDeniedException("Account not found.");
        if (state.Lock == InitialBalanceLock.NotOwner) throw new AccessDeniedException("Only the owner edits this account.");
        if (state.Lock != InitialBalanceLock.None) throw new InvalidOperationException("The initial balance is editable only on manual accounts.");
        await using var db = await _system.CreateDbContextAsync(ct);
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var delta = cents - account.InitialBalance;
        if (delta == 0) return;
        account.InitialBalance = cents;
        account.Balances = BalanceBlob((account.CurrentBalance ?? 0m) + delta / 100m, state.Currency);
        await db.SaveChangesAsync(ct);
        Publish(account.SourceId);
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

    /// <summary>
    /// The manual accounts of the project (not closed) the user may enter bookings on, except
    /// <paramref name="exceptAccountId"/>: where an offsetting entry for a booking of that account can go. Sorted by name.
    /// </summary>
    public async Task<List<OffsetTarget>> GetOffsetTargetsAsync(int projectId, long? exceptAccountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accountIds = db.ProjectAccountAssignments.Where(a => a.ProjectId == projectId).Select(a => a.AccountId);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id) && a.Provider == AccountProviders.Manual && !a.Closed && a.Id != exceptAccountId)
            .ToListAsync(ct);
        return accounts
            .Where(a => db.Scope == null || db.Scope.OwnsSource(a.SourceId))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new OffsetTarget(a.Id, a.Name ?? "", a.Icon ?? a.KindIcon, a.Color))
            .ToList();
    }

    /// <summary>
    /// Books the opposite of <paramref name="transactionId"/> on the manual account <paramref name="accountId"/>: same
    /// date, negated amount in the same currency, same payee and purpose, and the same category (when the account is of
    /// another source, its category of the same name, if any). Split bookings are refused (their parts are offset one
    /// by one). Returns the new booking's id.
    /// </summary>
    public async Task<long> CreateOffsettingEntryAsync(long transactionId, long accountId, string? note, CancellationToken ct = default)
    {
        Transaction source;
        string? categoryName = null;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            source = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transactionId, ct)
                ?? throw new AccessDeniedException("Booking not found.");
            if (source.Splitted is SplitState.Parent)
                throw new InvalidOperationException("A split booking is offset part by part.");
            if (source.AccountId == accountId) throw new InvalidOperationException("The offsetting entry goes on another account.");
            if (source.CategoryId is { } cid)
                categoryName = await db.Categories.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct);
        }
        var (sourceId, _) = await RequireManualAccountAsync(accountId, ct);
        long? categoryId = null;
        if (source.CategoryId is { } sourceCategory && !string.IsNullOrEmpty(categoryName))
        {
            await using var sys = await _system.CreateDbContextAsync(ct);
            categoryId = source.SourceId == sourceId
                ? sourceCategory
                : await sys.Categories.AsNoTracking()
                    .Where(c => c.SourceId == sourceId && c.Name == categoryName)
                    .OrderBy(c => c.Id).Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
        }
        var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(source.Timestamp).UtcDateTime);
        return await AddAsync(accountId, new ManualTransactionInput(
            date, -source.Amount / 100m, source.Currency, source.Name, source.Purpose, categoryId, note), ct);
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

    /// <summary>Deletes a booking of a manual account.</summary>
    public async Task DeleteAsync(long transactionId, CancellationToken ct = default)
    {
        var (_, accountCurrency) = await RequireManualRowAsync(transactionId, ct);
        await using var db = await _system.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Transactions.FirstAsync(t => t.Id == transactionId, ct);
        if (row.Splitted is SplitState.Child || await db.Transactions.AnyAsync(t => t.SplitParentId == row.Id, ct))
            throw new InvalidOperationException("A split booking is deleted after removing the split.");
        db.Transactions.Remove(row);
        await AdjustBalanceAsync(db, row.AccountId!.Value, accountCurrency, row.Amount, row.Currency, 0, null, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Publish(row.SourceId);
    }

    private static void Apply(Transaction row, ManualTransactionInput input, string accountCurrency, bool amountEditable)
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
