namespace OpenBudget.Shared;

/// <summary>An entry of <see cref="ContextMenu"/>. A separator has no key; a disabled entry shows its tooltip (e.g. "coming soon").</summary>
public sealed record ContextMenuItem(string Key, string Label, string? Icon = null, bool Disabled = false, string? Tooltip = null, bool Separator = false)
{
    public static ContextMenuItem Divider() => new("", "", Separator: true);
}
