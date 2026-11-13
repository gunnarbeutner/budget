using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenBudget.Assist;

/// <summary>
/// A category the model may pick; <see cref="Handle"/> is what it sees ("c3"). <see cref="Hints"/> are the household's
/// rule expressions for it (redacted), <see cref="Restriction"/> the own accounts it is limited to ("OWN_1, OWN_3").
/// </summary>
public sealed record PromptCategory(string Handle, long Id, string Path, bool Income, string? Hints = null, string? Restriction = null);

/// <summary>
/// A categorized booking shown as evidence: its category (handle and path) and whether its mandate, creditor id and
/// amount are the same as the booking's.
/// </summary>
public sealed record PromptNeighbour(AssistBooking Booking, string CategoryHandle, string CategoryPath, bool SameMandate = false, bool SameCreditor = false, bool SameAmount = false);

/// <summary>A booking to categorize with its categorized neighbours.</summary>
public sealed record PromptItem(AssistBooking Booking, IReadOnlyList<PromptNeighbour> Neighbours);

/// <summary>One validated answer of the model.</summary>
public sealed record AiCategorySuggestion(long TransactionId, long? CategoryId, double Confidence, string Rationale, string? RuleExpression, string? RuleScope);

/// <summary>
/// The categorization prompt: a stable system part (instructions, category list, rule syntax, example rules) followed
/// by the variable batch, the strict output schema and the validation of the answer.
/// </summary>
public static class CategoryPrompt
{
    public const string SchemaName = "category_suggestions";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Umlauts and "›" as they are: fewer tokens, and the text is never put into HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>How the booking fields read; shared by the system prompt and the Decisions instructions.</summary>
    public const string Guidance = """
        Each booking has: date, signed amount (negative = money out), currency, payee, cleaned purpose, code (booking
        text and GVC code), account_type and account_role of the household's own account (account is its stable token,
        e.g. OWN_2), mandate and creditor (tokens of the SEPA direct-debit mandate reference and creditor id; the same
        token means the same contract or creditor), counterparty (token of the counterparty account), recurrence (how
        often the same payee and amount occurred) and order_items (what an online order contained).
        similar_categorized lists bookings the household already categorized, with their category (category_id and
        category_path), amount, date and whether mandate, creditor and amount are the same as the booking's
        (same_mandate, same_creditor, same_amount). They are usually right.
        - Prefer contract identity over text similarity: a neighbour with same_mandate is the same contract and almost
          always has the right category, even when another neighbour's text looks closer. One creditor (a utility, an
          insurer, a phone company) can have several contracts in different categories (electricity and gas); the
          mandate tells them apart, and so does a recurring fixed amount.
        - The same payee with the same recurring amount (a subscription) usually keeps its category; one-off amounts of
          that payee may belong elsewhere (an app store subscription vs. single purchases).
        - Transfers that fund or top up a PayPal wallet or a credit card from another own account, or pay off a card
          bill, are transfers between own accounts, not purchases; a purchase paid via PayPal (on the PayPal wallet,
          naming a shop) is categorized by what was bought. Look at account_role and the wording.
        - When order_items are present, categorize by the items, not by the shop's name.
        - Income categories (marked income) only fit money coming in, other categories money going out (refunds excepted).
        - Tokens like ACC_1 stand for a counterparty account, OWN_1 for an own account, MANDATE_1 and CREDITOR_1 for SEPA
          references, PERSON_A for a household member; # stands for removed digits.
        """;

