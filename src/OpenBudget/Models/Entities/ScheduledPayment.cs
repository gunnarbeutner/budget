using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using OpenBudget.Connectors;

namespace OpenBudget.Models;

/// <summary>
/// A standing order or future-dated transfer the bank holds for an account, as the last sync reported it. Every
/// sync that reads them replaces the account's rows. Read-only: nothing here is ever sent back to the bank.
/// </summary>
[Table("scheduled_payments")]
public class ScheduledPayment
{
    [Key]
    public long Id { get; set; }

    /// <summary>The owning user (AppUser.Id).</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    public long AccountId { get; set; }

    public ScheduledPaymentKind Kind { get; set; }

    /// <summary>The bank's id of the order (FinTS "Auftragsidentifikation"), when it sends one.</summary>
    [MaxLength(100)]
    public string? BankOrderId { get; set; }

    /// <summary>The next execution on or after the day of the sync.</summary>
    public DateOnly NextDate { get; set; }

    /// <summary>A standing order's first execution; null for a single transfer.</summary>
    public DateOnly? FirstDate { get; set; }

    /// <summary>A standing order's last execution; null when it runs until cancelled.</summary>
    public DateOnly? LastDate { get; set; }

    public ScheduleUnit? IntervalUnit { get; set; }

    public int? IntervalCount { get; set; }

    /// <summary>Day of month (1-31, 97-99 for the ultimo codes, see <see cref="PaymentSchedule"/>) or weekday (1-7).</summary>
    public int? ExecutionDay { get; set; }

    /// <summary>Cents, negative for money leaving the account.</summary>
    public long Amount { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "EUR";

    [MaxLength(300)]
    public string? CounterpartyName { get; set; }

    [MaxLength(50)]
    public string? CounterpartyIban { get; set; }

    [MaxLength(20)]
    public string? CounterpartyBic { get; set; }

    [MaxLength(1000)]
    public string? Purpose { get; set; }

    public DateTime FetchedAt { get; set; }

    public Account? Account { get; set; }

    /// <summary>The executions of this order in <paramref name="from"/>..<paramref name="to"/> (inclusive).</summary>
    public IEnumerable<DateOnly> Occurrences(DateOnly from, DateOnly to) =>
        Kind == ScheduledPaymentKind.Standing
            ? PaymentSchedule.Occurrences(NextDate, IntervalUnit, IntervalCount, ExecutionDay, LastDate, from, to)
            : PaymentSchedule.Occurrences(NextDate, null, null, null, null, from, to);
}
