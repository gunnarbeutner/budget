using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>
/// Category suggestions from online orders: a booking matched to order charges (see <see cref="AmazonOrderStore"/>) gets
/// a category per item, from items the owner categorized before (the same product first, then similar titles), and
/// from the AI for the rest when it may be asked. One category for all items is suggested for the booking; different
/// ones become a split whose amounts are the item prices scaled to the booking (see <see cref="AmazonSplit.Proportion"/>).
/// Instalments of an order get the category of most of the order's value.
/// </summary>
public sealed partial class CategorySuggestionService
{
    private const int MaxLabel = 200;

    private sealed record StoredSplit(List<SuggestedSplitPart> Parts, bool Unclear);

    private sealed record ItemEvidence(long ItemId, string Title, string? Asin, long CategoryId, long Line);

    private sealed record ItemVote(long CategoryId, double Confidence);

    private static StoredSplit? ReadSplit(string json)
    {
        try { return JsonSerializer.Deserialize<StoredSplit>(json); }
        catch (JsonException) { return null; }
    }

    private static string WriteSplit(TxCategorySuggestion s) =>
        JsonSerializer.Serialize(new StoredSplit(s.Split?.ToList() ?? [], s.ShipmentUnclear));

    /// <summary>Order suggestions for <paramref name="targets"/>, written into <paramref name="results"/>; returns (sent to the AI, skipped without consent).</summary>
    private async Task<(int Sent, int Skipped)> OrderSuggestionsAsync(
        Context ctx, List<Transaction> targets, bool useAi, Dictionary<long, TxCategorySuggestion> results, CancellationToken ct)
    {
        await using var sys = await _system.CreateDbContextAsync(ct);
        var charges = (await sys.AmazonCharges.AsNoTracking()
                .Where(c => c.OwnerUserId == ctx.OwnerId && c.TransactionId != null && c.State == AmazonChargeState.Matched).ToListAsync(ct))
            .Where(c => ctx.Rows.ContainsKey(c.TransactionId!.Value)).ToList();
        if (charges.Count == 0) return (0, 0);
        var byBooking = await AmazonOrderStore.ItemsByBookingAsync(sys, ctx.OwnerId, charges, ct);

        // Evidence: items the owner gave a category through a split, and the items of categorized bookings.
        var evidence = new Dictionary<long, ItemEvidence>();
        foreach (var i in await sys.AmazonOrderItems.AsNoTracking().Where(i => i.OwnerUserId == ctx.OwnerId && i.CategoryId != null).ToListAsync(ct))
            if (ctx.ById.ContainsKey(i.CategoryId!.Value))
                evidence[i.Id] = new ItemEvidence(i.Id, i.Title, i.Asin, i.CategoryId!.Value, (i.PriceCents ?? 0) * i.Quantity);
        foreach (var (bookingId, paid) in byBooking)
        {
            if (ctx.Rows[bookingId].CategoryId is not { } category || !ctx.ById.ContainsKey(category)) continue;
            foreach (var i in paid.Items) evidence.TryAdd(i.Id, new ItemEvidence(i.Id, i.Title, null, category, i.LineCents ?? 0));
        }
        var asinRows = await sys.AmazonOrderItems.AsNoTracking().Where(i => evidence.Keys.Contains(i.Id) && i.Asin != null)
            .Select(i => new { i.Id, i.Asin }).ToListAsync(ct);
        var byAsin = asinRows.GroupBy(a => a.Asin!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.GroupBy(a => evidence[a.Id].CategoryId).OrderByDescending(x => x.Count()).First().Key, StringComparer.Ordinal);
        var itemAsins = (await sys.AmazonOrderItems.AsNoTracking()
                .Where(i => i.OwnerUserId == ctx.OwnerId && i.Asin != null).Select(i => new { i.Id, i.Asin }).ToListAsync(ct))
            .ToDictionary(i => i.Id, i => i.Asin!);
        var matching = new CategoryMatching(evidence.Values.Select(e =>
            new HistoryBooking(e.ItemId, e.CategoryId, null, "", CategoryMatching.Tokens(e.Title, ""), -Math.Max(1, e.Line))));

        var votes = new Dictionary<long, ItemVote>();
        var unsure = new List<(Transaction Row, SplitItem Item, List<Neighbour> Neighbours)>();
        foreach (var t in targets)
        {
            if (!byBooking.TryGetValue(t.Id, out var paid)) continue;
            foreach (var item in paid.Items)
            {
                if (votes.ContainsKey(item.Id)) continue;
                var neighbours = matching.Neighbours(new TargetBooking(item.Id, null, "", CategoryMatching.Tokens(item.Title, ""), -1));
                ItemVote? vote = null;
                if (item.CategoryId is { } own && ctx.ById.ContainsKey(own)) vote = new ItemVote(own, 0.99);
                else if (itemAsins.TryGetValue(item.Id, out var asin) && byAsin.TryGetValue(asin, out var sameProduct)) vote = new ItemVote(sameProduct, 0.95);
                else if (CategoryMatching.KnnVote(neighbours) is { } knn) vote = new ItemVote(knn.CategoryId, knn.Confidence);
                if (vote != null) votes[item.Id] = vote;
                if (vote == null || vote.Confidence < SureEnough) unsure.Add((t, item, neighbours));
            }
        }

        var sent = 0;
        var skipped = 0;
        if (useAi && _client.Enabled && unsure.Count > 0)
        {
            if (!(await AllowedOwnersAsync(ct)).Contains(ctx.OwnerId)) skipped = unsure.Select(u => u.Row.Id).Distinct().Count();
            else sent = await AskAiForItemsAsync(ctx, unsure, evidence, votes, ct);
        }

        foreach (var t in targets)
        {
            if (!byBooking.TryGetValue(t.Id, out var paid) || paid.Items.Count == 0) continue;
            if (paid.Items.Any(i => !votes.ContainsKey(i.Id))) continue;
            if (OrderSuggestion(ctx, t, paid, votes) is { } suggestion) results[t.Id] = suggestion;
        }
        return (sent, skipped);
    }

