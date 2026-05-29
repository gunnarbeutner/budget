using Microsoft.AspNetCore.Components;
using OpenBudget.Services;

namespace OpenBudget.Shared;

/// <summary>
/// Base for pages that show DB-backed data and need to react when another browser session
/// changes that data. Subscribes to <see cref="ChangeNotificationService"/> for the component's
/// lifetime (unsubscribing on dispose) and, on a relevant change, either reloads in place
/// (read-only views) or raises a banner the user can act on (editors, where an auto-reload would
/// discard in-progress edits).
///
/// Derived pages implement <see cref="IsRelevantChange"/> (filter by area + project/source) and
/// <see cref="ReloadAfterChangeAsync"/> (re-fetch their data). Editors override
/// <see cref="AutoReloadOnChange"/> to false and render a banner bound to
/// <see cref="ExternalChangePending"/> / <see cref="ApplyPendingChangeAsync"/>.
/// </summary>
public abstract class DataChangeAwareComponent : ComponentBase, IDisposable
{
    [Inject] protected ChangeNotificationService ChangeNotifier { get; set; } = default!;

    /// <summary>
    /// True when a relevant change arrived that we deliberately did not auto-apply (editor
    /// pages). Bind a "data changed elsewhere — reload" banner to this.
    /// </summary>
    protected bool ExternalChangePending { get; private set; }

    private bool _disposed;

    protected override void OnInitialized()
    {
        ChangeNotifier.Changed += OnExternalChange;
        base.OnInitialized();
    }

    private void OnExternalChange(DataChange change)
    {
        if (_disposed || !IsRelevantChange(change)) return;
        // The event fires on the *publishing* circuit's thread; hop back onto this component's
        // dispatcher before touching its state or calling StateHasChanged.
        _ = InvokeAsync(async () =>
        {
            // The circuit may have been torn down while this was queued; touching a disposed
            // component's state or calling StateHasChanged on it would throw.
            if (_disposed) return;
            if (AutoReloadOnChange)
            {
                await ReloadAfterChangeAsync();
            }
            else
            {
                ExternalChangePending = true;
            }
            StateHasChanged();
        });
    }

    /// <summary>Banner click handler: apply the pending change now.</summary>
    protected async Task ApplyPendingChangeAsync()
    {
        ExternalChangePending = false;
        await ReloadAfterChangeAsync();
        StateHasChanged();
    }

    /// <summary>Read-only views leave this true (reload immediately); editors override to false.</summary>
    protected virtual bool AutoReloadOnChange => true;

    /// <summary>Does this change affect what the page is currently showing?</summary>
    protected abstract bool IsRelevantChange(DataChange change);

    /// <summary>Re-fetch the page's data (or force a fresh navigation).</summary>
    protected abstract Task ReloadAfterChangeAsync();

    public virtual void Dispose()
    {
        _disposed = true;
        ChangeNotifier.Changed -= OnExternalChange;
    }
}
