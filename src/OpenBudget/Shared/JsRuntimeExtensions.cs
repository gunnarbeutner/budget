using Microsoft.JSInterop;

namespace OpenBudget.Shared;

/// <summary>
/// JS calls for UI touches (focus, placement, scrolling, opening a picker) that have nothing left to do once the
/// browser connection is gone: a lost circuit or a cancelled call is ignored instead of thrown.
/// </summary>
public static class JsRuntimeExtensions
{
    public static async Task TryInvokeVoidAsync(this IJSRuntime js, string identifier, params object?[]? args)
    {
        try { await js.InvokeVoidAsync(identifier, args); }
        catch (JSDisconnectedException) { /* the page is gone */ }
        catch (OperationCanceledException) { /* the call was cancelled with the circuit */ }
    }

    /// <summary>The call's result, or the default when the connection is gone.</summary>
    public static async Task<T?> TryInvokeAsync<T>(this IJSRuntime js, string identifier, params object?[]? args)
    {
        try { return await js.InvokeAsync<T>(identifier, args); }
        catch (JSDisconnectedException) { return default; }
        catch (OperationCanceledException) { return default; }
    }
}
