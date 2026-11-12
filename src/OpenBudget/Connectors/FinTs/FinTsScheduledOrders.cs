using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OpenBudget.Connectors.FinTs;

/// <summary>One credit transfer of a pain.001 document (the debtor is the account itself).</summary>
internal sealed record PainTransfer(
    DateOnly? ExecutionDate, decimal Amount, string Currency, string? Name, string? Iban, string? Bic, string? Purpose);

/// <summary>Reads SEPA credit transfer initiations (pain.001, any version) as banks return them for stored orders.</summary>
internal static class Pain001Parser
{
    /// <summary>
    /// The transfers of one document. <paramref name="wire"/> is the binary data element as it arrived (one char per
    /// byte); the bytes are decoded as the XML declaration says (UTF-8 when it says nothing).
    /// </summary>
    public static List<PainTransfer> Parse(string wire)
    {
        XDocument doc;
        try
        {
            using var stream = new MemoryStream(Encoding.Latin1.GetBytes(wire));
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FinTsProtocolException("The bank sent an unreadable pain.001 document.", ex);
        }

        var result = new List<PainTransfer>();
        foreach (var payment in doc.Descendants().Where(e => e.Name.LocalName == "PmtInf"))
        {
            // pain.001.001.03 / .003.03: <ReqdExctnDt>2026-12-01</ReqdExctnDt>; from .001.09 on: <ReqdExctnDt><Dt>…</Dt></ReqdExctnDt>.
            var requested = Child(payment, "ReqdExctnDt");
            var date = ParseIsoDate(requested == null ? null : Child(requested, "Dt")?.Value ?? requested.Value);
            foreach (var tx in payment.Elements().Where(e => e.Name.LocalName == "CdtTrfTxInf"))
            {
                var amountElement = tx.Descendants().FirstOrDefault(e => e.Name.LocalName == "InstdAmt");
                if (amountElement == null || !decimal.TryParse(amountElement.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) continue;
                var purpose = string.Join(' ', tx.Descendants().Where(e => e.Name.LocalName == "Ustrd").Select(e => e.Value.Trim()).Where(v => v.Length > 0));
                result.Add(new PainTransfer(
                    date,
                    amount,
                    amountElement.Attribute("Ccy")?.Value ?? "EUR",
                    Blank(Child(tx, "Cdtr") is { } c ? Child(c, "Nm")?.Value : null),
                    Blank(Deep(tx, "CdtrAcct", "IBAN")),
                    Blank(Deep(tx, "CdtrAgt", "BIC") ?? Deep(tx, "CdtrAgt", "BICFI")),
                    Blank(purpose)));
            }
        }
        return result;
    }

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Deep(XElement parent, string container, string leaf) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == container)?.Descendants().FirstOrDefault(e => e.Name.LocalName == leaf)?.Value;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? ParseIsoDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// Turns the bank's answers to HKCDB (standing orders, HICDB) and HKCSB (future-dated single transfers, HICSB) into
/// <see cref="RemoteScheduledPayment"/>s. Both answers carry, per order: the account (KTI), the SEPA descriptor, the
/// pain.001 document and the bank's order id; HICDB adds the standing order details as one group (first execution,
/// time unit M/W, interval, execution day, last execution), HICSB the execution date.
/// </summary>
internal static class FinTsScheduledOrders
{
    public static List<RemoteScheduledPayment> ReadStandingOrders(IEnumerable<FinTsSegment> hicdb, DateOnly today)
    {
        var result = new List<RemoteScheduledPayment>();
        foreach (var segment in hicdb)
        {
            var orderId = Blank(segment.Element(3));
            var details = Details(segment);
            foreach (var transfer in Pain001Parser.Parse(segment.Element(2)))
            {
                var first = details.First ?? transfer.ExecutionDate;
                if (first == null) continue;
                var next = PaymentSchedule.Next(first.Value, details.Unit ?? ScheduleUnit.Month, details.Interval ?? 1, details.Day, details.Last, today);
                if (next == null) continue; // ended
                result.Add(new RemoteScheduledPayment(
                    ScheduledPaymentKind.Standing, orderId, next.Value, -transfer.Amount, transfer.Currency,
                    transfer.Name, transfer.Iban, transfer.Bic, transfer.Purpose,
                    first, details.Last, details.Unit ?? ScheduleUnit.Month, details.Interval ?? 1, details.Day ?? first.Value.Day));
            }
        }
        return result;
    }

    public static List<RemoteScheduledPayment> ReadScheduledTransfers(IEnumerable<FinTsSegment> hicsb, DateOnly today)
    {
        var result = new List<RemoteScheduledPayment>();
        foreach (var segment in hicsb)
        {
            var orderId = Blank(segment.Element(3));
            DateOnly? scheduled = null;
            for (var i = 4; i < segment.Elements.Count && scheduled == null; i++) scheduled = FinTsFormats.ParseDate(segment.Element(i));
            foreach (var transfer in Pain001Parser.Parse(segment.Element(2)))
            {
                var date = transfer.ExecutionDate ?? scheduled;
                if (date == null || date < today) continue;
                result.Add(new RemoteScheduledPayment(
                    ScheduledPaymentKind.Scheduled, orderId, date.Value, -transfer.Amount, transfer.Currency,
                    transfer.Name, transfer.Iban, transfer.Bic, transfer.Purpose));
            }
        }
        return result;
    }

    /// <summary>The standing order details group: the first element after the order id whose second component is the time unit.</summary>
    private static (DateOnly? First, ScheduleUnit? Unit, int? Interval, int? Day, DateOnly? Last) Details(FinTsSegment segment)
    {
        for (var i = 4; i < segment.Elements.Count; i++)
        {
            var deg = segment.Deg(i);
            if (deg.Count < 2) continue;
            ScheduleUnit? unit = deg[1] switch { "M" => ScheduleUnit.Month, "W" => ScheduleUnit.Week, _ => null };
            if (unit == null) continue;
            return (
                FinTsFormats.ParseDate(deg[0]),
                unit,
                deg.Count > 2 && int.TryParse(deg[2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1,
                deg.Count > 3 && int.TryParse(deg[3], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null,
                deg.Count > 4 ? FinTsFormats.ParseDate(deg[4]) : null);
        }
        return (null, null, null, null, null);
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
