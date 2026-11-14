using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>
/// Asks the AI where receipt lines go that neither the item memory nor the keywords placed. The AI chooses only among
/// the receipt source's categories (its allowed categories and its default, each with the owner's description), never
/// among all of the owner's categories, so it cannot put an item into another shop's category. Only when the server
/// has an AI configured and the receipt's owner opted in to AI suggestions; each line goes as a booking of payee
/// "Supermarkt" with the product name as its purpose (nothing else of the receipt is sent), in batches. Answers below
/// <see cref="MinConfidence"/> are dropped; a failing request places nothing.
/// </summary>
public sealed class ReceiptItemAiClassifier : IReceiptItemClassifier
{
    public const double MinConfidence = 0.6;

    private readonly SystemDbContextFactory _system;
    private readonly IAssistClient _client;
    private readonly ILogger<ReceiptItemAiClassifier> _log;

    public ReceiptItemAiClassifier(SystemDbContextFactory system, IAssistClient client, ILogger<ReceiptItemAiClassifier> log)
    {
        _system = system;
        _client = client;
        _log = log;
    }

    /// <summary>The choices as prompt categories, the description appended to the path.</summary>
    public static List<PromptCategory> PromptChoices(IReadOnlyList<AllowedCategory> choices) =>
        choices.Select((c, i) => new PromptCategory(CategoryPrompt.Handle(i + 1), c.CategoryId,
            c.Description is { Length: > 0 } d ? $"{c.Path} ({d})" : c.Path, false)).ToList();

    public async Task<Dictionary<long, long>> ClassifyAsync(
        string ownerUserId, DateOnly date, IReadOnlyList<ReceiptLine> lines, IReadOnlyList<AllowedCategory> choices, CancellationToken ct)
    {
        var result = new Dictionary<long, long>();
        if (!_client.Enabled || lines.Count == 0 || choices.Count == 0) return result;
        await using var db = await _system.CreateDbContextAsync(ct);
        if (!await db.UserDataStates.AsNoTracking().AnyAsync(s => s.UserId == ownerUserId && s.UseAiSuggestions, ct)) return result;

        var prompt = PromptChoices(choices);
        var allowed = choices.Select(c => c.CategoryId).ToHashSet();
        var handles = prompt.ToDictionary(c => c.Handle, StringComparer.Ordinal);
        var names = (await db.Users.AsNoTracking().Select(u => new { u.DisplayName, u.UserName }).ToListAsync(ct))
            .SelectMany(r => new[] { r.DisplayName, r.UserName }).Where(n => !string.IsNullOrWhiteSpace(n) && n!.Length >= 3).Select(n => n!).ToList();
        var system = CategoryPrompt.SystemPrompt(prompt);

        try
        {
            foreach (var batch in lines.Chunk(CategorySuggestionService.BatchSize))
            {
                var redactor = new AssistRedactor(names);
                var items = batch.Select(l => new PromptItem(
                    redactor.Redact(new AssistBookingSource(l.Id, date, -Math.Abs(l.TotalCents), "EUR", "Supermarkt", l.Name)), [])).ToList();
                var response = await _client.CompleteAsync(
                    new AssistRequest(CategoryPrompt.SchemaName, CategoryPrompt.Schema(), system, CategoryPrompt.UserContent(items)), ct);
                _log.LogInformation("AI categorized {Count} receipt items ({Input} input tokens, {Output} output)", batch.Length, response.InputTokens, response.OutputTokens);
                foreach (var s in CategoryPrompt.ParseAnswer(response, redactor, handles))
                    if (s.CategoryId is { } category && allowed.Contains(category) && s.Confidence >= MinConfidence) result[s.TransactionId] = category;
            }
        }
        catch (AssistException ex)
        {
            _log.LogWarning(ex, "AI receipt item categories failed");
        }
        return result;
    }
}
