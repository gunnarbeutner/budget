namespace OpenBudget.Services;

/// <summary>
/// Per-circuit "which project is the user in" — set by every project-scoped page (through
/// <c>ProjectPageHeader</c>) and read by the app chrome in <c>MainLayout</c>: the project switcher shows its
/// name and the gear menu its settings links. It keeps the last project when the user moves on to a
/// non-project page (bank logins, MoneyMoney import), so the chrome still knows where "back" is.
/// Registered scoped, i.e. one per Blazor circuit.
/// </summary>
public sealed class CurrentProjectState
{
    public int? ProjectId { get; private set; }
    public string? ProjectName { get; private set; }

    /// <summary>Raised after <see cref="Set"/> changed the project or its name.</summary>
    public event Action? Changed;

    public void Set(int projectId, string? name)
    {
        // Pages pass "" while their project is still loading: keep the known name of the same project then.
        var newName = string.IsNullOrEmpty(name) ? (ProjectId == projectId ? ProjectName : null) : name;
        if (ProjectId == projectId && ProjectName == newName) return;
        ProjectId = projectId;
        ProjectName = newName;
        Changed?.Invoke();
    }
}
