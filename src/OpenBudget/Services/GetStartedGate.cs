using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

/// <summary>
/// Whether the signed-in user is held in Get started: until they finish it (<see cref="OnboardingState.IsFinished"/>)
/// no other page opens and the app's navigation is hidden. Computed once per circuit or request and recomputed after
/// the user's import state changes.
/// </summary>
public sealed class GetStartedGate : IDisposable
{
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;
    private readonly ChangeNotificationService _notifier;
    private bool? _captive;

    public GetStartedGate(SystemDbContextFactory system, AccessScopeProvider access, ChangeNotificationService notifier)
    {
        _system = system;
        _access = access;
        _notifier = notifier;
        _notifier.Changed += OnChanged;
    }

    /// <summary>The last computed answer (false until computed).</summary>
    public bool Captive => _captive == true;

    public async Task<bool> IsCaptiveAsync()
    {
        if (_captive is { } known) return known;
        var userId = (await _access.GetAsync()).UserId;
        if (userId == null) return false;
        await using var db = await _system.CreateDbContextAsync();
        var state = await db.UserDataStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId);
        _captive = !OnboardingState.IsFinished(state);
        return _captive.Value;
    }

    /// <summary>Forgets the answer, so the next check reads the state again.</summary>
    public void Invalidate() => _captive = null;

    private void OnChanged(DataChange change, Guid? origin)
    {
        if (change.Area == DataArea.ImportState) _captive = null;
    }

    public void Dispose() => _notifier.Changed -= OnChanged;
}
