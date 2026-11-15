namespace OpenBudget.Services;

/// <summary>
/// Words in payee names and purposes that say nothing about the merchant: legal forms, filler words and booking
/// boilerplate. Compared against upper-case words with umlauts spelled out ("FUER").
/// </summary>
public static class MerchantNoise
{
    public static readonly IReadOnlySet<string> Words = new HashSet<string>(StringComparer.Ordinal)
    {
        "GMBH", "MBH", "AG", "KG", "SE", "CO", "UG", "EV", "OHG", "GBR", "LTD", "INC", "SAGT", "DANKE", "UND", "DER", "DIE", "DAS",
        "FUER", "VOM", "BEI", "MIT", "IHR", "IHRE", "EUR", "SEPA", "LASTSCHRIFT", "UEBERWEISUNG", "GUTSCHRIFT", "KARTENZAHLUNG",
        "DATUM", "UHR", "ZAHLUNG", "AUFTRAG", "REF", "NR", "KTO", "BLZ", "VISA", "DEBIT", "GIROCARD", "ONLINE",
    };

    public static bool Contains(string word) => Words.Contains(word);
}