    public static JsonObject Schema() => (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["suggestions"],
          "properties": {
            "suggestions": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["tx", "categoryId", "confidence", "rationale", "rule"],
                "properties": {
                  "tx": { "type": "string" },
                  "categoryId": { "type": ["string", "null"] },
                  "confidence": { "type": "number" },
                  "rationale": { "type": "string" },
                  "rule": {
                    "anyOf": [
                      { "type": "null" },
                      {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["expression", "scope"],
                        "properties": {
                          "expression": { "type": "string" },
                          "scope": { "type": "string", "enum": ["payee", "purpose", "counterparty"] }
                        }
                      }
                    ]
                  }
                }
              }
            }
          }
        }
        """)!;

    /// <summary>One line of the category list: "c3 | Housing › Gas | expense | only OWN_1 | rules: name:"Example Gas"".</summary>
    public static string CategoryLine(PromptCategory c) => c.Handle + " | " + Describe(c);

    /// <summary>"Housing › Gas | expense | only OWN_1 | rules: name:"Example Gas"" (also the Decisions choice description).</summary>
    public static string Describe(PromptCategory c) =>
        c.Path + (c.Income ? " | income" : " | expense") + (c.Restriction == null ? "" : " | only " + c.Restriction) + (c.Hints == null ? "" : " | rules: " + c.Hints);

    /// <summary>The stable part of the request: instructions, rule syntax and the category list (the same for every batch).</summary>
    public static string SystemPrompt(IReadOnlyList<PromptCategory> categories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You categorize bank bookings of a German household for a personal budgeting app.
            For each booking pick the best category id from the list, or null when none fits or you are unsure.
            Confidence is your probability (0..1) that the category is right; be honest, 0.5 means a guess.
            Rationale: one short sentence, in the language of the booking texts, without repeating amounts.
            Optionally propose a rule when the booking clearly belongs to a recurring pattern (same payee or stable purpose
            words) that should always get this category; otherwise rule is null. Prefer a rule on the payee name; add an
            amount condition when only the recurring amount tells the categories apart. Never put tokens (ACC_1, OWN_1,
            MANDATE_1, CREDITOR_1, PERSON_A, #) into a rule.

            """);
        sb.AppendLine(Guidance);
        sb.AppendLine("""

            Rule syntax (a search expression):
            - name:"Example Market"   payee contains the phrase (case-insensitive, ä = ae)
            - purpose:rent             purpose contains the word
            - booking_text:Lastschrift
            - "Example Market" without a field searches payee, purpose, comment and booking text
            - amount<0, amount>=-50, amount=-9.99   signed amount in EUR
            - AND, OR, NOT (upper case), parentheses; terms next to each other are AND-ed; * is a wildcard

            Categories (id | path | income or expense, then when present: the only own accounts it is for, the household's rules for it):
            """);
        foreach (var c in categories) sb.AppendLine(CategoryLine(c));
        return sb.ToString();
    }

    public static string UserContent(IReadOnlyList<PromptItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var node = JsonSerializer.SerializeToNode(item.Booking, Json)!.AsObject();
            var neighbours = new JsonArray();
            foreach (var nb in item.Neighbours)
            {
                var n = JsonSerializer.SerializeToNode(nb.Booking, Json)!.AsObject();
                n.Remove("tx");
                n["category_id"] = nb.CategoryHandle;
                n["category_path"] = nb.CategoryPath;
                if (item.Booking.Mandate != null && nb.Booking.Mandate != null) n["same_mandate"] = nb.SameMandate;
                if (item.Booking.Creditor != null && nb.Booking.Creditor != null) n["same_creditor"] = nb.SameCreditor;
                n["same_amount"] = nb.SameAmount;
                neighbours.Add(n);
            }
            node["similar_categorized"] = neighbours;
            array.Add(node);
        }
        return "Bookings to categorize:\n" + array.ToJsonString(Json);
    }

    /// <summary>
    /// Validates the model's JSON: unknown bookings and unknown category ids are dropped (an unknown id becomes "no
    /// suggestion"), confidence is clamped to 0..1. Throws <see cref="AssistException"/> for a refusal or unreadable JSON.
    /// </summary>
    public static List<AiCategorySuggestion> ParseAnswer(AssistResponse response, AssistRedactor redactor, IReadOnlyDictionary<string, PromptCategory> categories)
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
        if (root?["suggestions"] is not JsonArray list) throw new AssistException("The AI answer does not match the schema.");
        var result = new List<AiCategorySuggestion>();
        var seen = new HashSet<long>();
        foreach (var s in list)
        {
            if (s is not JsonObject o) continue;
            var tx = redactor.Resolve(Str(o["tx"]));
            if (tx is not { } id || !seen.Add(id)) continue;
            var handle = Str(o["categoryId"]);
            long? categoryId = handle != null && categories.TryGetValue(handle, out var c) ? c.Id : null;
            var confidence = o["confidence"] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;
            if (categoryId == null) confidence = 0;
            string? rule = null, scope = null;
            if (categoryId != null && o["rule"] is JsonObject r && Str(r["expression"]) is { Length: > 0 } expr)
            {
                rule = expr.Trim();
                scope = Str(r["scope"]);
            }
            var rationale = Str(o["rationale"]) ?? "";
            if (rationale.Length > 280) rationale = rationale[..280];
            result.Add(new AiCategorySuggestion(id, categoryId, confidence, rationale, rule, scope));
        }
        return result;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string Handle(int index) => "c" + index.ToString(CultureInfo.InvariantCulture);
}
