using Microsoft.Extensions.Localization;
using OpenBudget.Services;

namespace OpenBudget.Shared;

/// <summary>What the sidebar's context menus may offer the current user.</summary>
/// <param name="CanEditProject">Editor (or owner) of the project; viewers get no editing entries at all.</param>
/// <param name="StructureBlock">Why a node's structure cannot be edited (null = it can); disabled entries show the reason.</param>
/// <param name="AccountClosed">For an account its owner may close or reopen: whether it is closed; null = no such entry.</param>
public sealed record SidebarMenuContext(bool CanEditProject, Func<SidebarNode, StructureBlock?> StructureBlock, Func<SidebarNode, bool?>? AccountClosed = null)
{
    public static SidebarMenuContext ReadOnly { get; } = new(false, _ => Services.StructureBlock.NotOwner);
}

/// <summary>
/// The entries of the sidebar's context menus (<see cref="SidebarNode"/>), shared by Transactions and the other
/// project views. Add new entries here (by node kind and access); each page handles the keys it understands.
/// </summary>
public static class SidebarMenus
{
    /// <summary>"Rules…": the category's rules bar in Transactions (editors only).</summary>
    public const string Rules = "rules";
    public const string Rename = "rename";
    public const string Color = "color";
    public const string NewCategory = "new-category";
    public const string NewGroup = "new-group";
    public const string MoveTo = "move-to";
    public const string MoveUp = "move-up";
    public const string MoveDown = "move-down";
    public const string Settings = "settings";
    public const string Delete = "delete";
    public const string CloseAccount = "close-account";
    public const string ReopenAccount = "reopen-account";

    public static IReadOnlyList<ContextMenuItem> For(SidebarNode node, SidebarMenuContext ctx, IStringLocalizer<BrowseTreeRail> l)
    {
        var items = new List<ContextMenuItem>();
        if (!ctx.CanEditProject) return items;
        if (node.Kind == SidebarNodeKind.Category)
        {
            items.Add(new ContextMenuItem(Rules, l["Ctx_Rules"], "bi-magic"));
            items.Add(ContextMenuItem.Divider());
        }

        var block = ctx.StructureBlock(node);
        string? reason = block switch
        {
            StructureBlock.MoneyMoney => l["Ctx_Block_MoneyMoney"].Value,
            StructureBlock.NotOwner => l["Ctx_Block_NotOwner"].Value,
            _ => null,
        };
        var off = reason != null;
        ContextMenuItem Item(string key, string label, string icon, bool danger = false) => new(key, label, icon, off, reason, Danger: danger);

        items.Add(Item(Rename, l["Ctx_Rename"], "bi-input-cursor-text"));
        switch (node.Kind)
        {
            case SidebarNodeKind.Category:
                items.Add(Item(Color, l["Ctx_Color"], "bi-palette"));
                items.Add(Item(NewCategory, l["Ctx_NewSubcategory"], "bi-plus-lg"));
                break;
            case SidebarNodeKind.CategoryGroup:
                items.Add(Item(NewCategory, l["Ctx_NewCategory"], "bi-plus-lg"));
                items.Add(Item(NewGroup, l["Ctx_NewGroup"], "bi-folder-plus"));
                break;
            case SidebarNodeKind.Account:
                // Always open: the statement settings work on MoneyMoney's accounts too (the name field shows the reason).
                items.Add(new ContextMenuItem(Settings, l["Ctx_AccountSettings"], "bi-gear"));
                if (ctx.AccountClosed?.Invoke(node) is { } closed)
                    items.Add(closed
                        ? new ContextMenuItem(ReopenAccount, l["Ctx_ReopenAccount"], "bi-box-arrow-up")
                        : new ContextMenuItem(CloseAccount, l["Ctx_CloseAccount"], "bi-archive"));
                break;
            case SidebarNodeKind.AccountGroup:
                // Icon and colour survive MoneyMoney uploads, so only other users' groups block the settings.
                items.Add(block == StructureBlock.NotOwner
                    ? Item(Settings, l["Ctx_GroupSettings"], "bi-gear")
                    : new ContextMenuItem(Settings, l["Ctx_GroupSettings"], "bi-gear"));
                items.Add(Item(NewGroup, l["Ctx_NewGroup"], "bi-folder-plus"));
                break;
            default:
                break;
        }
        items.Add(Item(MoveTo, l["Ctx_MoveTo"], "bi-folder-symlink"));
        items.Add(Item(MoveUp, l["Ctx_MoveUp"], "bi-arrow-up"));
        items.Add(Item(MoveDown, l["Ctx_MoveDown"], "bi-arrow-down"));
        items.Add(ContextMenuItem.Divider());
        if (node.Kind == SidebarNodeKind.CategoryGroup && node.HasChildren && !off)
            items.Add(new ContextMenuItem(Delete, l["Ctx_Delete"], "bi-trash", true, l["Ctx_Block_NotEmpty"], Danger: true));
        else
            items.Add(Item(Delete, node.Kind == SidebarNodeKind.Account ? l["Ctx_DeleteAccount"] : l["Ctx_Delete"], "bi-trash", danger: true));
        return items;
    }
}
