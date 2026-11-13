using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Assist;

/// <summary>A category the model may pick; <see cref="Handle"/> is what it sees ("c3").</summary>
public sealed record PromptCategory(string Handle, long Id, string Path, bool Income);

/// <summary>A booking to categorize with up to three categorized neighbours (their category handles).</summary>
public sealed record PromptItem(AssistBooking Booking, IReadOnlyList<(AssistBooking Booking, string CategoryHandle)> Neighbours);

/// <summary>One validated answer of the model.</summary>
public sealed record AiCategorySuggestion(long TransactionId, long? CategoryId, double Confidence, string Rationale, string? RuleExpression, string? RuleScope);

/// <summary>
/// The categorization prompt: a stable system part (instructions, category list, rule syntax, example rules) followed
/// by the variable batch, the strict output schema and the validation of the answer.
/// </summary>
public static class CategoryPrompt
{
    public const string SchemaName = "category_suggestions";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false };

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

    public static string SystemPrompt(IReadOnlyList<PromptCategory> categories, IReadOnlyList<string> exampleRules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You categorize bank bookings of a German household for a personal budgeting app.
            For each booking pick the best category id from the list, or null when none fits or you are unsure.
            Confidence is your probability (0..1) that the category is right; be honest, 0.5 means a guess.
            Rationale: one short sentence, in the language of the booking texts, without repeating amounts.
            Similar bookings that the household already categorized are given as evidence; they are usually right.
            Income categories (marked income) only fit money coming in, other categories money going out (refunds excepted).
            Optionally propose a rule when the booking clearly belongs to a recurring pattern (same payee or stable purpose
            words) that should always get this category; otherwise rule is null. Prefer a rule on the payee name.
            Tokens like ACC_1 stand for a counterparty account, PERSON_A for a household member; # stands for removed digits.
            Never put such tokens into a rule.

            Rule syntax (a search expression):
            - name:"Example Market"   payee contains the phrase (case-insensitive, ä = ae)
            - purpose:rent             purpose contains the word
            - booking_text:Lastschrift
            - "Example Market" without a field searches payee, purpose, comment and booking text
            - amount<0, amount>=-50, amount=-9.99   signed amount in EUR
            - AND, OR, NOT (upper case), parentheses; terms next to each other are AND-ed; * is a wildcard

            Categories (id | path | income):
            """);
        foreach (var c in categories)
            sb.Append(c.Handle).Append(" | ").Append(c.Path).AppendLine(c.Income ? " | income" : "");
        if (exampleRules.Count > 0)
        {
            sb.AppendLine().AppendLine("Existing rules of the household (expression => category id):");
            foreach (var r in exampleRules) sb.AppendLine(r);
        }
        return sb.ToString();
    }

    public static string UserContent(IReadOnlyList<PromptItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var node = JsonSerializer.SerializeToNode(item.Booking, Json)!.AsObject();
            var neighbours = new JsonArray();
            foreach (var (b, handle) in item.Neighbours)
            {
                var n = JsonSerializer.SerializeToNode(b, Json)!.AsObject();
                n.Remove("tx");
                n["category_id"] = handle;
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
