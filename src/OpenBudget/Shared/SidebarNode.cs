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
/// focused row; Esc or a click outside closes it.
/// </para>
/// </summary>
public sealed record SidebarNode(SidebarNodeKind Kind, long Id, string Name);
