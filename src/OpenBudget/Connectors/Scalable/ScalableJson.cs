using System.Globalization;
using System.Text.Json;

namespace OpenBudget.Connectors.Scalable;

/// <summary>Null-tolerant navigation of GraphQL responses (a missing or null field simply yields nothing).</summary>
internal static class ScalableJson
{
    /// <summary>Follows <paramref name="path"/> through nested objects; null when any step is missing or null.</summary>
    public static JsonElement? Path(this JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next) || next.ValueKind == JsonValueKind.Null)
                return null;
            current = next;
        }
        return current;
    }

    public static JsonElement? Path(this JsonElement? element, params string[] path) => element?.Path(path);

    public static string? Str(this JsonElement element, params string[] path) =>
        element.Path(path) is { ValueKind: JsonValueKind.String } v ? v.GetString() : element.Path(path) is { ValueKind: JsonValueKind.Number } n ? n.GetRawText() : null;

    public static string? Str(this JsonElement? element, params string[] path) => element?.Str(path);

    public static bool Bool(this JsonElement element, params string[] path) =>
        element.Path(path) is { ValueKind: JsonValueKind.True };

    public static bool Bool(this JsonElement? element, params string[] path) => element?.Bool(path) ?? false;

    /// <summary>Numbers arrive as JSON numbers or as strings.</summary>
    public static decimal? Dec(this JsonElement element, params string[] path) => element.Path(path) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetDecimal(out var d) => d,
        { ValueKind: JsonValueKind.String } s when decimal.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public static decimal? Dec(this JsonElement? element, params string[] path) => element?.Dec(path);

    /// <summary>The items of an array; a single object counts as a one-item list, anything else as empty.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement? element)
    {
        if (element is not { } e) return [];
        if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray();
        return e.ValueKind == JsonValueKind.Object ? [e] : [];
    }
}
