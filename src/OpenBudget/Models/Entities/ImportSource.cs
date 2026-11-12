using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Named imported data source. Each source carries its own set of imported rows; the
/// source's <see cref="Id"/> namespaces every imported rowid so multiple source-app
/// installations can coexist in one budgets.db without ID collisions.
/// </summary>
[Table("import_sources")]
public class ImportSource
{
    [Key]
    public int Id { get; set; }

    /// <summary>Internal URL-safe slug, unique across users; never shown (see SourceService.GetLabelsAsync).</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last-write time of the most recent successful import for this source.</summary>
    public DateTime? LastUploadAt { get; set; }

    public long? LastUploadSizeBytes { get; set; }

    /// <summary>
    /// Owning user (AppUser.Id). Null only for legacy rows not yet claimed through the CLI; an
    /// unowned source is invisible to everyone. Accounts and transactions belong to the owner.
    /// </summary>
    [MaxLength(450)]
    public string? OwnerUserId { get; set; }

    /// <summary>Not used by the app; the column stays in the schema.</summary>
    [MaxLength(100)]
    public string? UploadToken { get; set; }

    /// <summary>
    /// When the owner took over this source's structure (account groups, accounts' names/groups/order, category
    /// groups, categories) from MoneyMoney. From then on MoneyMoney uploads still deliver transactions and balances
    /// of MoneyMoney-owned accounts but never overwrite the structure. Null while MoneyMoney still owns it.
    /// </summary>
    public DateTime? StructureTakenOverAt { get; set; }

    /// <summary>
    /// When the owner declared the MoneyMoney import finished: from then on MoneyMoney uploads (databases and statements)
    /// are refused with 409 Conflict, so the import was one-time. Null while uploads are accepted; the owner can resume them.
    /// </summary>
    public DateTime? MoneyMoneyImportFinishedAt { get; set; }

    public ICollection<Project> Projects { get; set; } = new List<Project>();
}
