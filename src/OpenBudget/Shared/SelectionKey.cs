using System.Globalization;
using OpenBudget.Services;

namespace OpenBudget.Shared;

public enum SelectionKind
{
    Category,
    Group,
    Account,
}

/// <summary>
/// A category, category group or account as a text key in URLs and filter values ("cat-12", "grp-3", "acct-7").
/// </summary>
public sealed record SelectionKey(SelectionKind Kind, long Id)
{
    private const string CategoryPrefix = "cat-";
    private const string GroupPrefix = "grp-";
    private const string AccountPrefix = "acct-";

    public static SelectionKey Category(long id) => new(SelectionKind.Category, id);

    public static SelectionKey Group(long id) => new(SelectionKind.Group, id);

    public static SelectionKey Account(long id) => new(SelectionKind.Account, id);

    /// <summary>The key of a category tree node.</summary>
    public static SelectionKey Of(CategoryItemType type, long id) => type == CategoryItemType.Group ? Group(id) : Category(id);

    /// <summary>The tree node type of a category or group key.</summary>
    public CategoryItemType ItemType => Kind == SelectionKind.Group ? CategoryItemType.Group : CategoryItemType.Category;

    public override string ToString() => Prefix(Kind) + Id.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SelectionKey? key)
    {
        foreach (var kind in Enum.GetValues<SelectionKind>())
        {
            var prefix = Prefix(kind);
            if (text != null && text.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(text.AsSpan(prefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
            {
                key = new SelectionKey(kind, id);
                return true;
            }
        }
        key = null;
        return false;
    }

    private static string Prefix(SelectionKind kind) => kind switch
    {
        SelectionKind.Group => GroupPrefix,
        SelectionKind.Account => AccountPrefix,
        _ => CategoryPrefix,
    };
}

/// <summary>The documents' account filter value for the documents of a bank without an account ("bank:Example Bank").</summary>
public static class InstitutionKey
{
    private const string Prefix = "bank:";

    public static string Format(string institution) => Prefix + institution;

    public static bool TryParse(string? text, out string institution)
    {
        institution = text != null && text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..] : "";
        return text != null && text.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
