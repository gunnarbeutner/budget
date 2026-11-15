using System.Text.Json;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// A built-in product kind for receipt lines: its name, the words in a category's path or description that make the
/// category the place for it, and the product-name fragments that identify it (see <see cref="ReceiptCategorizer.Kinds"/>).
/// </summary>
public sealed record ReceiptKind(string Kind, IReadOnlyList<string> CategoryWords, IReadOnlyList<string> Keywords)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The kinds of the embedded resource <c>ReceiptKeywords.{locale}.json</c>, in file order.</summary>
    public static IReadOnlyList<ReceiptKind> Load(string locale)
    {
        var name = $"OpenBudget.Resources.ReceiptKeywords.{locale}.json";
        using var stream = typeof(ReceiptKind).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("missing embedded resource " + name);
        return JsonSerializer.Deserialize<List<ReceiptKind>>(stream, Options)
            ?? throw new InvalidOperationException("empty embedded resource " + name);
    }
}
