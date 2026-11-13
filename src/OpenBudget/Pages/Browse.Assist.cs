using Microsoft.AspNetCore.Components;
using OpenBudget.Assist;
using OpenBudget.Models;
using OpenBudget.Shared;

namespace OpenBudget.Pages;

/// <summary>
/// Browse's category suggestions: the chips on uncategorized rows (from the suggestion bar or a booking's "Suggest
/// category"), accepting and rejecting them. Accepting categorizes like a pick in the category picker.
/// </summary>
public partial class Browse
{
    [Inject] private CategorySuggestionService CategorySuggestions { get; set; } = null!;
    [Inject] private AssistSettingsService AssistSettings { get; set; } = null!;

    private readonly Dictionary<long, TxCategorySuggestion> _suggestions = [];

    /// <summary>The rule a shown suggestion could become (accept and create the rule in one step), by booking.</summary>
    private Dictionary<long, RuleSuggestion> _suggestionRules = [];

    private async Task OnSuggestions(IReadOnlyList<TxCategorySuggestion> list)
    {
        foreach (var s in list)
        {
            if (s.CategoryId != null) _suggestions[s.TransactionId] = s;
        }
        await LoadSuggestionRulesAsync();
    }

    private async Task LoadSuggestionRulesAsync()
    {
        var inline = _suggestions.Values.Where(s => s.Confidence >= SuggestionChip.InlineConfidence).ToList();
        _suggestionRules = inline.Count == 0 ? [] : await CategorySuggestions.RulesForAsync(ProjectId, inline);
    }

    /// <summary>Keeps only the suggestions of bookings still in the list (one categorized meanwhile drops out).</summary>
    private async Task PruneSuggestionsAsync()
    {
        if (_suggestions.Count == 0) return;
        var keep = await TransactionQueries.FilterIdsAsync(await BuildTransactionQueryAsync(0, 1), _suggestions.Keys.ToList());
        foreach (var id in _suggestions.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _suggestions.Remove(id);
            _suggestionRules.Remove(id);
        }
    }

    private async Task AcceptWithRuleAsync(TransactionRow row, TxCategorySuggestion suggestion, RuleSuggestion rule)
    {
        try
        {
            await CategorySuggestions.CreateRuleAndApplyAsync(ProjectId, rule);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Services.AccessDeniedException)
        {
            AddToast(new ToastItem { Message = L["Toast_Denied"], IsError = true });
            return;
        }
        await AcceptSuggestionAsync(row, suggestion);
        await LoadSuggestionRulesAsync();
    }

    private async Task OnSuggestionsAppliedAsync(IReadOnlyCollection<long> ids)
    {
        foreach (var id in ids) _suggestions.Remove(id);
        await RefreshAfterEditAsync();
        await LoadSuggestionRulesAsync();
    }

    private async Task SuggestForRowAsync(TransactionRow row)
    {
        if (project == null) return;
        var useAi = AssistSettings.Available && await AssistSettings.GetOptInAsync();
        var result = await CategorySuggestions.SuggestAsync(ProjectId, [row.Id], useAi);
        await OnSuggestions(result.Suggestions);
        if (!_suggestions.ContainsKey(row.Id))
            AddToast(new ToastItem { Message = result.AiError ?? L["Suggest_None"] });
    }

    private async Task AcceptSuggestionAsync(TransactionRow row, TxCategorySuggestion suggestion)
    {
        if (suggestion.CategoryId is not { } categoryId) return;
        try
        {
            if (suggestion.Split is { } parts) await CategorySuggestions.AcceptSplitAsync(row.Id, parts);
            else await CategorySuggestions.AcceptAsync([row.Id], categoryId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Services.AccessDeniedException)
        {
            AddToast(new ToastItem { Message = L["Toast_Denied"], IsError = true });
        }
        _suggestions.Remove(row.Id);
        await RefreshAfterEditAsync();
    }

    private async Task RejectSuggestionAsync(TransactionRow row)
    {
        _suggestions.Remove(row.Id);
        _suggestionRules.Remove(row.Id);
        await CategorySuggestions.ForgetAsync([row.Id]);
    }
}
