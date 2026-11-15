namespace OpenBudget.Assist;

/// <summary>
/// The tuning knobs of the suggestion features in one place. Each value trades how much is suggested against how
/// often a suggestion is wrong; none is tied to a particular booking, payee or currency.
/// </summary>
public static class AssistTuning
{
    /// <summary>
    /// "Suggest budget": a booking counts as a one-off only when its robust z-score (0.6745 · distance from the median
    /// / MAD) is above this. 3.5 is the usual cut-off for robust outlier detection (Iglewicz and Hoaglin).
    /// </summary>
    public const double OutlierZ = 3.5;

    /// <summary>
    /// "Suggest budget": a year total the AI proposes is flagged for review when it differs from the deterministic
    /// baseline by more than this share (0.3 = 30 %).
    /// </summary>
    public const double FlagDeviation = 0.3;

    /// <summary>
    /// Category suggestions: a deterministic suggestion (rule, exact key or similar bookings) at least this sure is
    /// shown as is; less sure ones are sent to the AI when it is enabled.
    /// </summary>
    public const double SureEnough = 0.75;

    /// <summary>
    /// Category suggestions: a proposed rule may contradict at most this share of the categorized bookings it matches
    /// (rounded down, so a rule with fewer than 20 matches may contradict none).
    /// </summary>
    public const double MaxRuleConflictShare = 0.05;

    /// <summary>Receipt items: the AI's placement of a receipt line is kept only when its confidence is at least this.</summary>
    public const double MinReceiptItemConfidence = 0.6;
}
