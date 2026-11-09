namespace OpenBudget.Services;

internal static class MoneyMath
{
    /// <summary>
    /// Convert a euro (or other currency) amount in REAL form to integer cents. Banker's
    /// rounding matches the C# default and avoids skewing on rows that land exactly on a
    /// half-cent boundary. Used by the importer when copying the source's REAL
    /// amount column into our INTEGER cents column.
    /// </summary>
    public static long EurosToCents(double euros)
        => (long)Math.Round(euros * 100.0, MidpointRounding.ToEven);
}
