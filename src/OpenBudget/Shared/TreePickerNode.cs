namespace OpenBudget.Shared;

/// <summary>
/// One row in a <see cref="TreePicker{TId}"/>. Callers produce a flat depth-first list of
/// nodes — the picker just renders them in order with <see cref="Level"/> driving the
/// indent. Rows with <see cref="Selectable"/> = false render as non-clickable headers
/// (typically used for organizational groups in a tree of selectable leaves).
/// <see cref="Icon"/> is an optional Bootstrap Icons class (e.g. <c>"bi-bank"</c>)
/// rendered before the label.
/// </summary>
public record TreePickerNode<TId>(TId Id, int Level, bool Selectable, string Label, string? Icon = null);
