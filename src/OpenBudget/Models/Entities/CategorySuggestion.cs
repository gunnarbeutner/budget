using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A cached category suggestion for an uncategorized booking. Valid while the owner's category tree hashes to
/// <see cref="TreeHash"/>; a changed tree makes it stale. Written and read through the system context, only for
/// bookings the asking user can see.
/// </summary>
[Table("category_suggestions")]
public class CategorySuggestion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long TransactionId { get; set; }

    public long? CategoryId { get; set; }

    public double Confidence { get; set; }

    /// <summary>"rule", "iban", "knn" or "ai".</summary>
    [MaxLength(10)]
    public string Source { get; set; } = "";

    [MaxLength(300)]
    public string? Rationale { get; set; }

    /// <summary>A suggested split (JSON list of parts: amount in cents, category, label), or null for a single category.</summary>
    public string? SplitJson { get; set; }

    [MaxLength(80)]
    public string? Model { get; set; }

    [MaxLength(64)]
    public string TreeHash { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}
