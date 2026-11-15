using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>One answer of the AI for a receipt line name (see <see cref="ProductIdentityResolver"/>).</summary>
public sealed record ProductIdentityAnswer(string Id, string ProductName, string? Brand, int? PackCount, decimal? UnitSize, ProductSizeUnit? Unit, ProductIdentityKind Kind);

/// <summary>
/// Asks the AI what the receipt line names of an owner stand for (<see cref="ReceiptProductIdentity"/>): the canonical
/// product name, brand, pack count, unit size and unit, and whether it is a product at all. Only the shop's name and
/// the line names go out, never amounts, dates or anything else of the user; in batches of <see cref="BatchSize"/>
/// distinct names, each name once per owner and shop (names that have a row of any source are skipped, so the user's
/// rows are never overwritten). Only when the server has an AI configured and the owner opted in to AI suggestions;
/// without, the price analysis falls back to its name rules. A failing request stops the run and leaves its names
/// unresolved for the next one.
/// </summary>
public sealed class ProductIdentityResolver
{
    public const int BatchSize = 50;

    public const string SchemaName = "receipt_product_identities";

    private readonly SystemDbContextFactory _system;
    private readonly IAssistClient _client;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProductIdentityResolver> _log;

    public ProductIdentityResolver(SystemDbContextFactory system, IAssistClient client, TimeProvider clock, ILogger<ProductIdentityResolver> log)
    {
        _system = system;
        _client = client;
        _clock = clock;
        _log = log;
    }

    public bool Enabled => _client.Enabled;

    public const string SystemPrompt = """
        You identify products on German supermarket and online-shop receipts. Each item is a shop and the item's name as
        printed on the receipt (often abbreviated, upper case, with sizes). For each item answer:
        - product: the canonical product name, short and human, in the receipt's language, without pack size or count
          (e.g. "Coca-Cola", "Bio Vollmilch 3,8 %", "Laugenbrötchen"). Different pack sizes of the same product get the
          same name: "COCA COLA 12X0,5L" and "Coca-Cola 1l" are both "Coca-Cola". Keep variants that are different
          products (flavour, fat content, organic) in the name. Expand abbreviations when you are sure.
        - brand: the brand when the name shows one, else null.
        - packCount: how many units the pack holds ("12x0,5l" is 12, "6er Eier" is 6), else null for one.
        - unitSize and unit: the size of one unit ("12x0,5l" is 0.5 and "l"; "500g" is 500 and "g"). Units: g, kg, ml, l,
          or piece for things counted (e.g. "10 Stück" is 10 piece with packCount null). Null when the name shows none.
        - kind: "not_a_product" for anything that is not a product: deposits (Pfand) and their returns, fees, shipping,
          delivery, service charges, gift wrap, memberships and subscriptions, vouchers, gift cards, discounts and
          coupons; else "product".
        Answer every item once, by its id. Do not invent sizes that the name does not show.
        """;

