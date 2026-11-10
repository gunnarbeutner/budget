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
        Context,
        Note,
    }

    private IReadOnlyList<PeriodChoice> PeriodChoices => PeriodFilter.Choices(DateOnly.FromDateTime(Clock.Today));

    private (DateTime? From, DateTime? ToExclusive) PeriodBoundsUtc
    {
        get
        {
            var (from, to) = PeriodFilter.Resolve(periodFilter, DateOnly.FromDateTime(Clock.Today), customFrom, customTo);
            return (from is { } f ? LocalMidnightToUtc(f) : null, to is { } t ? LocalMidnightToUtc(t) : null);
        }
    }

    private DateTime LocalMidnightToUtc(DateOnly day)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(local, Clock.Zone); }
        catch (ArgumentException) { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
    }

    private DateOnly ToLocalDay(DateTime utc) => DateOnly.FromDateTime(Clock.ToLocal(utc));

    private static bool IsKnownPeriodKey(string key) =>
        key is "all" or "30d" or "90d" or "12m" or "custom"
        || PeriodFilter.Resolve(key, new DateOnly(2000, 1, 1)) is { From: not null };

    private string PeriodLabel(PeriodChoice choice) => choice.Kind switch
    {
        "all" => L["Period_All"],
        "days" => L["Period_Days", choice.Number],
        "months" => L["Period_Months", choice.Number],
        "month" => new DateTime(choice.Year, choice.Number, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture),
        "quarter" => L["Period_Quarter", choice.Number, choice.Year],
        "year" => choice.Year.ToString(CultureInfo.InvariantCulture),
        _ => L["Period_Custom"],
    };

    // ---- toolbar: filter panel, documents, category color ----

    private bool _filterOpen;
    private int _statementCount;
    private bool _canEditColors;
    private int _colorMenuVersion;

    /// <summary>How many of the panel's filters are set (search has its own field and is not counted).</summary>
    private int ActiveFilterCount =>
        (accountFilter.HasValue ? 1 : 0) + (amountMin.HasValue ? 1 : 0) + (amountMax.HasValue ? 1 : 0)
        + (periodFilter != PeriodFilter.All ? 1 : 0) + (checkedFilter != "any" ? 1 : 0) + (directionFilter != "any" ? 1 : 0)
        + (newOnly ? 1 : 0) + (categoryFilter.HasValue ? 1 : 0);

    private async Task LoadToolbarExtrasAsync()
    {
        _statementCount = 0;
        if (SelectedAccountNode is { } account)
        {
            try { _statementCount = (await BankLogins.GetAccountStatementsAsync(account.Id)).Count; }
            catch (AccessDeniedException) { _statementCount = 0; }
            catch (InvalidOperationException) { _statementCount = 0; }
        }
        _canEditColors = project != null && selectionKind == "category" && await CategoryColorEdits.CanEditAsync(project.SourceId);
    }

    private async Task SetCategoryColorAsync(long categoryId, string? color)
    {
        if (!await CategoryColorEdits.SetColorAsync(categoryId, color)) return;
        _colorMenuVersion++;   // re-creates the <details>, which closes it
        await ReloadAsync();
    }

    private async Task ShowSimilarAsync(TransactionRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Name)) return;
        payeeEquals = row.Name;
        await FilterChangedAsync();
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
        selectionSum = 0;
        popup = PopupKind.None;
        popupTargets = [];
    }

    private void ClearFilterState()
    {
        accountFilter = null;
        searchInput = ""; searchEffective = "";
        amountMin = null; amountMax = null;
        amountMinInput = ""; amountMaxInput = "";
        periodFilter = PeriodFilter.All; customFrom = null; customTo = null;
        checkedFilter = "any"; directionFilter = "any"; newOnly = false; categoryFilter = null;
        payeeEquals = null;
    }

    /// <summary>Navigating to another node starts a fresh view unless the filters are pinned.</summary>
    private void ResetTransientFilters()
    {
        if (!pinFilters) ClearFilterState();
        payeeEquals = null;
    }

    private async Task<RunningBalance?> EnsureRunningBalanceAsync(CancellationToken ct)
    {
        if (runningBalance != null) return runningBalance;
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
        selectionSum = selected.Count == 0 ? 0m : (await TransactionQueries.GetSelectionTotalsAsync(selected)).Sum;
    }

    // ---- filter handlers ----

    private async Task OnPeriodFilterChanged(ChangeEventArgs e)
    {
        periodFilter = e.Value?.ToString() ?? PeriodFilter.All;
        await FilterChangedAsync();
    }

    private async Task OnCustomDateChanged(ChangeEventArgs e, bool isFrom)
    {
        DateOnly? value = DateOnly.TryParseExact(e.Value?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        if (isFrom) customFrom = value; else customTo = value;
        await FilterChangedAsync();
    }

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

    private async Task OnCategoryFilterChanged(ChangeEventArgs e)
    {
        categoryFilter = long.TryParse(e.Value?.ToString(), CultureInfo.InvariantCulture, out var id) ? id : null;
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
        selectionSum = 0;
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
                await JS.InvokeVoidAsync("obTx.focusSearch");
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

    private void ClosePopup() => popup = PopupKind.None;

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
        pickerData ??= await Categorization.GetPickerDataAsync(project.SourceId);
        popupTargets = [.. selected];
        var single = popupTargets.Count == 1 && rowCache.TryGetValue(popupTargets[0], out var r) ? r : null;
        popupCurrentCategory = single?.CategoryId;
        popupSuggestions = await Categorization.GetSuggestionsAsync(project.SourceId, single?.Name, pickerData);
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
        if (selected.Count == 1 && rowCache.TryGetValue(selected.First(), out var row) && row.AccountId is { } account)
            contextStatement = await TransactionQueries.FindStatementAsync(account, row.Date);
        contextItems = BuildContextItems();
        (popupX, popupY) = (x, y);
        popup = PopupKind.Context;
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
            items.Add(new("go-category", L["Ctx_GoCategory"], "bi-diagram-3", single.CategoryId == null));
            if (contextStatement != null) items.Add(new("statement", L["Ctx_Statement"], "bi-file-earmark-pdf"));
            items.Add(ContextMenuItem.Divider());
            var soon = L["Ctx_ComingSoon"].Value;
            items.Add(new("rule", L["Ctx_CreateRule"], "bi-funnel", true, soon));
            items.Add(new("split", L["Ctx_Split"], "bi-diagram-2", true, soon));
            items.Add(new("transfer", L["Ctx_Transfer"], "bi-arrow-left-right", true, soon));
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
            case "statement" when contextStatement is { } statement:
                await JS.InvokeVoidAsync("open", $"/statements/{statement.Id}", "_blank");
                break;
            default:
                break;
        }
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
                var similar = await Categorization.FindSimilarUncategorizedAsync(project.SourceId, payee, ids);
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
        var (fm, tm) = PeriodRange;
        tree = await BrowseService.GetTreeAsync(ProjectId, anchor.Year, fm, tm) ?? tree;
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
