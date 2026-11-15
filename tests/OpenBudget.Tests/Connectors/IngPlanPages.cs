namespace OpenBudget.Tests.Connectors;

/// <summary>
/// ING web banking pages for payout and savings plans, shaped like the real ones (Wicket, server-rendered) with made-up
/// names, IBANs and amounts: the plan overviews ("Übersicht Auszahlpläne", "Übersicht Sparaufträge") and "Konto verwalten".
/// </summary>
internal static class IngPlanPages
{
    /// <summary>A plan row: name (Empfänger or Kontoinhaber), purpose, next execution, interval, unsigned amount.</summary>
    public sealed record Row(string Name, string Purpose, string Date, string Interval, string Amount, string Iban = "DE00 0000 0000 0000 0000 00", string Last = "");

    public static string Header(string label, string iban) =>
        "<section class=\"g2p-banking-header\"><div class=\"g2p-banking-header__account\">"
        + $"<div class=\"g2p-banking-header__account__label\">{label}</div>"
        + $"<div class=\"g2p-banking-header__account__iban\">{iban}</div>"
        + "<div class=\"g2p-amount\">12.345,67 €</div></div></section>";

    /// <summary>The account's function menu, as on the transaction list and the plan overviews.</summary>
    public static string Menu(string payoutHref, string? savingsHref = null) =>
        "<ul class=\"function-menu\">"
        + "<li><a href=\"./einzahlen?x=e1\">Einzahlen</a></li><li><a href=\"./auszahlen?x=a1\">Auszahlen</a></li>"
        + "<li><a href=\"./termin?x=t1\">Terminüberweisungen</a></li>"
        + $"<li><a href=\"{payoutHref}\">Auszahlpläne</a></li>"
        + (savingsHref == null ? "" : $"<li><a href=\"{savingsHref}\">Sparaufträge</a></li>")
        + "<li><a href=\"./referenz?x=r1\">Referenzkonto ändern</a></li><li><a href=\"./zinsen?x=z1\">Zinsen</a></li></ul>";

    /// <summary>A plan overview; <paramref name="who"/> is the first column ("Empfänger" for payouts, "Kontoinhaber" for savings plans).</summary>
    public static string Overview(string title, string who, string details, string label, string iban, IReadOnlyList<Row> rows, string empty, string menu = "")
    {
        var body = rows.Count == 0
            ? $"<p class=\"info\">{empty}</p>"
            : "<div class=\"ibbr-table\" role=\"table\"><div class=\"ibbr-table-header--bordered\" role=\"row\">"
              + $"<span class=\"ibbr-table-cell\" role=\"columnheader\"><a href=\"#\">{who}</a></span>"
              + "<span class=\"ibbr-table-cell\" role=\"columnheader\"><a href=\"#\">Verwendungszweck</a></span>"
              + "<span class=\"ibbr-table-cell\" role=\"columnheader\"><a href=\"#\">Ausführung</a></span>"
              + "<span class=\"ibbr-table-cell\" role=\"columnheader\"><a href=\"#\">Intervall</a></span>"
              + "<span class=\"ibbr-table-cell\" role=\"columnheader\"><a href=\"#\">Betrag <span class=\"u-hidden-sm\">(EUR)</span></a></span>"
              + "<span class=\"ibbr-table-cell\" role=\"columnheader\"></span></div>"
              + "<div class=\"ibbr-table-body--bordered\">"
              + string.Concat(rows.Select((r, i) =>
                  "<div class=\"ibbr-table-row\" role=\"row\">"
                  + $"<span class=\"ibbr-table-cell u-visible-sm\" role=\"cell\"><span>{r.Name}</span><span>{r.Date}</span><span>|</span><span>{r.Interval}</span><span>{r.Purpose}</span></span>"
                  + $"<span class=\"ibbr-table-cell u-hidden-sm\" role=\"cell\"><span>{r.Name}</span></span>"
                  + $"<span class=\"ibbr-table-cell u-hidden-sm\" role=\"cell\">{r.Purpose}</span>"
                  + $"<span class=\"ibbr-table-cell u-hidden-sm\" role=\"cell\"><span>{r.Date}</span></span>"
                  + $"<span class=\"ibbr-table-cell u-hidden-sm\" role=\"cell\"><span>{r.Interval}</span></span>"
                  + $"<span class=\"ibbr-table-cell\" role=\"cell\"><span>{r.Amount}</span></span>"
                  + "<span class=\"ibbr-table-cell\" role=\"cell\"><span><button class=\"button-tiny--down\" type=\"button\"></button></span></span>"
                  + "<div class=\"action-bar--buttons\" role=\"cell\"><div class=\"ibbr-table-expander__content\">"
                  + $"<a href=\"./{details}?x=d{i}\" class=\"button grey font16\">Details</a><a href=\"./aendern?x=c{i}\" class=\"button grey font16\">Ändern</a><a href=\"./loeschen?x=l{i}\" class=\"button grey font16\">Löschen</a></div></div>"
                  + "</div>"))
              + "</div></div>";
        return $"<html><head><title>{title} - ING Internetbanking</title></head><body>{Header(label, iban)}{menu}{body}</body></html>";
    }

    public static string Payouts(string label, string iban, IReadOnlyList<Row> rows, string menu = "") =>
        Overview("Übersicht Auszahlpläne", "Empfänger", "auszahlplan_details", label, iban, rows, "Es sind keine Auszahlpläne vorhanden.", menu);

    public static string Savings(string label, string iban, IReadOnlyList<Row> rows, string menu = "") =>
        Overview("Übersicht Sparaufträge", "Kontoinhaber", "sparauftrag_details", label, iban, rows, "Es sind keine Sparaufträge vorhanden.", menu);

    /// <summary>A plan's detail page ("Auszahlplan Details", "Sparauftrag Details"): label and value per form group.</summary>
    public static string Details(string title, string label, string iban, Row r)
    {
        static string Group(string name, string value) =>
            $"<div class=\"form-group\"><div class=\"form-group__label\">{name}</div><span class=\"form-group__controls form-group--static\">"
            + (value.Length == 0 ? "" : $"<span class=\"form-group--text-bold\">{value}</span>") + "</span></div>";
        return $"<html><head><title>{title} - ING Internetbanking</title></head><body>{Header(label, iban)}<div class=\"form-container_content\">"
            + Group("Auftraggeber", "Erika Mustermann") + Group("Zahlungsempfänger", r.Name) + Group("IBAN", r.Iban) + Group("Bank", "ING-DiBa AG")
            + Group("Betrag", r.Amount + " EUR") + Group("Verwendungszweck", r.Purpose) + Group("Nächste Ausführung", r.Date)
            + Group("Letzte Ausführung", r.Last) + Group("Intervall", r.Interval) + Group("Referenz", "")
            + "</div></body></html>";
    }

    /// <summary>"Konto verwalten": per savings account its IBAN, its reference account and a link to its savings plans.</summary>
    public static string Settings(params (string Label, string Iban, string ReferenceIban, string SavingsHref)[] accounts) =>
        "<html><head><title>Konto verwalten - ING Internetbanking</title></head><body>"
        + string.Concat(accounts.Select(a =>
            $"<section class=\"product\"><h2>{a.Label}</h2><dl><dt>IBAN</dt><dd>{a.Iban}</dd>"
            + $"<dt>Referenzkonto</dt><dd>IBAN {a.ReferenceIban}</dd><dd>Bank ING-DIBA AG</dd></dl>"
            + $"<h3>Sparpläne</h3><a href=\"{a.SavingsHref}\">Übersicht</a></section>"))
        + "</body></html>";
}
