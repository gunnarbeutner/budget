using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Sorting receipt lines into categories and turning them into split parts that add up to the booking.</summary>
public sealed class ReceiptCategorizerTests
{
    private const long Food = 10;
    private const long Drugstore = 20;
    private const long Drinks = 30;

    private static ReceiptLine Line(long id, string name, long cents, string vat = "B", bool noDiscount = false, ReceiptItemKind kind = ReceiptItemKind.Product) =>
        new(id, name, vat, cents, noDiscount, kind);

    private static readonly ReceiptLine[] Lines =
    [
        Line(1, "Beispiel Gouda gerieben 150g", 189),
        Line(2, "Musterdeo Roll-On Fresh 50ml", 295, "A"),
        Line(3, "Apfelschorle 0,5l", 218, "A"),
        Line(4, "Spülmittel Zitrone 450ml", 145, "A"),
        Line(5, "PFAND 0,25", 50, "A", noDiscount: true, kind: ReceiptItemKind.Deposit),
        Line(6, "Liefergebühr", 0, "A/B", kind: ReceiptItemKind.Fee),
    ];

    [Theory]
    [InlineData("Beispiel Gouda gerieben 150g", "beispiel gouda gerieben")]
    [InlineData("Beispiel Gouda gerieben 250 g", "beispiel gouda gerieben")]
    [InlineData("Beispiel Banane ca. 200g", "beispiel banane")]
    [InlineData("Apfelschorle 6x1,5l*", "apfelschorle")]
    [InlineData("ja! Kochschinken", "ja kochschinken")]
    public void Normalises_names(string name, string expected) => Assert.Equal(expected, ReceiptCategorizer.Normalize(name));

    private static readonly ReceiptSourceRules Rules = new(null,
        [new AllowedCategory(Drugstore, "Haushalt › Verbrauchsartikel", "Drogerie, Haushalt"), new AllowedCategory(Drinks, "Lebensmittel › Getränke")]);

    [Theory]
    [InlineData("Musterdeo Roll-On Fresh 50ml", "drugstore")]
    [InlineData("Toilettenpapier 3-lagig 8 Rollen", "drugstore")]
    [InlineData("ZAHNP.PROFI", "drugstore")]
    [InlineData("Spülmittel Zitrone", "household")]
    [InlineData("Apfelschorle 0,5l", "drinks")]
    [InlineData("Testbier Pils 0,5l", "alcohol")]
    [InlineData("Weintrauben hell", null)]
    [InlineData("Süßkartoffel", null)]
    public void Recognises_built_in_kinds(string name, string? kind) => Assert.Equal(kind, ReceiptCategorizer.Kind(name));

    [Fact]
    public void Memory_beats_keywords_beats_ai_beats_the_default_and_only_allowed_categories_count()
    {
        const long Elsewhere = 99;
        var memory = new Dictionary<string, long>(StringComparer.Ordinal) { ["apfelschorle"] = Food, ["musterdeo roll on fresh"] = Drinks, ["beispiel gouda gerieben"] = Elsewhere };
        var ai = new Dictionary<long, long> { [1] = Drinks, [4] = Food };
        var result = ReceiptCategorizer.Assign(Lines, Food, Rules, memory, ai);

        Assert.Equal(new LineCategory(Drinks, ReceiptCategorySource.Ai), result[1]);      // memory of a category not allowed here is ignored
        Assert.Equal(new LineCategory(Drinks, ReceiptCategorySource.Memory), result[2]);
        Assert.Equal(new LineCategory(Food, ReceiptCategorySource.Memory), result[3]);
        Assert.Equal(new LineCategory(Drugstore, ReceiptCategorySource.Keyword), result[4]);
        Assert.Equal(new LineCategory(Food, ReceiptCategorySource.Default), result[5]);
        Assert.Equal(new LineCategory(Food, ReceiptCategorySource.Default), result[6]);

        // An AI answer outside the source's categories places nothing.
        result = ReceiptCategorizer.Assign(Lines, Food, Rules, new Dictionary<string, long>(StringComparer.Ordinal), new Dictionary<long, long> { [1] = Elsewhere });
        Assert.Equal(new LineCategory(Food, ReceiptCategorySource.Default), result[1]);
    }

    [Fact]
    public void Extra_keywords_and_the_default_category()
    {
        var rules = new ReceiptSourceRules(Drinks, [new AllowedCategory(Drugstore, "Freizeit › Sonstiges", null, "gouda, käse")]);
        var result = ReceiptCategorizer.Assign(Lines, Food, rules, new Dictionary<string, long>(StringComparer.Ordinal));
        Assert.Equal(Drugstore, result[1].CategoryId);
        Assert.Equal(Drinks, result[4].CategoryId);   // household, but no allowed category for it: the default
        Assert.Equal(Drinks, result[5].CategoryId);   // deposit: the default
    }

