using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Connectors.Scalable;

public sealed partial class ScalableConnector
{
    private const int PageSize = 50;
    private const int MaxPages = 400;

    // The status values of bookings are not documented. Known pending-like ones stay pending, known settled ones
    // are booked, anything else is booked as well but logged once so the sets can be completed.
    private static readonly HashSet<string> PendingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PENDING", "OPEN", "IN_PROGRESS", "CREATED", "SUBMITTED", "QUEUED", "PLACED", "ACCEPTED", "PARTIALLY_FILLED", "SCHEDULED", "PROCESSING",
    };

    private static readonly HashSet<string> SettledStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SETTLED", "BOOKED", "EXECUTED", "FILLED", "COMPLETED", "DONE", "CONFIRMED",
    };

    private readonly HashSet<string> _unknownStatuses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pages through the bookings (newest first) until the cursor ends or a page reaches back before <paramref name="since"/>.</summary>
    private async Task<List<RemoteTransaction>> FetchTransactionsAsync(
        string portfolioId, DateOnly? since, string query, string operation, string[] path, bool optional, CancellationToken ct)
    {
        var result = new List<RemoteTransaction>();
        try
        {
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < MaxPages; page++)
            {
                var vars = Vars(portfolioId);
                vars["input"] = new JsonObject { ["pageSize"] = PageSize, ["cursor"] = cursor };
                var data = await _graphQl.ExecuteAsync(ScalableGraphQl.BrokerPath, operation, query, vars, ct);
                var summaries = data.Path(path);
                var mapped = summaries.Path("transactions").Items().Select(MapTransaction).OfType<RemoteTransaction>().ToList();
                result.AddRange(mapped.Where(t => since == null || t.BookingDate >= since));

                cursor = summaries.Str("cursor");
                var reachedSince = since != null && mapped.Any(t => t.BookingDate < since);
                if (string.IsNullOrEmpty(cursor) || mapped.Count == 0 || reachedSince || !seenCursors.Add(cursor)) break;
            }
        }
        catch (ConnectorException ex) when (optional)
        {
            Log.LogWarning(ex, "The optional {Operation} query failed; its bookings are skipped: {Message}", operation, ex.Message);
        }
        return result;
    }

    private RemoteTransaction? MapTransaction(JsonElement t)
    {
        var amount = t.Dec("amount");
        var id = t.Str("id");
        if (amount is not { } value || !DateTimeOffset.TryParse(t.Str("lastEventDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
        {
            Log.LogDebug("Skipping a Scalable booking without amount or date.");
            return null;
        }

        var kind = t.Str("cashTransactionType") ?? t.Str("securityTransactionType") ?? t.Str("nonTradeSecurityTransactionType") ?? t.Str("type");
        var side = t.Str("side");
        var signed = side?.ToUpperInvariant() switch
        {
            "BUY" => -Math.Abs(value),
            "SELL" => Math.Abs(value),
            _ => value,
        };

        var purpose = new List<string>();
        if (t.Bool("isCancellation")) purpose.Add("Storno");
        if (kind != null) purpose.Add(kind);
        if (side != null) purpose.Add(side);
        if ((t.Dec("quantity") ?? t.Dec("eltifQuantity") ?? t.Dec("coinQuantity")) is { } quantity) purpose.Add(quantity.ToString("0.########", CultureInfo.InvariantCulture) + " x");
        if ((t.Str("isin") ?? t.Str("relatedIsin") ?? t.Str("ticker")) is { } instrument) purpose.Add(instrument);

        var date = DateOnly.FromDateTime(ToLocal(when).DateTime);
        return new RemoteTransaction(
            signed, t.Str("currency") ?? DefaultCurrency, date, date, t.Str("description") ?? kind ?? "Scalable Capital",
            purpose.Count == 0 ? null : string.Join(' ', purpose), IsBooked(t.Str("status")), id);
    }

    private bool IsBooked(string? status)
    {
        if (status == null || SettledStatuses.Contains(status)) return true;
        if (PendingStatuses.Contains(status)) return false;
        if (_unknownStatuses.Add(status))
            Log.LogInformation("Unknown Scalable booking status {Status}; treating such bookings as booked.", status.Length <= 40 ? status : status[..40]);
        return true;
    }

    /// <summary>Bookings are dated in Scalable's (German) calendar; falls back to the offset given when the time zone data is missing.</summary>
    private static DateTimeOffset ToLocal(DateTimeOffset instant)
    {
        try { return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")); }
        catch (TimeZoneNotFoundException) { return instant; }
        catch (InvalidTimeZoneException) { return instant; }
    }
}
