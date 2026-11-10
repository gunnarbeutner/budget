using Microsoft.Extensions.Localization;

namespace OpenBudget.Shared;

/// <summary>
/// The entries of the sidebar's context menus (<see cref="SidebarNode"/>), shared by Transactions and the other
/// project views. Add new entries here (by node kind and access); each page handles the keys it understands.
/// </summary>
public static class SidebarMenus
{
    /// <summary>"Rules…": the category's rules bar in Transactions (editors only).</summary>
    public const string Rules = "rules";

    public static IReadOnlyList<ContextMenuItem> For(SidebarNode node, bool canEdit, IStringLocalizer<BrowseTreeRail> l)
    {
        var items = new List<ContextMenuItem>();
        if (node.Kind == SidebarNodeKind.Category && canEdit)
            items.Add(new ContextMenuItem(Rules, l["Ctx_Rules"], "bi-magic"));
        return items;
    }
}
