using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A detected series of recurring charges the user marked "not a subscription": the Recurring view, Scheduled and the
/// cashflow forecast leave it out for that user. <see cref="SeriesKey"/> is <c>RecurringSeries.Key</c>.
/// </summary>
[Table("recurring_dismissals")]
public class RecurringDismissal
{
    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string SeriesKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
