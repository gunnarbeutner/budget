using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A pending booking the user had edited (category, note, check mark or split) that a later bank sync no longer
/// reported and that matched no new booking. Shown under "To review" until dismissed; the sync removes it after
/// <see cref="Lifetime"/>.
/// </summary>
[Table("cancelled_reservations")]
public class CancelledReservation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    public long AccountId { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    /// <summary>Amount in cents, signed like the booking.</summary>
    public long Amount { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    public DateTime CreatedAt { get; set; }
}
