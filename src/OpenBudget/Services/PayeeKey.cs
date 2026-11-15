namespace OpenBudget.Services;

/// <summary>
/// The merchant in a payee name, for grouping and comparing bookings. Text is folded (<see cref="TextFold"/>), split
/// into words at anything but letters and digits, and words containing digits (references, ids, dates) are dropped. A
/// card descriptor is cut at its "*": the part before it names the merchant, unless that part is only a payment
/// processor ("PAYPAL *STREAMIO", "SumUp *Cafe Rose"), in which case the part after it does.
/// </summary>
public static class PayeeKey
{
    /// <summary>Payment processors that stand in front of the actual merchant.</summary>
    public static readonly IReadOnlySet<string> PaymentProcessors = Set(
        "paypal", "klarna", "stripe", "sumup", "adyen", "mollie", "zettle", "payone", "unzer", "sofort", "giropay");

    /// <summary>Legal forms of companies.</summary>
    public static readonly IReadOnlySet<string> LegalForms = Set(
        "gmbh", "mbh", "ag", "kg", "se", "co", "ug", "ev", "ohg", "gbr", "ltd", "llc", "inc", "sarl");

    /// <summary>Filler words, booking boilerplate, card schemes and web address parts.</summary>
    public static readonly IReadOnlySet<string> Boilerplate = Set(
        "sagt", "danke", "und", "der", "die", "das", "fuer", "vom", "bei", "mit", "ihr", "ihre", "the",
        "eur", "sepa", "lastschrift", "ueberweisung", "gutschrift", "kartenzahlung", "karte", "card", "datum", "uhr", "zahlung",
        "payment", "auftrag", "ref", "nr", "kto", "blz", "visa", "mastercard", "debit", "girocard", "online",
        "sub", "subscription", "abo", "bill", "billing", "mktp", "europe",
        "com", "www", "http", "https", "net", "org", "de", "eu", "uk", "us");

    /// <summary>Every word that says nothing about the merchant: processors, legal forms and boilerplate.</summary>
    public static readonly IReadOnlySet<string> StopWords =
        new HashSet<string>(PaymentProcessors.Concat(LegalForms).Concat(Boilerplate), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="word"/> (either case, umlauts spelled out) is a stop word.</summary>
    public static bool IsStopWord(string word) => StopWords.Contains(word);

    /// <summary>The folded words of <paramref name="text"/> without words containing digits.</summary>
    public static IEnumerable<string> Words(string? text, TextCase textCase = TextCase.Lower)
    {
        var folded = TextFold.Fold(text, textCase).ToCharArray();
        for (var i = 0; i < folded.Length; i++)
            if (!char.IsLetterOrDigit(folded[i])) folded[i] = ' ';
        return new string(folded).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Any(char.IsDigit));
    }

    /// <summary>
    /// The payee words of <paramref name="name"/> (or <paramref name="purpose"/> when there is no name), stop words
    /// included: "Amzn Mktp DE*AB12" is "amzn mktp de".
    /// </summary>
    public static string Of(string? name, string? purpose = null, TextCase textCase = TextCase.Lower)
    {
        var text = string.IsNullOrWhiteSpace(name) ? purpose ?? string.Empty : name;
        var star = text.IndexOf('*', StringComparison.Ordinal);
        if (star > 0)
        {
            var before = Words(text[..star], textCase).ToList();
            var after = Words(text[(star + 1)..], textCase).ToList();
            return string.Join(' ', before.Count > 0 && after.Count > 0 && before.All(PaymentProcessors.Contains) ? after : before);
        }
        return string.Join(' ', Words(text, textCase));
    }

    /// <summary><see cref="Of"/> without stop words: "REWE Markt GmbH" is "rewe markt".</summary>
    public static string Merchant(string? name, string? purpose = null, TextCase textCase = TextCase.Lower) =>
        string.Join(' ', Of(name, purpose, textCase).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !IsStopWord(w)));

    private static HashSet<string> Set(params string[] words) => new(words, StringComparer.OrdinalIgnoreCase);
}
