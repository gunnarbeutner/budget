using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A cancelled reservation as "To review" lists it.</summary>
public sealed record CancelledReservationView(int Id, string? Name, decimal Amount, string? Currency);

/// <summary>
/// Edited pending bookings the bank dropped (see <see cref="CancelledReservation"/>): listed per project, dismissed by
/// whoever may edit the account's bookings. Runs through the user-scoped context.
/// </summary>
public sealed class CancelledReservationService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public CancelledReservationService(IDbContextFactory<BudgetDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<CancelledReservationView>> ListAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.CancelledReservations.AsNoTracking()
            .Where(r => db.ProjectAccountAssignments.Any(a => a.ProjectId == projectId && a.AccountId == r.AccountId))
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => new { r.Id, r.Name, r.Amount, r.Currency })
            .ToListAsync(ct);
        return rows.Select(r => new CancelledReservationView(r.Id, r.Name, r.Amount / 100m, r.Currency)).ToList();
    }

    public async Task DismissAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.CancelledReservations.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row == null) return;
        db.CancelledReservations.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
