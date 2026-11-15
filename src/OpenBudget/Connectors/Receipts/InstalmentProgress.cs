namespace OpenBudget.Connectors.Receipts;

/// <summary>One instalment charge of an order paid in instalments: its receipt, day, amount (positive cents) and booking.</summary>
public sealed record InstalmentCharge(long ReceiptId, DateTime? Date, long Cents, bool Matched, long? TransactionId = null);

/// <summary>An instalment still to come: its number in the plan, the expected day and its amount (positive cents).</summary>
public sealed record InstalmentDue(int Number, DateTime Date, long Cents);

/// <summary>
/// Where an order paid in instalments stands: how many instalments the plan has, how many are charged and what they
/// paid, the instalments still due with their expected days, and whether the plan is complete or cancelled.
/// </summary>
public sealed record InstalmentProgress(long FullCents, int Count, IReadOnlyList<InstalmentCharge> Charges, IReadOnlyList<InstalmentDue> Due, bool Complete, bool Cancelled)
{
    public int Charged => Charges.Count;

    public long PaidCents => Charges.Sum(c => c.Cents);

    /// <summary>The expected day of the next instalment (null when none is due).</summary>
    public DateTime? NextDate => Due.Count > 0 ? Due[0].Date : null;

    /// <summary>Whether instalments are still to come.</summary>
    public bool Running => !Complete && !Cancelled;

    /// <summary>
    /// The progress of a plan of the full price <paramref name="fullCents"/> given its instalment charges and the order's
    /// refunds on the same login (positive cents).
    /// <para>
    /// The number of instalments is the full price over the first instalment, rounded (Amazon splits evenly, with a cent
    /// of rounding on the first or last). The plan is complete when the charges add up to the full price (one cent of
    /// rounding per instalment) and all are matched. It is cancelled (returned or cancelled order: the remaining
    /// instalments never come) when a refund equals the full price (the plan's reversal), or the other refunds pay back
    /// all instalments charged so far.
    /// </para>
    /// <para>
    /// The next instalment is expected one typical gap (the median of the gaps between the charges) after the last one,
    /// or one month after it when only one is charged; each further one a gap later. Due instalments split what is left
    /// evenly, the last one taking the rounding remainder.
    /// </para>
    /// </summary>
    public static InstalmentProgress Of(long fullCents, IEnumerable<InstalmentCharge> charges, IEnumerable<long> refundCents)
    {
        var list = charges.OrderBy(c => c.Date).ThenBy(c => c.ReceiptId).ToList();
        var refunds = refundCents.Select(Math.Abs).ToList();
        var paid = list.Sum(c => c.Cents);
        var count = list.Count == 0 || list[0].Cents <= 0 ? list.Count
            : Math.Max(list.Count, (int)Math.Round((double)fullCents / list[0].Cents, MidpointRounding.AwayFromZero));
        var complete = list.Count > 0 && paid + list.Count >= fullCents && list.All(c => c.Matched);
        var reversed = refunds.Contains(fullCents);
        var paidBack = list.Count > 0 && refunds.Where(r => r != fullCents).Sum() + list.Count >= paid;
        var cancelled = reversed || paidBack;

        var due = new List<InstalmentDue>();
        if (!complete && !cancelled && list.Count > 0 && list[^1].Date is { } last && count > list.Count)
        {
            var dates = list.Where(c => c.Date != null).Select(c => c.Date!.Value).ToList();
            var gaps = dates.Zip(dates.Skip(1), (a, b) => (b - a).TotalDays).Order().ToList();
            var gap = gaps.Count == 0 ? (double?)null : gaps[gaps.Count / 2];
            var left = Math.Max(0, fullCents - paid);
            var remaining = count - list.Count;
            var each = left / remaining;
            for (var k = 1; k <= remaining; k++)
            {
                var date = gap is { } g && g >= 1 ? last.AddDays(Math.Round(g * k)) : last.AddMonths(k);
                var cents = k == remaining ? left - each * (remaining - 1) : each;
                due.Add(new InstalmentDue(list.Count + k, date, cents));
            }
        }
        return new InstalmentProgress(fullCents, count, list, due, complete, cancelled);
    }
}
