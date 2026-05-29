namespace OpenBudget.Services;

/// <summary>
/// The data areas a change can touch. Subscribers filter on this (plus the optional
/// project/source scope on <see cref="DataChange"/>) to decide whether a change is theirs to
/// care about.
/// </summary>
public enum DataArea
{
    /// <summary>Project rows, tracking items, account/category assignments, sort order.</summary>
    Projects,
    /// <summary>Budgets, their monthly entries, and contribution splits.</summary>
    Budgets,
    /// <summary>Project tracking-item rows specifically (a finer signal than Projects).</summary>
    TrackingItems,
    /// <summary>MoneyMoney source registrations (add / delete).</summary>
    Sources,
    /// <summary>Imported MoneyMoney transactions/categories/accounts for a source.</summary>
    MoneyMoneyData
}

/// <summary>
/// A single change committed somewhere in the app. <see cref="ProjectId"/> / <see cref="SourceId"/>
/// are set when the change is scoped to one project or source so subscribers can ignore changes
/// to data they aren't showing; null means "applies broadly within this area".
/// </summary>
public readonly record struct DataChange(DataArea Area, int? ProjectId = null, int? SourceId = null);

/// <summary>
/// In-process pub/sub that lets one Blazor circuit tell every other circuit "this data changed,
/// reload it". The app runs as a single process, so an in-memory event is sufficient — no Redis
/// or DB polling. Registered as a singleton; writers call <see cref="Publish"/> after a
/// successful commit and components subscribe to <see cref="Changed"/> for the lifetime of the
/// page (unsubscribing on dispose).
/// </summary>
public sealed class ChangeNotificationService
{
    /// <summary>
    /// Raised on the publishing circuit's thread. Handlers must marshal back onto their own
    /// component dispatcher (via ComponentBase.InvokeAsync) before touching component state.
    /// </summary>
    public event Action<DataChange>? Changed;

    /// <summary>
    /// Notify all subscribers of a committed change. Each handler is invoked in isolation: a
    /// throwing or torn-down subscriber (e.g. a circuit that just disconnected) must never break
    /// the writer that triggered the publish.
    /// </summary>
    public void Publish(DataChange change)
    {
        var handlers = Changed;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<DataChange>)handler)(change); }
            catch { /* a dead/faulty subscriber must not propagate into the writer */ }
        }
    }
}