    public static JsonObject Schema() => (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["items"],
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "product", "brand", "packCount", "unitSize", "unit", "kind"],
                "properties": {
                  "id": { "type": "string" },
                  "product": { "type": "string" },
                  "brand": { "type": ["string", "null"] },
                  "packCount": { "type": ["integer", "null"] },
                  "unitSize": { "type": ["number", "null"] },
                  "unit": { "type": ["string", "null"], "enum": ["g", "kg", "ml", "l", "piece", null] },
                  "kind": { "type": "string", "enum": ["product", "not_a_product"] }
                }
              }
            }
          }
        }
        """)!;

    /// <summary>The user content: the items as JSON, each with an id ("n1", …), the shop's name and the line name.</summary>
    public static string UserContent(IReadOnlyList<(string Id, string Shop, string Name)> items) =>
        new JsonObject
        {
            ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["shop"] = ReceiptShops.DisplayName(i.Shop), ["name"] = i.Name }).ToArray()),
        }.ToJsonString();

    /// <summary>The answers of a response, one per known id; implausible fields are dropped, answers without a name skipped.</summary>
    public static List<ProductIdentityAnswer> ParseAnswer(AssistResponse response, IReadOnlySet<string> ids)
    {
        if (response.Refusal != null) throw new AssistException("The AI declined to answer.");
        if (string.IsNullOrWhiteSpace(response.Json)) throw new AssistException("The AI sent an empty answer.");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(response.Json);
        }
        catch (JsonException ex)
        {
            throw new AssistException("The AI sent an unreadable answer.", ex);
        }
        if (root?["items"] is not JsonArray list) throw new AssistException("The AI answer does not match the schema.");
        var result = new List<ProductIdentityAnswer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in list)
        {
            if (node is not JsonObject o || Str(o["id"]) is not { } id || !ids.Contains(id) || !seen.Add(id)) continue;
            if (Str(o["product"]) is not { } product) continue;
            var count = o["packCount"] is JsonValue c && c.TryGetValue<int>(out var n) && n is > 1 and <= 1000 ? n : (int?)null;
            var size = o["unitSize"] is JsonValue v && v.TryGetValue<decimal>(out var d) && d is > 0m and <= 100_000m ? d : (decimal?)null;
            ProductSizeUnit? unit = Str(o["unit"]) switch
            {
                "g" => ProductSizeUnit.Gram,
                "kg" => ProductSizeUnit.Kilogram,
                "ml" => ProductSizeUnit.Millilitre,
                "l" => ProductSizeUnit.Litre,
                "piece" => ProductSizeUnit.Piece,
                _ => null,
            };
            if (unit == null) size = null;
            var kind = Str(o["kind"]) == "not_a_product" ? ProductIdentityKind.NotAProduct : ProductIdentityKind.Product;
            result.Add(new ProductIdentityAnswer(id, Cap(product, 200), Str(o["brand"]) is { } b ? Cap(b, 100) : null, count, size, unit, kind));
        }
        return result;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string Cap(string s, int length) => s.Length > length ? s[..length] : s;

    /// <summary>The owner's distinct product line names (per shop) that have no identity yet, oldest receipts first.</summary>
    public async Task<List<(string Shop, string Name)>> UnresolvedAsync(string ownerUserId, CancellationToken ct)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        var names = await db.ReceiptItems.AsNoTracking().Where(i => i.OwnerUserId == ownerUserId && i.Kind == ReceiptItemKind.Product)
            .OrderBy(i => i.ReceiptInvoiceId).Select(i => new { i.Invoice.Shop, i.Name }).ToListAsync(ct);
        var known = (await db.ReceiptProductIdentities.AsNoTracking().Where(i => i.OwnerUserId == ownerUserId).Select(i => new { i.Shop, i.NameKey }).ToListAsync(ct))
            .Select(i => (i.Shop, i.NameKey)).ToHashSet();
        return names.Select(n => (n.Shop, n.Name, Key: PriceAnalysis.NameKey(n.Name)))
            .Where(n => n.Key.Length > 0 && !known.Contains((n.Shop, n.Key)))
            .DistinctBy(n => (n.Shop, n.Key)).Select(n => (n.Shop, n.Name)).ToList();
    }

    /// <summary>
    /// Resolves up to <paramref name="maxBatches"/> batches of the owner's unresolved names, waiting
    /// <paramref name="pause"/> between requests, and stores each batch's answers as it comes. Returns how many names
    /// were stored; stops at the first failing request.
    /// </summary>
    public async Task<int> ResolveAsync(string ownerUserId, int maxBatches, TimeSpan pause, CancellationToken ct)
    {
        if (!_client.Enabled) return 0;
        await using (var db = await _system.CreateDbContextAsync(ct))
            if (!await db.UserDataStates.AsNoTracking().AnyAsync(s => s.UserId == ownerUserId && s.UseAiSuggestions, ct)) return 0;
        var pending = await UnresolvedAsync(ownerUserId, ct);
        var stored = 0;
        var batches = 0;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            if (batches++ >= maxBatches) break;
            if (batches > 1 && pause > TimeSpan.Zero) await Task.Delay(pause, _clock, ct);
            var items = batch.Select((n, i) => ("n" + (i + 1).ToString(CultureInfo.InvariantCulture), n.Shop, n.Name)).ToList();
            List<ProductIdentityAnswer> answers;
            AssistResponse response;
            try
            {
                response = await _client.CompleteAsync(new AssistRequest(SchemaName, Schema(), SystemPrompt, UserContent(items)), ct);
                answers = ParseAnswer(response, items.Select(i => i.Item1).ToHashSet(StringComparer.Ordinal));
            }
            catch (AssistException ex)
            {
                _log.LogWarning(ex, "AI product identities failed");
                break;
            }
            _log.LogInformation("AI identified {Count} receipt product names ({Input} input tokens, {Output} output)", answers.Count, response.InputTokens, response.OutputTokens);
            stored += await StoreAsync(ownerUserId, items, answers, response.Model, ct);
        }
        return stored;
    }

    private async Task<int> StoreAsync(string ownerUserId, List<(string Id, string Shop, string Name)> items, List<ProductIdentityAnswer> answers, string model, CancellationToken ct)
    {
        var byId = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        await using var db = await _system.CreateDbContextAsync(ct);
        var shops = items.Select(i => i.Shop).Distinct(StringComparer.Ordinal).ToList();
        var keys = items.Select(i => PriceAnalysis.NameKey(i.Name)).ToList();
        var existing = (await db.ReceiptProductIdentities.Where(i => i.OwnerUserId == ownerUserId && shops.Contains(i.Shop) && keys.Contains(i.NameKey))
            .Select(i => new { i.Shop, i.NameKey }).ToListAsync(ct)).Select(i => (i.Shop, i.NameKey)).ToHashSet();
        var now = _clock.GetUtcNow().UtcDateTime;
        var stored = 0;
        foreach (var a in answers)
        {
            var item = byId[a.Id];
            var key = PriceAnalysis.NameKey(item.Name);
            if (!existing.Add((item.Shop, key))) continue;
            db.ReceiptProductIdentities.Add(new ReceiptProductIdentity
            {
                OwnerUserId = ownerUserId,
                Shop = item.Shop,
                NameKey = key,
                ProductName = a.ProductName,
                Brand = a.Brand,
                PackCount = a.PackCount,
                UnitSize = a.UnitSize,
                Unit = a.Unit,
                Kind = a.Kind,
                Source = ProductIdentitySource.Ai,
                Model = model.Length > 100 ? model[..100] : model,
                ResolvedAt = now,
            });
            stored++;
        }
        await db.SaveChangesAsync(ct);
        return stored;
    }
}

/// <summary>
/// Runs <see cref="ProductIdentityResolver"/> in the background: for an owner whenever their receipts were matched
/// (<see cref="Request"/>, after every receipt sync), and as a catch-up for every owner who opted in to AI suggestions
/// <see cref="StartDelay"/> after start and every <see cref="CatchUpInterval"/>. One owner at a time, at most
/// <see cref="MaxBatchesPerRun"/> requests per run, <see cref="Pause"/> apart; an owner with names left is asked again
/// at the next catch-up, so a large backlog is worked off in steps and a restart resumes where it stopped. Never on a
/// page load; idle without an AI.
/// </summary>
public sealed class ProductIdentityWorker : BackgroundService
{
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan CatchUpInterval = TimeSpan.FromHours(1);

    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

    public const int MaxBatchesPerRun = 20;

    private readonly ProductIdentityResolver _resolver;
    private readonly SystemDbContextFactory _system;
    private readonly PriceHistoryCache _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProductIdentityWorker> _log;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);

    public ProductIdentityWorker(ProductIdentityResolver resolver, SystemDbContextFactory system, PriceHistoryCache cache, TimeProvider clock, ILogger<ProductIdentityWorker> log)
    {
        _resolver = resolver;
        _system = system;
        _cache = cache;
        _clock = clock;
        _log = log;
    }

    /// <summary>Queues the owner's unresolved names (once while queued); does nothing without an AI.</summary>
    public void Request(string ownerUserId)
    {
        if (_resolver.Enabled && _queued.TryAdd(ownerUserId, 0)) _queue.Writer.TryWrite(ownerUserId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_resolver.Enabled) return;
        await Task.WhenAll(CatchUpAsync(stoppingToken), WorkAsync(stoppingToken));
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        await foreach (var owner in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _queued.TryRemove(owner, out _);
            try
            {
                if (await _resolver.ResolveAsync(owner, MaxBatchesPerRun, Pause, stoppingToken) > 0) _cache.Clear();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Resolving receipt product names failed");
            }
        }
    }

    private async Task CatchUpAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartDelay, _clock, ct);
            while (!ct.IsCancellationRequested)
            {
                await using (var db = await _system.CreateDbContextAsync(ct))
                    foreach (var owner in await db.UserDataStates.AsNoTracking().Where(s => s.UseAiSuggestions).Select(s => s.UserId).ToListAsync(ct))
                        Request(owner);
                await Task.Delay(CatchUpInterval, _clock, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Receipt product name catch-up stopped");
        }
    }
}
