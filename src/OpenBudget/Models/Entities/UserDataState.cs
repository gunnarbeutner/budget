using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Per-user state of the user's own data: the id space new imported rows are numbered in, the MoneyMoney import's
/// progress, and whether Get started was dismissed. One row per user, created on first need.
/// </summary>
[Table("user_data_states")]
public class UserDataState
{
    /// <summary>AppUser.Id.</summary>
    [Key]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// The user's id space (see <see cref="Services.RowIds"/>): rows the MoneyMoney import and the connectors create get
    /// ids <c>RowIds.Namespace(IdSpace, rowid)</c>. Unique across users, assigned as the largest existing value + 1.
    /// </summary>
    public int IdSpace { get; set; }

    /// <summary>When the user's MoneyMoney import finished copying; a further import is refused while it is set.</summary>
    public DateTime? LastUploadAt { get; set; }

    public long? LastUploadSizeBytes { get; set; }

    /// <summary>
    /// When the user's structure (account groups, accounts' names/groups/order, category groups, categories) became
    /// OpenBudget's (its siblings got explicit sort positions).
    /// </summary>
    public DateTime? StructureTakenOverAt { get; set; }

    /// <summary>
    /// Comma-separated numbers under which the user's last PortfolioPerformance file may still be stored from before files
    /// were kept per user; moved at startup (see <c>UploadedPortfolioFileSource.RelocateLegacyFilesAsync</c>), then null.
    /// </summary>
    [MaxLength(100)]
    public string? LegacyFileKeys { get; set; }

    /// <summary>When the user completed Get started with Done; until then it holds them.</summary>
    public DateTime? GetStartedCompletedAt { get; set; }

    /// <summary>The user allows their bookings to be sent to the AI service for suggestions (off by default).</summary>
    public bool UseAiSuggestions { get; set; }
}
