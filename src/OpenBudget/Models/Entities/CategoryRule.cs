using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>Which sign of amount a <see cref="CategoryRule"/> matches.</summary>
public enum RuleAmountSign
{
    Any = 0,
    Expense = 1,
    Income = 2,
}

/// <summary>
/// A categorisation rule of a project ("Gear › Rules"). Rules are ordered per project (<see cref="SortOrder"/>); the
/// first enabled rule whose conditions all hold sets the category of an uncategorised transaction. Rules never touch a
/// category chosen by hand. Text conditions are case-insensitive "contains", or regular expressions when
/// <see cref="UseRegex"/> is set. Amount bounds are absolute values in cents, so "10 to 50" works for expenses too.
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

    /// <summary>Matched against the payee / counterparty name.</summary>
    [MaxLength(500)]
    public string? PayeeText { get; set; }

    /// <summary>Matched against the purpose and the bank's booking text.</summary>
    [MaxLength(500)]
    public string? PurposeText { get; set; }

    public bool UseRegex { get; set; }

    /// <summary>Restricts the rule to one account; null = every account of the project.</summary>
    public long? AccountId { get; set; }

    public RuleAmountSign AmountSign { get; set; }

    /// <summary>Lower bound of the absolute amount in cents (inclusive), if any.</summary>
    public long? MinAmountCents { get; set; }

    /// <summary>Upper bound of the absolute amount in cents (inclusive), if any.</summary>
    public long? MaxAmountCents { get; set; }

    /// <summary>The category to set (a category of the project's source).</summary>
    public long CategoryId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
}
