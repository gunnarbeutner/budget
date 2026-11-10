namespace OpenBudget.Shared;

/// <summary>What kind of sidebar row a context menu was opened on.</summary>
public enum SidebarNodeKind
{
    Category,
    CategoryGroup,
    Account,
    AccountGroup,
}

/// <summary>
/// A row of the project sidebar (<see cref="BrowseTreeRail"/>) as its context menu sees it.
/// <para>
/// Sidebar context menus: the page passes <c>NodeMenu</c>, a function returning the <see cref="ContextMenuItem"/>s for
/// a node (an empty list = no menu: no right-click, long-press or keyboard menu on that row), and handles
/// <c>OnNodeMenuPick</c> with the node and the picked item's key. Items support a label, an icon, <c>Danger</c>
/// styling, <c>Disabled</c> with a <c>Tooltip</c> giving the reason, and separators (<see cref="ContextMenuItem.Divider"/>).
/// The rail opens the menu on right-click, on a touch long-press and on Shift+F10 / the context-menu key of a
/// focused row; Esc or a click outside closes it. Alt+Arrow Up / Down on a focused row pick the
/// <see cref="SidebarMenus.MoveUp"/> / <see cref="SidebarMenus.MoveDown"/> entries when the menu has them enabled.
/// </para>
/// <para>
/// Structure editing (see <see cref="SidebarStructure"/>): the rail also renames a row inline (<c>RenamingNode</c>,
/// <c>OnRename</c>) and drags rows onto each other (<c>CanDrag</c>, <c>OnDrop</c>).
/// </para>
/// </summary>
/// <param name="HasChildren">A group with categories / sub-groups below it (only empty groups can be deleted).</param>
public sealed record SidebarNode(SidebarNodeKind Kind, long Id, string Name, bool HasChildren = false);