    /// <summary>One booking's suggestion from its items' categories (see the class remarks).</summary>
    private static TxCategorySuggestion? OrderSuggestion(Context ctx, Transaction t, BookingItems paid, Dictionary<long, ItemVote> votes)
    {
        var confidence = paid.Items.Min(i => votes[i.Id].Confidence) * (paid.Unclear ? 0.9 : 1);
        var groups = paid.Items.GroupBy(i => votes[i.Id].CategoryId).ToList();
        var rationale = $"{paid.Items.Count} items of order {string.Join(", ", paid.OrderIds.Distinct(StringComparer.Ordinal))}";
        TxCategorySuggestion Single(long category) =>
            new(t.Id, category, ctx.ById[category].Path, Math.Round(confidence, 2), "order", rationale) { ShipmentUnclear = paid.Unclear };
        if (groups.Count == 1) return Single(groups[0].Key);
        var weights = groups.Select(g => g.All(i => i.LineCents != null) ? g.Sum(i => i.LineCents) : null).ToList();
        if (paid.Instalment)
        {
            var main = groups.Zip(weights).OrderByDescending(x => x.Second ?? 0).First().First.Key;
            return Single(main);
        }
        var amounts = AmazonSplit.Proportion(t.SplitOriginalAmount ?? t.Amount, weights);
        var parts = groups.Select((g, i) => new SuggestedSplitPart(amounts[i], g.Key, ctx.ById[g.Key].Path,
                Truncate(AmazonSplit.Describe(g.Select(x => (x.Title, x.Quantity))), MaxLabel), g.Select(x => x.Id).ToList()))
            .Where(p => p.AmountCents != 0).ToList();
        if (parts.Count < 2) return Single(parts.Count == 1 ? parts[0].CategoryId : groups[0].Key);
        var largest = parts.OrderByDescending(p => Math.Abs(p.AmountCents)).First();
        return new TxCategorySuggestion(t.Id, largest.CategoryId, largest.CategoryPath, Math.Round(confidence, 2), "order", rationale)
        {
            Split = parts,
            ShipmentUnclear = paid.Unclear,
        };
    }

