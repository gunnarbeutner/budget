using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>Which sign of amount the rule editor's convenience inputs restrict to (composed into the expression).</summary>
public enum RuleAmountSign
{
    Any = 0,
    Expense = 1,
    Income = 2,
}

/// <summary>
/// A categorisation rule of a project (edited in a category's rules bar on Transactions). Rules are ordered per project (<see cref="SortOrder"/>); the
/// first enabled rule whose <see cref="Expression"/> matches sets the category of an uncategorised transaction. Rules
/// never touch a category chosen by hand.
/// </summary>
[Table("category_rules")]
public class CategoryRule
{
    [Key]
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int SortOrder { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>The condition, a search expression (see <see cref="Services.RuleExpression"/>).</summary>
    [MaxLength(4000)]
    public string Expression { get; set; } = "";

    /// <summary>Where the rule came from: null = made here, otherwise the name of the importer that created it.</summary>
    [MaxLength(32)]
    public string? ImportOrigin { get; set; }

    /// <summary>
    /// For imported rules: the expression as last imported. While <see cref="Expression"/> still equals it the rule is
    /// untouched and a re-import may update it; once the user edited it, re-imports leave it alone.
    /// </summary>
    [MaxLength(4000)]
    public string? ImportedExpression { get; set; }

    /// <summary>
    /// An imported rule the user deleted: kept (hidden, never applied) so that re-imports do not bring it back.
    /// </summary>
    public bool Dismissed { get; set; }

    /// <summary>The category to set (a category of the project's source).</summary>
    public long CategoryId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
}
