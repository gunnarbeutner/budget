namespace OpenBudget.Shared;

/// <summary>A button on a toast ("Undo", "Also categorize 3 similar").</summary>
public sealed record ToastAction(string Label, Func<Task> Invoke, string TestId);

/// <summary>A short-lived message with actions, shown bottom-left until dismissed or timed out.</summary>
public sealed class ToastItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Message { get; init; } = "";
    public List<ToastAction> Actions { get; } = [];
    public bool IsError { get; init; }
}
