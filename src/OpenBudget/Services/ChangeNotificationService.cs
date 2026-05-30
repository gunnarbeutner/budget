using System.Runtime.InteropServices;

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
[StructLayout(LayoutKind.Auto)]
public readonly record struct DataChange(DataArea Area, int? ProjectId = null, int? SourceId = null);

/// <summary>
/// Per-circuit identity stamped onto the changes a circuit publishes. A component compares an
/// incoming change's origin against its own circuit's id so it can ignore the echo of its own
/// commits — it already updated its view inline — while still reacting to changes made by other
/// sessions. Registered scoped, so there is exactly one instance per Blazor circuit, shared by
/// that circuit's components and the scoped data services they call.
/// </summary>
public sealed class ChangeOrigin
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>
/// In-process pub/sub that lets one Blazor circuit tell every other circuit "this data changed,
/// reload it". The app runs as a single process, so an in-memory event is sufficient — no Redis
/// or DB polling. Registered as a singleton; writers call <see cref="Publish"/> after a
/// successful commit and components subscribe to <see cref="Changed"/> for the lifetime of the
/// page (unsubscribing on dispose).
/// </summary>
public sealed class ChangeNotificationService
{
    private readonly ILogger<ChangeNotificationService>? _logger;

    public ChangeNotificationService(ILogger<ChangeNotificationService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Raised on the publishing circuit's thread. The second argument is the <see cref="ChangeOrigin"/>
    /// id of the circuit that published the change, or null for changes with no circuit origin
    /// (e.g. an API upload/delete). Handlers must marshal back onto their own component dispatcher
    /// (via ComponentBase.InvokeAsync) before touching component state.
    /// </summary>
    public event Action<DataChange, Guid?>? Changed;

    /// <summary>
    /// Notify all subscribers of a committed change. <paramref name="origin"/> is the publishing
    /// circuit's <see cref="ChangeOrigin"/> id (null for changes not tied to a circuit) so a
    /// subscriber can skip the echo of its own commit. Each handler is invoked in isolation: a
    /// throwing or torn-down subscriber (e.g. a circuit that just disconnected) must never break
    /// the writer that triggered the publish.
    /// </summary>
    public void Publish(DataChange change, Guid? origin = null)
    {
        var handlers = Changed;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            // A dead/faulty subscriber must not propagate into the writer, but log it: a swallowed
            // exception here usually means a handler (not just a torn-down circuit) has a bug.
            try { ((Action<DataChange, Guid?>)handler)(change, origin); }
            catch (Exception ex) { _logger?.LogWarning(ex, "A change-notification handler threw for {Change}.", change); }
        }
    }
}
