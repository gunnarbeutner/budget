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

    private void OnSuggestions(IReadOnlyList<TxCategorySuggestion> list)
    {
        foreach (var s in list)
        {
            if (s.CategoryId != null) _suggestions[s.TransactionId] = s;
        }
    }

    private async Task OnSuggestionsAppliedAsync(IReadOnlyCollection<long> ids)
    {
        foreach (var id in ids) _suggestions.Remove(id);
        await RefreshAfterEditAsync();
    }

    private async Task SuggestForRowAsync(TransactionRow row)
    {
        if (project == null) return;
        var useAi = AssistSettings.Available && await AssistSettings.GetOptInAsync();
        var result = await CategorySuggestions.SuggestAsync(ProjectId, [row.Id], useAi);
        OnSuggestions(result.Suggestions);
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
        await CategorySuggestions.ForgetAsync([row.Id]);
    }
}
