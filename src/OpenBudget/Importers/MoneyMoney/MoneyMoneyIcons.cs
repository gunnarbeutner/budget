using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>A parsed MoneyMoney icon: its colour and shape indexes and the OpenBudget icon (null: the default) and colour.</summary>
public sealed record MoneyMoneyIconLook(int ColorIndex, int ShapeIndex, string? Icon, string Color);

/// <summary>
/// Maps MoneyMoney's account and account-group icons onto OpenBudget's. MoneyMoney stores them in <c>accounts.icon</c>
/// and <c>account_groups.icon</c> as <c>AccountIcon&lt;C&gt;-&lt;S&gt;</c> or <c>AccountIcon&lt;C&gt;</c>: C is the colour
/// (1 red, 2 orange, 3 yellow, 4 green, 5 blue, 6 purple, 7 grey), S the shape (1 rounded square, also when missing;
/// 2 card, 3 envelope, 4 piggy bank, 5 bank, 6 wallet, 7 money bag, 8 coin stack, 9 banknote).
/// </summary>
public static class MoneyMoneyIcons
{
    /// <summary>OpenBudget palette keys (<see cref="CategoryColors.Palette"/>) by MoneyMoney colour index.</summary>
    private static readonly string[] Colors = ["#b91c1c", "#a3480a", "#856404", "#2a7330", "#1d4ed8", "#7e22ce", "#4b5563"];

    /// <summary>
    /// OpenBudget icons by MoneyMoney shape index. The plain rounded square has no counterpart: the account keeps the
    /// icon of its type.
    /// </summary>
    private static readonly string?[] Shapes =
        [null, "bi-credit-card", "bi-envelope", "bi-piggy-bank", "bi-bank", "bi-wallet2", "bi-cash-coin", "bi-coin", "bi-cash"];

    /// <summary>The OpenBudget look of a stored MoneyMoney icon; null when the value is missing or not understood.</summary>
    public static MoneyMoneyIconLook? Parse(string? value)
    {
        const string prefix = "AccountIcon";
        if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = value.AsSpan(prefix.Length);
        int shape = 1;
        if (rest.Length == 3 && rest[1] == '-' && char.IsAsciiDigit(rest[2])) shape = rest[2] - '0';
        else if (rest.Length != 1) return null;
        if (!char.IsAsciiDigit(rest[0])) return null;
        int color = rest[0] - '0';
        if (color < 1 || color > Colors.Length || shape < 1 || shape > Shapes.Length) return null;
        return new MoneyMoneyIconLook(color, shape, Shapes[shape - 1], Colors[color - 1]);
    }
}
