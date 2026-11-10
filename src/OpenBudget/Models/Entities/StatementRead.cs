using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// That a user has opened a statement (or marked it read). Per user: everyone who can see a statement has
/// their own unread state. A statement without a row is unread only when it was downloaded after the user's
/// account was created (see <c>DocumentService</c>).
/// </summary>
[Table("statement_reads")]
public class StatementRead
{
    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public long StatementId { get; set; }

    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
}
