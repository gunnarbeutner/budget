using Microsoft.Extensions.Localization;
using OpenBudget.Models;

namespace OpenBudget.Shared;

/// <summary>The accounts of one bank login a sidebar refresh asks for (<see cref="BankLoginAccount.Id"/>s).</summary>
public sealed record LoginRefreshTarget(int LoginId, IReadOnlyList<int> LoginAccountIds);

/// <summary>
/// The bank entries of the sidebar's account and account-group menus: "Refresh account", "Edit login data…" and
/// "Refresh accounts". They only appear for accounts linked to one of the viewer's own bank logins
/// (<see cref="BrowseAccountNode.SyncLoginId"/> is set for the login's owner only).
/// </summary>
public static class SidebarBankMenu
{
    public const string RefreshAccount = "bank-refresh-account";
    public const string RefreshAccounts = "bank-refresh-accounts";
    public const string EditLogin = "bank-edit-login";

    /// <summary>
    /// The linked accounts below a sidebar row, one target per distinct bank login: the account itself, or every account of
    /// the group and its sub-groups.
    /// </summary>
    public static IReadOnlyList<LoginRefreshTarget> Targets(SidebarNode node, BrowseTree tree)
    {
        IEnumerable<BrowseAccountNode> accounts;
        if (node.Kind == SidebarNodeKind.Account)
        {
            accounts = tree.Accounts.Where(a => a.Id == node.Id);
        }
        else if (node.Kind == SidebarNodeKind.AccountGroup)
        {
            var groups = new HashSet<long> { node.Id };
            bool added;
            do
            {
                added = false;
                foreach (var g in tree.AccountGroups)
                    if (g.ParentId is { } p && groups.Contains(p) && groups.Add(g.Id)) added = true;
            }
            while (added);
            accounts = tree.Accounts.Where(a => a.GroupId is { } g && groups.Contains(g));
        }
        else
        {
            return [];
        }
        return accounts
            .Where(a => a.SyncLoginId != null && a.SyncLoginAccountId != null)
            .GroupBy(a => a.SyncLoginId!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new LoginRefreshTarget(g.Key, g.Select(a => a.SyncLoginAccountId!.Value).Distinct().ToList()))
            .ToList();
    }

    /// <summary>The bank entries of a row's menu (empty when it has none).</summary>
    /// <param name="syncing">Bank logins with a sync in progress (one sync per login at a time).</param>
    public static IReadOnlyList<ContextMenuItem> For(SidebarNode node, BrowseTree? tree, IReadOnlySet<int> syncing, IStringLocalizer<BrowseTreeRail> l)
    {
        if (tree == null) return [];
        if (node.Kind == SidebarNodeKind.Account)
        {
            if (tree.Accounts.FirstOrDefault(a => a.Id == node.Id) is not { SyncLoginId: { } loginId } account) return [];
            var busy = syncing.Contains(loginId);
            var unlisted = account.SyncLoginAccountId == null;
            return
            [
                new ContextMenuItem(RefreshAccount, l["Ctx_RefreshAccount"], "bi-arrow-repeat", busy || unlisted,
                    busy ? l["Ctx_Block_Syncing"].Value : unlisted ? l["Ctx_Block_NotListed"].Value : null),
                new ContextMenuItem(EditLogin, l["Ctx_EditLogin"], "bi-key"),
            ];
        }
        if (node.Kind == SidebarNodeKind.AccountGroup)
        {
            var targets = Targets(node, tree);
            if (targets.Count == 0)
                return [new ContextMenuItem(RefreshAccounts, l["Ctx_RefreshAccounts"], "bi-arrow-repeat", true, l["Ctx_Block_NoLinked"])];
            var allBusy = targets.All(t => syncing.Contains(t.LoginId));
            return [new ContextMenuItem(RefreshAccounts, l["Ctx_RefreshAccounts"], "bi-arrow-repeat", allBusy, allBusy ? l["Ctx_Block_Syncing"].Value : null)];
        }
        return [];
    }
}
