using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using OpenBudget.Connectors;
using OpenBudget.Connectors.LidlPlus;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// Lidl's token endpoint and ticket API over made-up receipts. Refresh tokens rotate with every use (the old one stops
/// working); an authorization code is accepted once, with the verifier of the challenge the sign-in page was opened with.
/// </summary>
internal sealed class FakeLidlPlus
{
    public static readonly LidlPlusOptions Options = new()
    {
        AuthBase = new Uri("https://accounts.lidl.test/"),
        TicketsBase = new Uri("https://tickets.lidl.test/api/"),
        RequestDelay = TimeSpan.Zero,
    };

    private readonly Lock _gate = new();
    private int _generation;

    /// <summary>The refresh token Lidl accepts now (null: none).</summary>
    public string? CurrentRefresh { get; set; }

    public string? CurrentAccess { get; private set; }

    /// <summary>The code the sign-in hands out and the PKCE challenge it was opened with.</summary>
    public string Code { get; set; } = "code-0001";

    public string? Challenge { get; set; }

    public List<string> Grants { get; } = [];

    /// <summary>Receipts, newest first: (summary, detail).</summary>
    public List<JsonObject> Details { get; } = [];

    /// <summary>Ids whose v2 details answer HTTP 500 (the v3 ones work).</summary>
    public HashSet<string> BrokenV2 { get; } = new(StringComparer.Ordinal);

    public int PageSize { get; set; } = 2;

    /// <summary>The ticket API is unreachable: its requests throw.</summary>
    public bool TicketsDown { get; set; }

    /// <summary>Runs when a ticket request fails because <see cref="TicketsDown"/> (what the app has stored at that moment).</summary>
    public Action? OnTicketsDown { get; set; }

    public List<string> TicketRequests { get; } = [];

    public BankHttpClient CreateClient() => new(_ => new Forward(this));

    private sealed class Forward(FakeLidlPlus fake) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fake.Handle(request));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private HttpResponseMessage Handle(HttpRequestMessage request)
    {
        lock (_gate)
        {
            var url = request.RequestUri!;
            if (url.Host == Options.AuthBase.Host && url.AbsolutePath == "/connect/token")
            {
                var form = HttpUtility.ParseQueryString(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var grant = form["grant_type"] ?? "";
                Grants.Add(grant);
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("LidlPlusNativeClient:secret"));
                if (request.Headers.Authorization?.Parameter != basic) return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "invalid_client" });
                var ok = grant switch
                {
                    "refresh_token" => CurrentRefresh != null && form["refresh_token"] == CurrentRefresh,
                    "authorization_code" => form["code"] == Code && Challenge != null && form["code_verifier"] is { } v
                        && Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(v))) == Challenge && form["redirect_uri"] == Options.RedirectUri,
                    _ => false,
                };
                if (!ok) return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" });
                if (grant == "authorization_code") Code = "spent";
                _generation++;
                CurrentRefresh = $"refresh-{_generation}";
                CurrentAccess = $"access-{_generation}";
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["access_token"] = CurrentAccess,
                    ["refresh_token"] = CurrentRefresh,
                    ["expires_in"] = 3600,
                    ["token_type"] = "Bearer",
                });
            }
            if (url.Host != Options.TicketsBase.Host) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (TicketsDown)
            {
                OnTicketsDown?.Invoke();
                throw new HttpRequestException("The ticket API is unreachable.");
            }
            if (request.Headers.Authorization?.Parameter != CurrentAccess || CurrentAccess == null)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (!request.Headers.TryGetValues("App", out var app) || !app.Contains("com.lidl.eci.lidl.plus", StringComparer.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            TicketRequests.Add(url.PathAndQuery);
            var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries); // api, v2, DE, tickets[, id]
            if (parts.Length == 4)
            {
                var page = int.Parse(HttpUtility.ParseQueryString(url.Query)["pageNumber"] ?? "1", CultureInfo.InvariantCulture);
                var slice = Details.Skip((page - 1) * PageSize).Take(PageSize).Select(Summary).ToArray();
                return Json(HttpStatusCode.OK, new JsonObject { ["tickets"] = new JsonArray(slice), ["totalCount"] = Details.Count, ["size"] = PageSize });
            }
            var id = parts[4];
            if (parts[1] == "v2" && BrokenV2.Contains(id)) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var detail = Details.FirstOrDefault(d => (string?)d["id"] == id);
            return detail == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(HttpStatusCode.OK, detail.DeepClone());
        }
    }

    private static JsonNode Summary(JsonObject detail) => new JsonObject
    {
        ["id"] = detail["id"]!.DeepClone(),
        ["date"] = detail["date"]!.DeepClone(),
        ["totalAmount"] = detail["totalAmount"]!.DeepClone(),
        ["store"] = detail["store"]?.DeepClone(),
        ["articlesCount"] = 3,
        ["isFavorite"] = false,
    };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- made-up receipts ----

    public static JsonObject Line(string name, string quantity, string unit, string amount, string vat = "A", bool weight = false,
        string? discount = null, JsonObject? deposit = null) => new()
        {
            ["description"] = name,
            ["quantity"] = quantity,
            ["isWeight"] = weight,
            ["currentUnitPrice"] = unit,
            ["originalAmount"] = amount,
            ["taxGroupName"] = vat,
            ["discounts"] = discount == null ? new JsonArray() : new JsonArray(new JsonObject { ["description"] = "Rabatt", ["amount"] = discount }),
            ["deposit"] = deposit,
        };

    public static JsonObject Ticket(string id, string date, decimal total, JsonArray lines, params (string Type, string Amount)[] payments) => new()
    {
        ["id"] = id,
        ["date"] = date,
        ["totalAmount"] = total,
        ["currency"] = new JsonObject { ["code"] = "EUR", ["symbol"] = "€" },
        ["store"] = new JsonObject { ["id"] = "DE0000", ["name"] = "Lidl Teststraße 1", ["locality"] = "Musterstadt" },
        ["itemsLine"] = lines,
        ["payments"] = new JsonArray(payments.Select(p => (JsonNode)new JsonObject { ["type"] = p.Type, ["amount"] = p.Amount, ["description"] = p.Type }).ToArray()),
    };

    /// <summary>
    /// A small shop: bananas by weight, milk with a discount, water with deposit, a bottle return and washing-up liquid
    /// (non-food); the lines come to 4.90, a receipt coupon of <paramref name="coupon"/> comes off, the rest is paid by card.
    /// </summary>
    public static JsonObject Groceries(string id, string date, decimal coupon = 0m)
    {
        var paid = 4.90m - coupon;
        return Ticket(id, date, paid, new JsonArray(
                Line("Bananen", "0,812", "1,49", "1,21", weight: true),
                Line("Vollmilch 3,5%", "2", "1,15", "2,30", discount: "0,30"),
                Line("Mineralwasser", "1", "0,49", "0,49", deposit: new JsonObject { ["description"] = "Pfand 0,25", ["quantity"] = "1", ["unitPrice"] = "0,25", ["amount"] = "0,25", ["taxGroupName"] = "A" }),
                Line("Pfandrückgabe", "1", "-0,25", "-0,25"),
                Line("Spülmittel Zitrone", "1", "1,20", "1,20")),
            ("CreditCard", paid.ToString("0.00", CultureInfo.GetCultureInfo("de-DE"))));
    }
}