    /// <summary>Asks the AI for the categories of items (each sent like a booking: payee "Amazon", the title as purpose).</summary>
    private async Task<int> AskAiForItemsAsync(
        Context ctx, List<(Transaction Row, SplitItem Item, List<Neighbour> Neighbours)> items, Dictionary<long, ItemEvidence> evidence,
        Dictionary<long, ItemVote> votes, CancellationToken ct)
    {
        var system = CategoryPrompt.SystemPrompt(ctx.Categories, []);
        var handles = ctx.Categories.ToDictionary(c => c.Handle, StringComparer.Ordinal);
        var names = await _settings.PersonNamesAsync(ct);
        static AssistBookingSource Source(long id, Transaction row, string title, long? line) =>
            new(-id, DateOnly.FromDateTime(row.Date), -Math.Abs(line ?? 0), row.Currency, "Amazon", title);
        foreach (var batch in items.Chunk(BatchSize))
        {
            var redactor = new AssistRedactor(names);
            var prompt = batch.Select(b => new PromptItem(
                redactor.Redact(Source(b.Item.Id, b.Row, b.Item.Title, b.Item.LineCents)),
                b.Neighbours.Take(3).Where(n => evidence.ContainsKey(n.Booking.Id)).Select(n => (
                    redactor.Redact(Source(n.Booking.Id, b.Row, evidence[n.Booking.Id].Title, evidence[n.Booking.Id].Line)),
                    ctx.ById[n.Booking.CategoryId].Handle)).ToList())).ToList();
            var response = await _client.CompleteAsync(new AssistRequest(CategoryPrompt.SchemaName, CategoryPrompt.Schema(), system, CategoryPrompt.UserContent(prompt)), ct);
            _log.LogInformation("AI categorized {Count} order items ({Input} input tokens, {Output} output)", batch.Length, response.InputTokens, response.OutputTokens);
            foreach (var s in CategoryPrompt.ParseAnswer(response, redactor, handles))
            {
                if (s.CategoryId is not { } category) continue;
                var itemId = -s.TransactionId;
                if (votes.TryGetValue(itemId, out var existing) && existing.Confidence >= s.Confidence) continue;
                votes[itemId] = new ItemVote(category, s.Confidence);
            }
        }
        return items.Select(i => i.Row.Id).Distinct().Count();
    }

    /// <summary>
    /// Splits the booking as suggested (each part noted with its items) and remembers the items' categories as evidence
    /// for later orders. Throws like <see cref="SplitService.SplitAsync"/> (a pending booking cannot be split yet).
    /// </summary>
    public async Task AcceptSplitAsync(long transactionId, IReadOnlyList<SuggestedSplitPart> parts, CancellationToken ct = default)
    {
        var splits = _splits ?? throw new InvalidOperationException("Splitting is not available.");
        await splits.SplitAsync(transactionId, parts.Select(p => new SplitPartInput(p.AmountCents, p.CategoryId, p.Label)).ToList(), ct);
        await using var sys = await _system.CreateDbContextAsync(ct);
        var owner = await sys.Transactions.AsNoTracking().Where(t => t.Id == transactionId).Select(t => t.OwnerUserId).FirstOrDefaultAsync(ct);
        foreach (var part in parts)
        {
            var ids = part.ItemIds.ToList();
            await sys.AmazonOrderItems.Where(i => ids.Contains(i.Id) && i.OwnerUserId == owner)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.CategoryId, part.CategoryId), ct);
        }
        await ForgetAsync([transactionId], ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
