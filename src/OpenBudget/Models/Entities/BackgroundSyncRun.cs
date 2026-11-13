using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

public enum SyncRunResult
{
    Done = 0,
    NeedsUser = 1,
    Failed = 2,
}

/// <summary>
/// One entry of a login's sync log: every background run, and manual runs of FinTS logins (for the outcome of their dialog
/// init: whether the bank wanted a TAN). Never holds secrets: only the result, a short reason and FinTS return codes.
/// </summary>
[Table("background_sync_runs")]
public class BackgroundSyncRun
{
    [Key]
    public long Id { get; set; }

    public int BankLoginId { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>A background run (false: a manual one, logged for its FinTS codes).</summary>
    public bool Background { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime FinishedAt { get; set; }

    public SyncRunResult Result { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }

    /// <summary>The SCA-related return codes of the FinTS dialog inits (3076: no TAN needed; 0030/3955: TAN wanted).</summary>
    [MaxLength(200)]
    public string? FinTsCodes { get; set; }

    /// <summary>The bank code of a FinTS login (the evidence for the per-bank background setting).</summary>
    [MaxLength(20)]
    public string? Blz { get; set; }
}

/// <summary>Per FinTS bank (by bank code): whether background runs may use FinTS at all. Off until an admin switches it on.</summary>
[Table("fints_bank_settings")]
public class FinTsBankSetting
{
    [Key]
    [MaxLength(20)]
    public string Blz { get; set; } = string.Empty;

    public bool BackgroundEnabled { get; set; }
}
