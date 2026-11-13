using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>
/// The per-user "Use AI suggestions" setting. Bookings may only be sent when the asking user and the owner of the
/// booking both opted in. Also builds the preview of what is sent for a sample booking.
/// </summary>
public sealed class AssistSettingsService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly IAssistClient _client;

    public AssistSettingsService(IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, IAssistClient client)
    {
        _dbFactory = dbFactory;
        _system = system;
        _client = client;
    }

    /// <summary>Whether the server has an AI service configured.</summary>
    public bool Available => _client.Enabled;

    public string Model => _client.Model;

    /// <summary>Whether the signed-in user opted in.</summary>
    public async Task<bool> GetOptInAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var userId = db.Scope?.UserId;
        if (userId == null) return false;
        return await db.UserDataStates.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.UseAiSuggestions).FirstOrDefaultAsync(ct);
    }

    public async Task SetOptInAsync(bool value, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var userId = db.Scope?.UserId ?? throw new AccessDeniedException("Not signed in.");
        var state = await db.UserDataStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state == null) return;
        state.UseAiSuggestions = value;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The users among <paramref name="userIds"/> who opted in.</summary>
    public async Task<HashSet<string>> OptedInAsync(IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct(StringComparer.Ordinal).ToList();
        await using var db = await _system.CreateDbContextAsync(ct);
        return (await db.UserDataStates.AsNoTracking().Where(s => ids.Contains(s.UserId) && s.UseAiSuggestions).Select(s => s.UserId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The names of all users of this server (display and user names), which the redactor replaces.</summary>
    public async Task<List<string>> PersonNamesAsync(CancellationToken ct = default)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        var rows = await db.Users.AsNoTracking().Select(u => new { u.DisplayName, u.UserName }).ToListAsync(ct);
        return rows.SelectMany(r => new[] { r.DisplayName, r.UserName }).Where(n => !string.IsNullOrWhiteSpace(n) && n!.Length >= 3).Select(n => n!).ToList();
    }

    /// <summary>The newest booking of the signed-in user exactly as it would be sent, or null without bookings.</summary>
    public async Task<AssistBooking?> PreviewAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var userId = db.Scope?.UserId;
        if (userId == null) return null;
        var t = await db.Transactions.AsNoTracking().Where(x => x.OwnerUserId == userId && x.Splitted != SplitState.Parent)
            .OrderByDescending(x => x.Timestamp).Include(x => x.Account).FirstOrDefaultAsync(ct);
        if (t == null) return null;
        var redactor = new AssistRedactor(await PersonNamesAsync(ct));
        return redactor.Redact(ToSource(t, t.Account?.KindLabel));
    }

    public static AssistBookingSource ToSource(Transaction t, string? accountKind) => new(
        t.Id, DateOnly.FromDateTime(t.Date), t.Amount, t.Currency, t.Name, t.Purpose, t.BookingText, t.TransactionCode, accountKind,
        t.CounterpartyIban, t.CounterpartyBic, t.MandateReference, t.CreditorId, t.EndToEndReference, t.BankReference);
}
