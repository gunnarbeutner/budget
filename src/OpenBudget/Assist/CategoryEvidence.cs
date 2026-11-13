using System.Globalization;
using System.Text.RegularExpressions;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>
/// Builds what the matching and the AI see of a project's bookings: the matching keys of a booking (payee, words,
/// IBAN, mandate, creditor, amount, date), the booking as an <see cref="AssistBookingSource"/> with its own account's
/// stable token and role, how often its payee and amount recur, and the prompt item with its neighbours and how they
/// relate to it. Shared by the suggestion service and the offline evaluation so both send the same.
/// </summary>
public sealed partial class CategoryEvidence
{
    /// <summary>Contract neighbours (same mandate, payee and amount, creditor) put before the text neighbours.</summary>
    public const int MaxKeyNeighbours = 2;

    /// <summary>Text neighbours (kNN) in a prompt item.</summary>
    public const int MaxTextNeighbours = 3;

    private readonly IReadOnlyDictionary<long, Transaction> _rows;
    private readonly Dictionary<long, (string Token, string Kind, string Role)> _accounts = [];
    private readonly Dictionary<string, List<Transaction>> _byPayee = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<long, PromptCategory> _categories;

    /// <summary>Indexes the bookings by payee and gives the accounts their tokens.</summary>
    /// <param name="rows">The project's bookings (categorized or not).</param>
    /// <param name="accounts">The project's accounts; their tokens (OWN_1, …) follow the account id order.</param>
    /// <param name="categories">The categories by id (for the neighbours' handles and paths).</param>
    public CategoryEvidence(IReadOnlyDictionary<long, Transaction> rows, IEnumerable<Account> accounts, IReadOnlyDictionary<long, PromptCategory> categories)
    {
        _rows = rows;
        _categories = categories;
        var i = 0;
        foreach (var a in accounts.OrderBy(a => a.Id))
            _accounts[a.Id] = ("OWN_" + (++i).ToString(CultureInfo.InvariantCulture), a.KindLabel, AccountRole(a));
        foreach (var t in rows.Values.Where(t => t.Amount != 0))
        {
            var key = CategoryMatching.PayeeKey(t.Name);
            if (key.Length == 0) continue;
            if (!_byPayee.TryGetValue(key, out var list)) _byPayee[key] = list = [];
            list.Add(t);
        }
    }

    /// <summary>The account's role in words, from its type and connector ("PayPal wallet", "credit card", "checking account").</summary>
    public static string AccountRole(Account a)
    {
        if (a.Provider == "paypal" || (a.Name?.Contains("paypal", StringComparison.OrdinalIgnoreCase) ?? false)) return "PayPal wallet";
        if (a.Provider is "kraken" or "scalable" && a.Type != 3) return "broker cash account";
        return a.Type switch
        {
            1 => "checking account",
            2 => "savings account",
            3 => "credit card",
            4 => "loan",
            5 => "manually kept account",
            6 => "cash",
            7 => "securities account",
            _ => "other account",
        };
    }

    /// <summary>The stable token of an own account ("OWN_2"), or null.</summary>
    public string? AccountToken(long? accountId) => accountId is { } id && _accounts.TryGetValue(id, out var a) ? a.Token : null;

    /// <summary>The mandate reference of a booking (the stored one, else the MREF tag of the purpose), normalised.</summary>
    public static string? Mandate(Transaction t) => CategoryMatching.SepaKey(t.MandateReference) ?? CategoryMatching.SepaKey(SepaPurpose.Get(t.Purpose, "MREF"));

    /// <summary>The creditor id of a booking (the stored one, else the CRED tag of the purpose), normalised.</summary>
    public static string? Creditor(Transaction t) => CategoryMatching.SepaKey(t.CreditorId) ?? CategoryMatching.SepaKey(SepaPurpose.Get(t.Purpose, "CRED"));

    public static HistoryBooking History(Transaction t, long categoryId) =>
        new(t.Id, categoryId, CategoryMatching.IbanKey(t.CounterpartyIban), CategoryMatching.PayeeKey(t.Name), CategoryMatching.Tokens(t.Name, PurposeOnly(t.Purpose)), t.Amount)
        {
            Mandate = Mandate(t),
            Creditor = Creditor(t),
            Date = DateOnly.FromDateTime(t.Date),
        };

    public static TargetBooking Target(Transaction t) =>
        new(t.Id, CategoryMatching.IbanKey(t.CounterpartyIban), CategoryMatching.PayeeKey(t.Name), CategoryMatching.Tokens(t.Name, PurposeOnly(t.Purpose)), t.Amount)
        {
            Mandate = Mandate(t),
            Creditor = Creditor(t),
            Date = DateOnly.FromDateTime(t.Date),
        };

    private static string PurposeOnly(string? purpose) => SepaPurpose.Get(purpose, "SVWZ") ?? purpose ?? "";

