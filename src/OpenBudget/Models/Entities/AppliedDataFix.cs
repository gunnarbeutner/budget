using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>A data fix (see <c>DataFixRunner</c>) that ran to completion on this database, and when.</summary>
[Table("data_fixes")]
public class AppliedDataFix
{
    [Key]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public DateTime AppliedAt { get; set; }
}