    [Fact]
    public void Without_allowed_categories_everything_stays_with_the_booking_and_nothing_goes_to_the_ai()
    {
        var empty = new Dictionary<string, long>(StringComparer.Ordinal);
        var result = ReceiptCategorizer.Assign(Lines, Food, ReceiptSourceRules.None, empty);
        Assert.All(result.Values, r => Assert.Equal(Food, r.CategoryId));
        Assert.Empty(ReceiptCategorizer.Undecided(Lines, ReceiptSourceRules.None, empty));
        Assert.Equal([1], ReceiptCategorizer.Undecided(Lines, Rules, empty).Select(l => l.Id));
    }

    [Fact]
    public void The_bonus_falls_on_discountable_lines_only()
    {
        var net = ReceiptCategorizer.NetCents(Lines, -100);
        Assert.Equal(50, net[5]);   // deposit marked "*": no share
        Assert.Equal(0, net[6]);    // nothing to discount
        Assert.Equal(Lines.Sum(l => l.TotalCents) - 100, net.Values.Sum());
        Assert.True(net[2] < 295 && net[1] < 189);
    }

    [Fact]
    public void One_part_per_category_adding_up_to_the_booking_exactly()
    {
        var categories = ReceiptCategorizer.Assign(Lines, Food, Rules, new Dictionary<string, long>(StringComparer.Ordinal));
        var payment = Lines.Sum(l => l.TotalCents) - 100;
        var parts = ReceiptCategorizer.Parts(Lines, categories, -100, -payment, Food);

        Assert.Equal(3, parts.Count);
        Assert.Equal(-payment, parts.Sum(p => p.AmountCents));
        var drugstore = parts.Single(p => p.CategoryId == Drugstore);
        Assert.Equal([2, 4], drugstore.LineIds);
        var food = parts.Single(p => p.CategoryId == Food);
        Assert.Equal([1, 5, 6], food.LineIds);
        Assert.Equal([3], parts.Single(p => p.CategoryId == Drinks).LineIds);
        Assert.All(parts, p => Assert.True(p.AmountCents < 0));
    }

    [Fact]
    public void A_booking_that_differs_from_the_receipt_puts_the_difference_on_the_largest_part()
    {
        var categories = ReceiptCategorizer.Assign(Lines, Food, Rules, new Dictionary<string, long>(StringComparer.Ordinal));
        var parts = ReceiptCategorizer.Parts(Lines, categories, 0, -1000, Food);
        Assert.Equal(-1000, parts.Sum(p => p.AmountCents));
    }

    [Fact]
    public void Signatures_ignore_order()
    {
        Assert.Equal(ReceiptCategorizer.Signature([(-100, 1), (-200, null)]), ReceiptCategorizer.Signature([(-200, null), (-100, 1)]));
        Assert.NotEqual(ReceiptCategorizer.Signature([(-100, 1)]), ReceiptCategorizer.Signature([(-100, 2)]));
    }

    [Fact]
    public void Deposit_lines_join_the_default_part_and_no_part_has_the_opposite_sign()
    {
        const long Rewe = 40;
        ReceiptLine[] lines =
        [
            Line(1, "Testeis Vanille", 399),
            Line(2, "Testbutter", 239),
            Line(3, "Pfand 0,25 EM", 300, "A", kind: ReceiptItemKind.Deposit),
            Line(4, "Pfandrückgabe", -100, "A", kind: ReceiptItemKind.Deposit),
            Line(5, "Pfandrückgabe", -400, "A", kind: ReceiptItemKind.Deposit),
            Line(6, "Testduschgel", 195, "A"),
        ];
        // Another shop's category is not this source's: memory and AI answers for it are ignored.
        var memory = new Dictionary<string, long>(StringComparer.Ordinal) { ["testbutter"] = Rewe };
        var ai = new Dictionary<long, long> { [1] = Rewe };
        var categories = ReceiptCategorizer.Assign(lines, Food, Rules, memory, ai);
        Assert.All(new long[] { 1, 2 }, id => Assert.Equal(Food, categories[id].CategoryId));
        Assert.Equal(Drugstore, categories[6].CategoryId);

        // Even when the products went elsewhere, the deposits never make a part of their own.
        var allElsewhere = lines.ToDictionary(l => l.Id, l => new LineCategory(l.Kind == ReceiptItemKind.Product ? Drugstore : Food, "test"));
        var parts = ReceiptCategorizer.Parts(lines, allElsewhere, 0, -633, Food);
        Assert.Equal(-633, Assert.Single(parts).AmountCents);

        parts = ReceiptCategorizer.Parts(lines, categories, 0, -633, Food);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.True(p.AmountCents < 0));
        Assert.Equal([1, 2, 3, 4, 5], parts.Single(p => p.CategoryId == Food).LineIds);
        Assert.Equal(-633, parts.Sum(p => p.AmountCents));
    }
}