    /// <summary>How often the booking's payee (and amount) recur among the other bookings, in words, or null.</summary>
    public string? Recurrence(Transaction t)
    {
        var key = CategoryMatching.PayeeKey(t.Name);
        if (key.Length == 0 || t.Amount == 0 || !_byPayee.TryGetValue(key, out var same)) return null;
        return CategoryMatching.Recurrence(DateOnly.FromDateTime(t.Date), t.Amount, same.Where(o => o.Id != t.Id).Select(o => (DateOnly.FromDateTime(o.Date), o.Amount)));
    }

    /// <summary>The booking with everything the redactor may pass on (own account token and role, order items, recurrence).</summary>
    public AssistBookingSource Source(Transaction t)
    {
        var account = t.AccountId is { } id && _accounts.TryGetValue(id, out var a) ? a : default;
        return AssistSettingsService.ToSource(t, account.Kind) with
        {
            OwnAccount = account.Token,
            AccountRole = account.Role,
            OrderItems = t.OrderItems,
            Recurrence = Recurrence(t),
        };
    }

    /// <summary>
    /// The neighbours for the prompt: up to <see cref="MaxKeyNeighbours"/> bookings of the same contract first (see
    /// <see cref="CategoryMatching.KeyNeighbours"/>), then the most similar ones, <see cref="MaxTextNeighbours"/> in all
    /// beyond the contract ones.
    /// </summary>
    public static List<HistoryBooking> PromptNeighbours(CategoryMatching matching, TargetBooking target, IEnumerable<Neighbour> knn, Func<long, bool>? allowed = null)
    {
        var list = matching.KeyNeighbours(target, MaxKeyNeighbours, allowed);
        foreach (var n in knn.Where(n => !list.Contains(n.Booking)).Take(MaxTextNeighbours)) list.Add(n.Booking);
        return list;
    }

    /// <summary>The prompt item of a booking and its categorized neighbours (redacted with <paramref name="redactor"/>).</summary>
    public PromptItem Item(AssistRedactor redactor, Transaction t, IEnumerable<HistoryBooking> neighbours)
    {
        var mandate = Mandate(t);
        var creditor = Creditor(t);
        var booking = redactor.Redact(Source(t));
        var list = new List<PromptNeighbour>();
        foreach (var n in neighbours)
        {
            if (!_rows.TryGetValue(n.Id, out var row) || !_categories.TryGetValue(n.CategoryId, out var category)) continue;
            list.Add(new PromptNeighbour(redactor.Redact(Source(row)), category.Handle, category.Path,
                mandate != null && Mandate(row) == mandate, creditor != null && Creditor(row) == creditor, CategoryMatching.SameAmount(t.Amount, row.Amount)));
        }
        return new PromptItem(booking, list);
    }

    /// <summary>
    /// The categories as the AI sees them: handle, path, income, the own accounts a restricted category is limited to
    /// and up to three of the household's enabled rules for it as hints. Rules on accounts, IBANs or SEPA references are
    /// left out, the rest is redacted (names, long digit runs).
    /// </summary>
    public static List<PromptCategory> Categories(
        IEnumerable<(long Id, string Path, bool Income)> categories, IEnumerable<CategoryRule> rules, IReadOnlyCollection<long> accountIds,
        Func<long, long, bool> allows, Func<long, string?> accountToken, IEnumerable<string?> personNames)
    {
        var redactor = new AssistRedactor(personNames);
        var hints = new Dictionary<long, List<string>>();
        foreach (var r in rules.Where(r => r.Enabled))
        {
            if (RuleExpression.TryParse(r.Expression, out _)?.Print() is not { } expr || PrivateField().IsMatch(expr)) continue;
            var clean = redactor.CleanText(expr, [], 80);
            if (clean.Contains("ACC_", StringComparison.Ordinal)) continue;
            if (!hints.TryGetValue(r.CategoryId, out var list)) hints[r.CategoryId] = list = [];
            if (list.Count < 3) list.Add(clean);
        }
        var result = new List<PromptCategory>();
        var i = 0;
        foreach (var c in categories)
        {
            var only = accountIds.Where(a => allows(c.Id, a)).ToList();
            var restriction = only.Count < accountIds.Count ? (only.Count == 0 ? "no account" : string.Join(", ", only.Select(accountToken).Where(x => x != null))) : null;
            result.Add(new PromptCategory(CategoryPrompt.Handle(++i), c.Id, c.Path, c.Income,
                hints.TryGetValue(c.Id, out var h) ? string.Join("; ", h) : null, restriction));
        }
        return result;
    }

    [GeneratedRegex(@"\b(?:account_id|account_group|local_account|remote_account|mandate|creditor_id|end_to_end|reference)\s*(?:[:=!<>])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PrivateField();
}
