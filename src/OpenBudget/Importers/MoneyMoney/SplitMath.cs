namespace OpenBudget.Importers.MoneyMoney;

/// <summary>Arithmetic for MoneyMoney split bookings.</summary>
public static class SplitMath
{
    /// <summary>
    /// The part of a split parent's amount not covered by its children (all in cents). Zero when
    /// the children add up exactly; negative when they over-cover a positive parent.
    /// </summary>
    public static long Remainder(long parentCents, long childrenSumCents) => parentCents - childrenSumCents;
}
