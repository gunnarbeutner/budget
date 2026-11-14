using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Shared;

namespace OpenBudget.Pages;

/// <summary>
/// Browse's editing side: selection and in-place expansion, the action bar, context menu, category picker and note
/// panel, keyboard shortcuts, toasts with undo, and the review filters. Everything that writes goes through
/// <see cref="TransactionEditService"/>, so what the user may do is decided server-side.
/// </summary>
public partial class Browse
{
    private enum PopupKind
    {
        None,
        Picker,
        FilterPicker,
        Context,
        Note,
    }

    /// <summary>The custom range's bounds in UTC (local midnights); open while the period is a month, YTD or year.</summary>
    private (DateTime? From, DateTime? ToExclusive) PeriodBoundsUtc => CurrentPeriod is { IsRange: true } p
        ? (LocalMidnightToUtc(p.From!.Value), LocalMidnightToUtc(p.To!.Value.AddDays(1)))
        : (null, null);

    private DateTime LocalMidnightToUtc(DateOnly day)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(local, Clock.Zone); }
        catch (ArgumentException) { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
    }

    private DateOnly ToLocalDay(DateTime utc) => DateOnly.FromDateTime(Clock.ToLocal(utc));

    // ---- toolbar: filter panel ----

    private bool _filterOpen;

    /// <summary>How many of the panel's filters are set (search has its own field and is not counted).</summary>
    private int ActiveFilterCount =>
        (accountFilter.HasValue ? 1 : 0) + (amountMin.HasValue ? 1 : 0) + (amountMax.HasValue ? 1 : 0)
        + (checkedFilter != "any" ? 1 : 0) + (directionFilter != "any" ? 1 : 0)
        + (newOnly ? 1 : 0) + (categoryFilter.HasValue ? 1 : 0);

    private async Task ShowSimilarAsync(TransactionRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Name)) return;
        payeeEquals = row.Name;
        await FilterChangedAsync();
    }

    // ---- rules bar (right-click a category › Rules…) ----

    private bool _canEditRules;
    private bool _rulesOpen;
    private string? _rulesPrefill;
    private int _rulesPrefillToken;
    /// <summary>While a rule draft is edited: the bookings it matches, shown instead of the normal list.</summary>
    private IReadOnlyCollection<long>? _previewIds;
    /// <summary>"Create rule from this…" on a booking without a category: the payee, while the picker asks for the category.</summary>
    private string? _rulePickPayee;

    private long? RulesCategoryId => _rulesOpen && _canEditRules && selectionKind == "category" ? selectionId : null;

    // ---- sidebar context menu: Rules… and structure editing (SidebarStructure) ----

    private BrowseTreeRail? _rail;
    private SidebarStructure? _structure;

    private IReadOnlyList<ContextMenuItem> NodeMenu(SidebarNode node)
    {
        var structure = _structure?.Menu(node) ?? SidebarMenus.For(node, new SidebarMenuContext(_canEditRules, _ => StructureBlock.NotOwner), RailL);
        var bank = BankLogins.IsEnabled ? SidebarBankMenu.For(node, tree, _syncingLogins, RailL) : [];
        if (bank.Count == 0) return structure;
        return structure.Count == 0 ? bank : [.. bank, ContextMenuItem.Divider(), .. structure];
    }

    // ---- sidebar bank entries: Refresh account(s), Edit login data… ----

    /// <summary>The viewer's bank logins with a sync in progress (their refresh entries are disabled).</summary>
    private HashSet<int> _syncingLogins = [];

    private async Task RefreshSyncingLoginsAsync()
    {
        try
        {
            _syncingLogins = (await BankLogins.GetStatusesAsync()).Where(s => s.Phase != Connectors.Sync.SyncPhase.Finished).Select(s => s.LoginId).ToHashSet();
        }
        catch (Exception ex) when (ex is AccessDeniedException or InvalidOperationException)
        {
            _syncingLogins = [];
        }
    }

    /// <summary>A sync's status changed: refresh the busy logins without reloading the page (never a relevant change itself).</summary>
    private bool RefreshSyncingLoginsLater()
    {
        _ = InvokeAsync(async () =>
        {
            await RefreshSyncingLoginsAsync();
            StateHasChanged();
        });
        return false;
    }

    private async Task<bool> PickBankEntryAsync(SidebarNode node, string key)
    {
        if (tree == null) return false;
        switch (key)
        {
            case SidebarBankMenu.EditLogin:
                if (tree.Accounts.FirstOrDefault(a => a.Id == node.Id)?.SyncLoginId is { } loginId)
                    Navigation.NavigateTo($"connections?edit={loginId.ToString(CultureInfo.InvariantCulture)}");
                return true;
            case SidebarBankMenu.RefreshAccount:
            case SidebarBankMenu.RefreshAccounts:
                var busy = false;
                foreach (var target in SidebarBankMenu.Targets(node, tree))
                {
                    if (_syncingLogins.Contains(target.LoginId)) { busy = true; continue; }
                    var result = await BankLogins.RefreshAccountsAsync(target.LoginId, target.LoginAccountIds);
                    if (result == Connectors.Sync.StartResult.Started) _syncingLogins.Add(target.LoginId);
                    else if (result == Connectors.Sync.StartResult.AlreadyRunning) busy = true;
                }
                if (busy) AddToast(new ToastItem { Message = RailL["Ctx_Block_Syncing"], IsError = true });
                return true;
            default:
                return false;
        }
    }

    private bool CanDragNode(SidebarNode node) => _structure?.CanDrag(node) ?? false;

    private Task OnRenameNodeAsync(string? name) => _structure?.RenameAsync(name) ?? Task.CompletedTask;

    private Task OnDropNodeAsync((SidebarNode Dragged, SidebarNode Target, bool Into) drop) => _structure?.DropAsync(drop) ?? Task.CompletedTask;

    private async Task OnNodeMenuPickAsync((SidebarNode Node, string Key) pick)
    {
        if (pick.Key == SidebarMenus.Rules && pick.Node.Kind == SidebarNodeKind.Category)
        {
            await OpenRulesAsync(pick.Node.Id, null);
            return;
        }
        if (await PickBankEntryAsync(pick.Node, pick.Key)) return;
        if (_structure != null) await _structure.PickAsync(pick.Node, pick.Key);
    }

    /// <summary>The structure changed (or a rename started): refresh the tree in place; a deleted selection falls back to all.</summary>
    private async Task OnStructureChangedAsync(SidebarNode? deleted)
    {
        if (project == null) return;
        if (deleted != null && selectionId == deleted.Id && selectionKind == SelectionKindOf(deleted.Kind)) await SelectAll();
        pickerData = null;
        _leafCacheKey = null;
        _leafCache = null;
        await RefreshAfterEditAsync();
    }

    private static string SelectionKindOf(SidebarNodeKind kind) => kind switch
    {
        SidebarNodeKind.Category => "category",
        SidebarNodeKind.CategoryGroup => "group",
        SidebarNodeKind.Account => "account",
        SidebarNodeKind.AccountGroup => "accountgroup",
        _ => "",
    };

    private void OnStructureMessage(string message) => AddToast(new ToastItem { Message = message, IsError = true });

    /// <summary>Shows the category's transactions with its rules bar on top (and, with a payee, a new prefilled draft row).</summary>
    private async Task OpenRulesAsync(long categoryId, string? prefillPayee)
    {
        var reload = selectionKind != "category" || selectionId != categoryId || _previewIds != null;
        selectionKind = "category";
        selectionId = categoryId;
        ResetTransientFilters();
        CoerceChartGranularityForSelection();
        railOpen = false;
        _rulesOpen = true;
        if (prefillPayee != null)
        {
            _rulesPrefill = prefillPayee;
            _rulesPrefillToken++;
        }
        UpdateUrl();
        if (reload) await ReloadTransactionsAsync();
    }

    private async Task CloseRulesAsync()
    {
        _rulesOpen = false;
        UpdateUrl();
        if (_previewIds != null)
        {
            _previewIds = null;
            await ReloadTransactionsAsync();
        }
    }

    private async Task OnRulePreviewAsync(IReadOnlyCollection<long>? ids)
    {
        if (ids == null && _previewIds == null) return;
        _previewIds = ids;
        await ReloadTransactionsAsync();
    }

    /// <summary>"Create rule from this…": the rules bar of the booking's category with a new draft for its payee. Without a
    /// category the picker asks which category the rule should set first.</summary>
    private async Task CreateRuleFromAsync(TransactionRow row, MouseEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(row.Name) || project == null) return;
        if (row.CategoryId is { } category)
        {
            await OpenRulesAsync(category, row.Name.Trim());
            return;
        }
        pickerData ??= await LoadPickerDataAsync(project);
        _rulePickPayee = row.Name.Trim();
        popupTargets = [];
        popupCurrentCategory = null;
        (popupX, popupY) = (e.ClientX, e.ClientY);
        popup = PopupKind.Picker;
    }

    private (string Keys, string Text)[] ShortcutRows =>
    [
        ("↑ ↓", L["Key_Move"]),
        ("Shift + ↑ ↓", L["Key_Extend"]),
        ("Space", L["Key_Toggle"]),
        ("Ctrl/⌘ + A", L["Key_SelectAll"]),
        ("C", L["Key_Category"]),
        ("N", L["Key_Note"]),
        ("X", L["Key_Check"]),
        ("/", L["Key_Search"]),
        ("Esc", L["Key_Clear"]),
        ("?", L["Key_Help"]),
    ];

    // ---- state resets ----

    private void ResetSelectionState()
    {
        selected.Clear();
        viewedIds.Clear();
        rowIndex.Clear();
        rowCache.Clear();
        cursorId = anchorId = expandedId = null;
        expandedStatement = null;
        selectionSums = [];
        popup = PopupKind.None;
        popupTargets = [];
        _rulePickPayee = null;
    }

    private void ClearFilterState()
    {
        accountFilter = null;
        searchInput = ""; searchEffective = "";
        amountMin = null; amountMax = null;
        amountMinInput = ""; amountMaxInput = "";
        checkedFilter = "any"; directionFilter = "any"; newOnly = false; categoryFilter = null;
        payeeEquals = null;
    }

    /// <summary>Navigating to another node starts a fresh view unless the filters are pinned.</summary>
    private void ResetTransientFilters()
    {
        if (!pinFilters) ClearFilterState();
        payeeEquals = null;
        _rulesOpen = false;
        _previewIds = null;
    }

    private async Task<RunningBalance?> EnsureRunningBalanceAsync(CancellationToken ct)
    {
        if (runningBalance != null) return runningBalance;
        if (selectionKind == "accountgroup")
        {
            if (SelectionAccountBalance == null || GroupAccounts() is not { } members) return null;
            var parts = new List<(decimal, IEnumerable<(DateTime Date, decimal Amount)>)>();
            foreach (var m in members.Where(m => m.Balance != null))
                parts.Add((m.Balance!.Value, await TransactionQueries.GetBookedFlowsAsync(m.Id, ct)));
            runningBalance = RunningBalance.ForAccounts(parts, ToLocalDay);
            return runningBalance;
        }
        if (SelectedAccountNode is not { Balance: { } balance } account) return null;
        var flows = await TransactionQueries.GetBookedFlowsAsync(account.Id, ct);
        runningBalance = new RunningBalance(balance, flows, ToLocalDay);
        return runningBalance;
    }

    private async Task PruneSelectionAsync()
    {
        if (selected.Count == 0) return;
        var keep = await TransactionQueries.FilterIdsAsync(await BuildTransactionQueryAsync(0, 1), selected);
        selected.IntersectWith(keep);
        if (expandedId is { } e && !selected.Contains(e)) { expandedId = null; expandedStatement = null; }
        await UpdateSelectionTotalsAsync();
    }

    private async Task UpdateSelectionTotalsAsync()
    {
        selectionSums = selected.Count == 0 ? [] : (await TransactionQueries.GetSelectionTotalsAsync(selected)).Sums;
    }

    // ---- filter handlers ----

    private async Task OnCheckedFilterChanged(ChangeEventArgs e)
    {
        checkedFilter = e.Value?.ToString() ?? "any";
        await FilterChangedAsync();
    }

    private async Task OnDirectionFilterChanged(ChangeEventArgs e)
    {
        directionFilter = e.Value?.ToString() ?? "any";
        await FilterChangedAsync();
    }

    // The category filter's picker opens below the filter button; a mouse click on a browser that does not focus buttons
    // falls back to the pointer position.
    private async Task OpenFilterPickerAsync(MouseEventArgs e)
    {
        (popupX, popupY) = await JS.TryInvokeAsync<double[]?>("obFocusAnchor") is { Length: 2 } xy ? (xy[0] - 24, xy[1] + 4) : (e.ClientX, e.ClientY);
        popup = PopupKind.FilterPicker;
    }

    /// <summary>A picked category filters the list to it; "Any category" (null) clears the filter.</summary>
    private async Task PickCategoryFilterAsync(long? categoryId)
    {
        popup = PopupKind.None;
        categoryFilter = categoryId;
        await FilterChangedAsync();
    }

    private async Task ToggleNewOnly()
    {
        newOnly = !newOnly;
        await FilterChangedAsync();
    }

    private async Task TogglePin()
    {
        pinFilters = !pinFilters;
        UpdateUrl();
        await Task.CompletedTask;
    }

    private async Task FilterChangedAsync()
    {
        UpdateUrl();
        await ReloadTransactionsAsync();
    }

    // ---- selection handlers ----

    private async Task OnRowClickAsync(TxListItem item, MouseEventArgs e)
    {
        var id = item.Row.Id;
        cursorId = id;
        if (e.ShiftKey && anchorId is { } anchorRow && rowIndex.TryGetValue(anchorRow, out var anchorIndex))
        {
            await SelectRangeAsync(anchorIndex, item.Index, additive: e.CtrlKey || e.MetaKey);
        }
        else if (e.CtrlKey || e.MetaKey)
        {
            if (!selected.Remove(id)) selected.Add(id);
            anchorId = id;
            expandedId = null;
        }
        else if (selected.Count == 1 && selected.Contains(id))
        {
            // Clicking the expanded row again folds it; the selection stays.
            expandedId = expandedId == id ? null : id;
        }
        else
        {
            selected.Clear();
            selected.Add(id);
            anchorId = id;
            expandedId = id;
        }
        await AfterSelectionChangedAsync();
    }

    private async Task SelectRangeAsync(int from, int to, bool additive)
    {
        var lo = Math.Min(from, to);
        var hi = Math.Max(from, to);
        var ids = await TransactionQueries.QueryIdsAsync(await BuildTransactionQueryAsync(0, 1), lo, hi - lo + 1);
        if (!additive) selected.Clear();
        selected.UnionWith(ids);
        expandedId = null;
    }

    private async Task SelectAllAsync()
    {
        var ids = await TransactionQueries.QueryIdsAsync(await BuildTransactionQueryAsync(0, 1));
        selected.Clear();
        selected.UnionWith(ids);
        expandedId = null;
        await AfterSelectionChangedAsync();
    }

    private async Task AfterSelectionChangedAsync()
    {
        if (expandedId is { } id && selected.Count == 1 && rowCache.TryGetValue(id, out var row))
        {
            expandedStatement = row.AccountId is { } account ? await TransactionQueries.FindStatementAsync(account, row.Date) : null;
            expandedOffsetTargets = await OffsetTargetsForAsync(row);
            // Looking at a new row is what clears its marker.
            if (row.IsNew && row.CanEdit && viewedIds.Add(id)) await Edits.MarkViewedAsync([id]);
        }
        else
        {
            expandedStatement = null;
        }
        await UpdateSelectionTotalsAsync();
    }

    private async Task ClearSelection()
    {
        selected.Clear();
        expandedId = null;
        expandedStatement = null;
        selectionSums = [];
        popup = PopupKind.None;
        await Task.CompletedTask;
    }

    // ---- keyboard ----

    private async Task AttachShortcutsAsync()
    {
        selfRef ??= DotNetObjectReference.Create(this);
        try { await JS.InvokeVoidAsync("obTx.attach", selfRef); }
        catch (JSDisconnectedException) { /* circuit gone */ }
    }

    private async Task DetachShortcutsAsync()
    {
        try { await JS.InvokeVoidAsync("obTx.detach"); }
        catch (JSDisconnectedException) { /* circuit gone */ }
        catch (InvalidOperationException) { /* JS unreachable (prerender disposal) */ }
    }

    [JSInvokable]
    public async Task OnShortcut(string key, bool shift, bool ctrl)
    {
        if (project == null || tree == null || txTotals == null) return;
        switch (key)
        {
            case "ArrowDown":
                await MoveCursorAsync(1, shift);
                break;
            case "ArrowUp":
                await MoveCursorAsync(-1, shift);
                break;
            case " ":
                if (cursorId is { } c)
                {
                    if (!selected.Remove(c)) selected.Add(c);
                    anchorId = c;
                    expandedId = null;
                    await AfterSelectionChangedAsync();
                }
                break;
            case "a" or "A" when ctrl:
                await SelectAllAsync();
                break;
            case "c" or "C":
                if (selected.Count > 0) await OpenPickerAtCursorAsync();
                break;
            case "n" or "N":
                if (selected.Count > 0) await OpenNoteAtCursorAsync();
                break;
            case "x" or "X":
                if (selected.Count > 0) await ToggleCheckSelectionAsync();
                break;
            case "/":
                await JS.TryInvokeVoidAsync("obTx.focusSearch");
                break;
            case "?":
                showShortcuts = true;
                break;
            case "Escape":
                if (showShortcuts) showShortcuts = false;
                else await ClearSelection();
                break;
            default:
                break;
        }
        StateHasChanged();
    }

    private async Task MoveCursorAsync(int step, bool extend)
    {
        var current = cursorId is { } c && rowIndex.TryGetValue(c, out var i) ? i : (step > 0 ? -1 : 1);
        var target = Math.Clamp(current + step, 0, Math.Max(0, (txTotals?.Count ?? 1) - 1));
        var ids = await TransactionQueries.QueryIdsAsync(await BuildTransactionQueryAsync(0, 1), target, 1);
        if (ids.Count == 0) return;
        var id = ids[0];
        rowIndex[id] = target;
        cursorId = id;
        if (extend && anchorId is { } anchorRow && rowIndex.TryGetValue(anchorRow, out var anchorIndex))
        {
            await SelectRangeAsync(anchorIndex, target, additive: false);
        }
        else
        {
            selected.Clear();
            selected.Add(id);
            anchorId = id;
            expandedId = id;
        }
        await AfterSelectionChangedAsync();
        try { await JS.InvokeVoidAsync("obTx.reveal", id); }
        catch (JSDisconnectedException) { /* circuit gone */ }
    }

    private async Task<(double X, double Y)> CursorAnchorAsync()
    {
        try
        {
            if (cursorId is { } id && await JS.InvokeAsync<double[]?>("obTx.rowAnchor", id) is { Length: 2 } xy) return (xy[0], xy[1]);
        }
        catch (JSDisconnectedException) { /* circuit gone */ }
        return (200, 200);
    }

    // ---- popups ----

    private void ClosePopup()
    {
        popup = PopupKind.None;
        _rulePickPayee = null;
    }

    private async Task OpenPickerFromRow(TransactionRow row, MouseEventArgs e)
    {
        if (!selected.Contains(row.Id) || selected.Count <= 1)
        {
            selected.Clear();
            selected.Add(row.Id);
            anchorId = cursorId = row.Id;
            await UpdateSelectionTotalsAsync();
        }
        await OpenPickerAsync(e.ClientX, e.ClientY);
    }

    private async Task OpenPickerFromBar(MouseEventArgs e) => await OpenPickerAsync(e.ClientX, e.ClientY);

    private async Task OpenPickerAtCursorAsync()
    {
        var (x, y) = await CursorAnchorAsync();
        await OpenPickerAsync(x, y);
    }

    private async Task OpenPickerAsync(double x, double y)
    {
        if (project == null || selected.Count == 0) return;
        if (BlockedReason(selected) is { } reason) { AddToast(new ToastItem { Message = reason, IsError = true }); return; }
        pickerData ??= await LoadPickerDataAsync(project);
        popupTargets = [.. selected];
        var single = popupTargets.Count == 1 && rowCache.TryGetValue(popupTargets[0], out var r) ? r : null;
        popupCurrentCategory = single?.CategoryId;
        popupSuggestion = single != null && single.CategoryId == null && single.Amount != 0 && _suggestions.TryGetValue(single.Id, out var s) && s.Split == null ? s : null;
        (popupX, popupY) = (x, y);
        popup = PopupKind.Picker;
    }

    protected string? PopupNoteInitial =>
        popupTargets.Count == 1 && rowCache.TryGetValue(popupTargets[0], out var r) ? r.Note : null;

    private async Task OpenNote(double x, double y)
    {
        if (selected.Count == 0) return;
        if (BlockedReason(selected) is { } reason) { AddToast(new ToastItem { Message = reason, IsError = true }); return; }
        popupTargets = [.. selected];
        (popupX, popupY) = (x, y);
        popup = PopupKind.Note;
        await Task.CompletedTask;
    }

    private async Task OpenNoteAtCursorAsync()
    {
        var (x, y) = await CursorAnchorAsync();
        await OpenNote(x, y);
    }

    /// <summary>The lock explanation when every selected, already loaded row is read-only; null when at least one may be edited.</summary>
    private string? BlockedReason(HashSet<long> ids)
    {
        var known = ids.Where(rowCache.ContainsKey).Select(i => rowCache[i]).ToList();
        if (known.Count == 0 || known.Count < ids.Count || known.Any(r => r.CanEdit)) return null;
        return known.Any(r => r.EditBlock == EditBlock.MoneyMoney) ? L["Lock_MoneyMoney"] : L["Lock_NoPermission"];
    }

    private async Task OnRowContextAsync(TxListItem item, MouseEventArgs e)
    {
        if (!selected.Contains(item.Row.Id))
        {
            selected.Clear();
            selected.Add(item.Row.Id);
            anchorId = item.Row.Id;
            expandedId = null;
            await UpdateSelectionTotalsAsync();
        }
        cursorId = item.Row.Id;
        await OpenContextMenu(e.ClientX, e.ClientY);
    }

    private async Task OpenContextMenu(double x, double y)
    {
        if (selected.Count == 0) return;
        contextStatement = null;
        contextOffsetTargets = [];
        if (selected.Count == 1 && rowCache.TryGetValue(selected.First(), out var row))
        {
            if (row.AccountId is { } account) contextStatement = await TransactionQueries.FindStatementAsync(account, row.Date);
            contextOffsetTargets = await OffsetTargetsForAsync(row);
        }
        contextItems = BuildContextItems();
        (popupX, popupY) = (x, y);
        popup = PopupKind.Context;
    }

    /// <summary>The account header's menu: copy the bank details, the IBAN or the balance (entries without a value are left out).</summary>
    private void OnHeaderContext(MouseEventArgs e)
    {
        if (selectionKind != "account" || SelectedAccountNode == null) return;
        var items = new List<ContextMenuItem> { new("copy-heading", L["Ctx_Copy"], Disabled: true) };
        if (HeaderCopyText("copy-bank-details") is { } bankDetails) items.Add(new("copy-bank-details", L["Ctx_CopyBankDetails"], "bi-bank") { CopyText = bankDetails });
        if (HeaderCopyText("copy-iban") is { } iban) items.Add(new("copy-iban", L["Ctx_CopyIban"], "bi-credit-card-2-front") { CopyText = iban });
        if (HeaderCopyText("copy-balance") is { } balanceText) items.Add(new("copy-balance", L["Ctx_CopyBalance"], "bi-cash") { CopyText = balanceText });
        if (items.Count == 1) return;
        contextItems = items;
        (popupX, popupY) = (e.ClientX, e.ClientY);
        popup = PopupKind.Context;
    }

    private string? HeaderCopyText(string key)
    {
        if (SelectedAccountNode is not { } a) return null;
        return key switch
        {
            "copy-bank-details" => AccountDisplay.BankDetailsText(null, a.Iban, null, a.Institution),
            "copy-iban" => AccountDisplay.CompactIban(a.Iban),
            "copy-balance" => a.Balance is { } b ? Formatting.FormatMoneyDe(b, CurrencySums.Normalize(a.Currency)) : null,
            _ => null,
        };
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard and confirms with a short toast.</summary>
    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        bool ok;
        try { ok = await JS.InvokeAsync<bool>("obCopyText", text); }
        catch (JSDisconnectedException) { return; }
        catch (JSException) { ok = false; }
        AddToast(new ToastItem { Message = ok ? L["Toast_Copied"] : L["Toast_CopyFailed"], IsError = !ok });
    }

    private List<ContextMenuItem> BuildContextItems()
    {
        var blocked = BlockedReason(selected);
        var single = selected.Count == 1 && rowCache.TryGetValue(selected.First(), out var r) ? r : null;
        var items = new List<ContextMenuItem>
        {
            new("category", L["Ctx_SetCategory"], "bi-tag", blocked != null, blocked),
            new("remove-category", L["Ctx_RemoveCategory"], "bi-x-circle", blocked != null, blocked),
            new("note", L["Ctx_EditNote"], "bi-sticky", blocked != null, blocked),
            new("check", L["Ctx_ToggleCheck"], "bi-check2-square", blocked != null, blocked),
        };
        if (single != null)
        {
            items.Add(ContextMenuItem.Divider());
            items.Add(new("search-payee", L["Ctx_SearchPayee"], "bi-search", string.IsNullOrWhiteSpace(single.Name)));
            items.Add(new("similar", L["Ctx_Similar"], "bi-intersect", string.IsNullOrWhiteSpace(single.Name)));
            items.Add(ContextMenuItem.Divider());
            items.Add(new("go-account", L["Ctx_GoAccount"], "bi-bank", single.AccountId == null));
            // MoneyMoney's nameless root category stands for "no category", as on the row itself
            items.Add(new("go-category", L["Ctx_GoCategory"], "bi-diagram-3", single.CategoryId == null || string.IsNullOrEmpty(single.CategoryName)));
            if (contextStatement != null) items.Add(new("statement", L["Ctx_Statement"], "bi-file-earmark-pdf"));
            if (single.CanEditBooking || contextOffsetTargets.Count > 0)
                items.Add(ContextMenuItem.Divider());
            if (single.CanEditBooking)
                items.Add(new("edit-booking", L["Ctx_EditBooking"], "bi-pencil"));
            if (contextOffsetTargets.Count > 0)
            {
                items.Add(new("offset", L["Ctx_Offset"], "bi-arrow-left-right")
                {
                    Children = [.. contextOffsetTargets.Select(t => new ContextMenuItem(OffsetKeyPrefix + t.AccountId.ToString(CultureInfo.InvariantCulture), t.Name, t.Icon))],
                });
            }
            items.Add(ContextMenuItem.Divider());
            var soon = L["Ctx_ComingSoon"].Value;
            items.Add(new("rule", L["Ctx_CreateRule"], "bi-funnel", true, soon));
            items.Add(new("split", L["Ctx_Split"], "bi-diagram-2", true, soon));
        }
        return items;
    }

    private async Task OnContextPickAsync(string key)
    {
        var at = (popupX, popupY);
        popup = PopupKind.None;
        var single = selected.Count == 1 && rowCache.TryGetValue(selected.First(), out var r) ? r : null;
        switch (key)
        {
            case "copy-bank-details" or "copy-iban" or "copy-balance":
                await CopyAsync(HeaderCopyText(key));
                break;
            case "category":
                await OpenPickerAsync(at.popupX, at.popupY);
                break;
            case "remove-category":
                await CategorizeAsync([.. selected], null);
                break;
            case "note":
                await OpenNote(at.popupX, at.popupY);
                break;
            case "check":
                await ToggleCheckSelectionAsync();
                break;
            case "search-payee" when single?.Name is { } payee:
                searchInput = searchEffective = payee;
                await FilterChangedAsync();
                break;
            case "similar" when single?.Name is { } similar:
                payeeEquals = similar;
                await FilterChangedAsync();
                break;
            case "go-account" when single?.AccountId is { } accountId:
                if (tree?.Accounts.FirstOrDefault(a => a.Id == accountId) is { IsAssigned: false }) showRelatedAccounts = true;
                await SelectAccount(accountId);
                break;
            case "go-category" when single?.CategoryId is { } categoryId:
                await GoToCategoryAsync(categoryId);
                break;
            case not null when single != null && key.StartsWith(OffsetKeyPrefix, StringComparison.Ordinal)
                && long.TryParse(key.AsSpan(OffsetKeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var offsetAccount):
                await CreateOffsettingEntryAsync(single, offsetAccount);
                break;
            case "edit-booking" when single is { CanEditBooking: true }:
                await OpenEditBookingAsync(single);
                break;
            case "statement" when contextStatement is { } statement:
                await JS.TryInvokeVoidAsync("open", $"/statements/{statement.Id}", "_blank");
                break;
            default:
                break;
        }
    }

    // ---- manual bookings ----

    private bool _manualTxOpen;
    private TransactionRow? _manualTxRow;

    private async Task OpenAddBookingAsync()
    {
        if (project == null) return;
        pickerData ??= await LoadPickerDataAsync(project);
        _manualTxRow = null;
        _manualTxOpen = true;
    }

    private async Task OpenEditBookingAsync(TransactionRow row)
    {
        if (project == null) return;
        popup = PopupKind.None;
        pickerData ??= await LoadPickerDataAsync(project);
        _manualTxRow = row;
        _manualTxOpen = true;
    }

    private void CloseBookingDialog()
    {
        _manualTxOpen = false;
        _manualTxRow = null;
    }

    private async Task BookingSavedAsync(string message)
    {
        CloseBookingDialog();
        AddToast(new ToastItem { Message = message });
        selected.Clear();
        expandedId = null;
        await RefreshAfterEditAsync();
    }

    private const string OffsetKeyPrefix = "offset-";

    /// <summary>Where an offsetting entry for <paramref name="row"/> can go; none for pending bookings and split remainders.</summary>
    private async Task<List<OffsetTarget>> OffsetTargetsForAsync(TransactionRow row) =>
        row.IsPending || row.IsSplitRemainder ? [] : await ManualBookings.GetOffsetTargetsAsync(ProjectId, row.AccountId);

    /// <summary>Books the opposite of <paramref name="row"/> on a manual account; the toast leads to the new booking.</summary>
    private async Task CreateOffsettingEntryAsync(TransactionRow row, long accountId)
    {
        popup = PopupKind.None;
        try
        {
            var payee = string.IsNullOrWhiteSpace(row.Name) ? Formatting.FormatMoneyDe(row.Amount, row.Currency) : row.Name.Trim();
            var newId = await ManualBookings.CreateOffsettingEntryAsync(row.Id, accountId, L["Offset_Note", payee]);
            var accountName = expandedOffsetTargets.Concat(contextOffsetTargets).FirstOrDefault(t => t.AccountId == accountId)?.Name ?? "";
            var toast = new ToastItem { Message = L["Toast_Offset", accountName] };
            toast.Actions.Add(new ToastAction(L["Toast_ShowBooking"], () => ShowBookingAsync(toast, accountId, newId), "toast-show-booking"));
            AddToast(toast);
            await RefreshAfterEditAsync();
        }
        catch (AccessDeniedException)
        {
            AddToast(new ToastItem { Message = L["Toast_Denied"], IsError = true });
        }
        catch (InvalidOperationException ex)
        {
            AddToast(new ToastItem { Message = ex.Message, IsError = true });
        }
    }

    /// <summary>Opens the account of a booking and selects and expands it.</summary>
    private async Task ShowBookingAsync(ToastItem toast, long accountId, long transactionId)
    {
        RemoveToast(toast);
        await SelectAccount(accountId);
        selected.Clear();
        selected.Add(transactionId);
        anchorId = cursorId = transactionId;
        expandedId = transactionId;
        await AfterSelectionChangedAsync();
    }

    private async Task GoToCategoryAsync(long categoryId)
    {
        selectionKind = "category"; selectionId = categoryId;
        ResetTransientFilters();
        CoerceChartGranularityForSelection();
        UpdateUrl();
        await ReloadTransactionsAsync();
    }

    private async Task PickCategoryAsync(long? categoryId)
    {
        popup = PopupKind.None;
        if (_rulePickPayee is { } payee)
        {
            _rulePickPayee = null;
            if (categoryId is { } ruleCategory) await OpenRulesAsync(ruleCategory, payee);
            return;
        }
        await CategorizeAsync(popupTargets, categoryId);
    }

    private async Task SaveNotePanelAsync(string? text)
    {
        popup = PopupKind.None;
        await SetNoteAsync(popupTargets, text);
    }

    // ---- edits ----

    private string Plural(string key, int count) => L[count == 1 ? key + "_One" : key + "_Many", count].Value;

    private Task SetNoteForAsync(long id, string note) => SetNoteAsync([id], note);

    private Task SetCheckedForAsync(long id, bool value) => SetCheckedAsync([id], value);

    private async Task CategorizeAsync(List<long> ids, long? categoryId, bool offerSimilar = true)
    {
        if (project == null) return;
        var payee = ids.Count == 1 && rowCache.TryGetValue(ids.First(), out var row) ? row.Name : null;
        await RunEditAsync(
            () => Edits.SetCategoryAsync(ids, categoryId),
            result => Plural(categoryId == null ? "Toast_CategoryRemoved" : "Toast_CategorySet", result.Changed),
            async (toast, _) =>
            {
                if (!offerSimilar || categoryId == null || string.IsNullOrWhiteSpace(payee)) return;
                var similar = await Categorization.FindSimilarUncategorizedAsync(project.OwnerUserId, payee, ids);
                if (similar.Count > 0)
                    toast.Actions.Add(new ToastAction(L["Toast_AlsoSimilar", similar.Count], () => CategorizeAsync(similar, categoryId, offerSimilar: false), "toast-similar"));
            });
    }

    private Task SetNoteAsync(IReadOnlyCollection<long> ids, string? note) =>
        RunEditAsync(() => Edits.SetNoteAsync(ids, note), result => Plural("Toast_NoteSet", result.Changed));

    private Task SetCheckedAsync(IReadOnlyCollection<long> ids, bool value) =>
        RunEditAsync(() => Edits.SetCheckedAsync(ids, value), result => Plural(value ? "Toast_Checked" : "Toast_Unchecked", result.Changed));

    private Task ToggleCheckSelectionAsync() =>
        RunEditAsync(() => Edits.ToggleCheckedAsync([.. selected]), result => Plural("Toast_CheckToggled", result.Changed));

    private async Task MarkAllCheckedAsync()
    {
        if (project == null) return;
        var query = await BuildTransactionQueryAsync(0, 1);
        await RunEditAsync(() => Edits.MarkAllCheckedAsync(query), result => Plural("Toast_MarkedAll", result.Changed));
    }

    private async Task RunEditAsync(
        Func<Task<EditResult>> edit, Func<EditResult, string> message, Func<ToastItem, EditResult, Task>? decorate = null)
    {
        try
        {
            var result = await edit();
            if (result.Receipt == null)
            {
                AddToast(new ToastItem { Message = L["Toast_NoChange"] });
                return;
            }
            var text = message(result);
            if (result.Skipped > 0) text += " " + L["Toast_Skipped", result.Skipped];
            var toast = new ToastItem { Message = text };
            var receipt = result.Receipt;
            toast.Actions.Add(new ToastAction(L["Toast_Undo"], () => UndoAsync(receipt, toast), "toast-undo"));
            if (decorate != null) await decorate(toast, result);
            AddToast(toast);
            await RefreshAfterEditAsync();
        }
        catch (AccessDeniedException)
        {
            AddToast(new ToastItem { Message = L["Toast_Denied"], IsError = true });
        }
        catch (InvalidOperationException ex)
        {
            AddToast(new ToastItem { Message = ex.Message, IsError = true });
        }
    }

    private async Task UndoAsync(EditReceipt receipt, ToastItem toast)
    {
        RemoveToast(toast);
        try
        {
            var restored = await Edits.UndoAsync(receipt);
            AddToast(new ToastItem { Message = L["Toast_Undone", restored] });
        }
        catch (AccessDeniedException)
        {
            AddToast(new ToastItem { Message = L["Toast_Denied"], IsError = true });
        }
        await RefreshAfterEditAsync();
    }

    private async Task RefreshAfterEditAsync()
    {
        if (project == null) return;
        tree = await LoadTreeAsync() ?? tree;
        await ReloadTransactionsAsync(keepSelection: true);
        // The rows the refresh reloaded carry fresh values; the expanded row's details follow along.
        await AfterSelectionChangedAsync();
    }

    // ---- toasts ----

    private void AddToast(ToastItem toast)
    {
        toasts.Add(toast);
        while (toasts.Count > 3) RemoveToast(toasts[0]);
        var cts = new CancellationTokenSource();
        toastTimers[toast.Id] = cts;
        _ = ExpireToastAsync(toast, cts.Token);
    }

    private void RemoveToast(ToastItem toast)
    {
        toasts.Remove(toast);
        if (toastTimers.Remove(toast.Id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task ExpireToastAsync(ToastItem toast, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ToastLifetime, ct);
            await InvokeAsync(() =>
            {
                RemoveToast(toast);
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) { /* dismissed or page left */ }
        catch (ObjectDisposedException) { /* circuit gone */ }
    }

    private async Task OnToastAction((ToastItem Toast, ToastAction Action) args)
    {
        // Actions that spawn a follow-up edit remove their own toast first so the stack stays tidy.
        if (args.Action.TestId != "toast-undo") RemoveToast(args.Toast);
        await args.Action.Invoke();
    }

    private void DismissToast(ToastItem toast) => RemoveToast(toast);
}
