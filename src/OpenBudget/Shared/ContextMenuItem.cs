namespace OpenBudget.Shared;

/// <summary>
/// An entry of <see cref="ContextMenu"/>. A separator has no key; a disabled entry shows its tooltip (e.g. "coming soon").
/// An entry with <see cref="Children"/> opens them as a submenu instead of being picked itself.
/// </summary>
public sealed record ContextMenuItem(string Key, string Label, string? Icon = null, bool Disabled = false, string? Tooltip = null, bool Separator = false, bool Danger = false)
{
    public IReadOnlyList<ContextMenuItem>? Children { get; init; }

    /// <summary>Text the browser copies on the click itself (Safari allows the clipboard only there); see obCopyText.</summary>
    public string? CopyText { get; init; }

    public static ContextMenuItem Divider() => new("", "", Separator: true);
}
