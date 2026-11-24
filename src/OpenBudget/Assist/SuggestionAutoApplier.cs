using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>What categorises an owner's new bookings after a sync, after the rules (see <see cref="SuggestionAutoApplier"/>).</summary>
public interface ISyncCategorizer
{
    Task CategorizeAsync(string ownerUserId, CancellationToken ct);
}

/// <summary>
/// After a sync, gives an owner's uncategorized bookings their suggested category (similar bookings, then the AI), in
/// each of their projects by project order (see <see cref="CategorySuggestionService.ApplySuggestionsAsync"/>). Only
/// for owners who opted in to AI suggestions. Runs in its own DI scope acting for the owner (<see cref="ActingUser"/>),
/// so it reads and writes only what the owner could themselves. A failure is logged; the sync's data stays.
/// </summary>
public sealed class SuggestionAutoApplier : ISyncCategorizer
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SystemDbContextFactory _system;
    private readonly ChangeNotificationService _notifier;
    private readonly ILogger<SuggestionAutoApplier> _log;

    public SuggestionAutoApplier(IServiceScopeFactory scopes, SystemDbContextFactory system, ChangeNotificationService notifier, ILogger<SuggestionAutoApplier> log)
    {
        _scopes = scopes;
        _system = system;
        _notifier = notifier;
        _log = log;
    }

    public async Task CategorizeAsync(string ownerUserId, CancellationToken ct)
    {
        List<int> projects;
        await using (var db = await _system.CreateDbContextAsync(ct))
        {
            if (!await db.UserDataStates.AsNoTracking().AnyAsync(s => s.UserId == ownerUserId && s.UseAiSuggestions, ct)) return;
            projects = await db.Projects.AsNoTracking().Where(p => p.OwnerUserId == ownerUserId)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => p.Id).ToListAsync(ct);
        }
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ActingUser>().Set(ownerUserId);
            var suggestions = scope.ServiceProvider.GetRequiredService<CategorySuggestionService>();
            var applied = 0;
            foreach (var project in projects) applied += await suggestions.ApplySuggestionsAsync(project, ct);
            if (applied == 0) return;
            _log.LogInformation("Suggested categories for {Count} bookings after the sync.", applied);
            _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: ownerUserId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Suggesting categories after the sync failed.");
        }
    }
}
