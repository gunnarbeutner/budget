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

    [Theory]
    [InlineData("Musterdeo Roll-On Fresh 50ml", true)]
    [InlineData("Toilettenpapier 3-lagig 8 Rollen", true)]
    [InlineData("Spülmittel Zitrone", true)]
    [InlineData("Apfelschorle 0,5l", false)]
    [InlineData("Süßkartoffel", false)]
    public void Recognises_non_food(string name, bool expected) => Assert.Equal(expected, ReceiptCategorizer.IsNonFood(name));

    [Fact]
    public void Memory_beats_keywords_beats_ai_beats_the_booking_category()
    {
        var memory = new Dictionary<string, long>(StringComparer.Ordinal) { ["apfelschorle"] = Drinks, ["musterdeo roll on fresh"] = Food };
        var ai = new Dictionary<long, long> { [1] = Drinks, [4] = Food };
        var result = ReceiptCategorizer.Assign(Lines, Food, Drugstore, memory, ai);

        Assert.Equal(new LineCategory(Drinks, ReceiptCategorySource.Ai), result[1]);
        Assert.Equal(new LineCategory(Food, ReceiptCategorySource.Memory), result[2]);
        Assert.Equal(new LineCategory(Drinks, ReceiptCategorySource.Memory), result[3]);
        Assert.Equal(new LineCategory(Drugstore, ReceiptCategorySource.Keyword), result[4]);
        Assert.Equal(new LineCategory(Food, ReceiptCategorySource.Default), result[5]);
        Assert.Equal(new LineCategory(Food, ReceiptCategorySource.Default), result[6]);
    }

    [Fact]
    public void Without_a_non_food_category_keywords_place_nothing_and_vat_alone_decides_nothing()
    {
        var result = ReceiptCategorizer.Assign(Lines, Food, null, new Dictionary<string, long>(StringComparer.Ordinal));
        Assert.All(result.Values, r => Assert.Equal(Food, r.CategoryId));
        Assert.Equal([1, 2, 3, 4], ReceiptCategorizer.Undecided(Lines, null, new Dictionary<string, long>(StringComparer.Ordinal)).Select(l => l.Id));
        Assert.Equal([1, 3], ReceiptCategorizer.Undecided(Lines, Drugstore, new Dictionary<string, long>(StringComparer.Ordinal)).Select(l => l.Id));
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
        var categories = ReceiptCategorizer.Assign(Lines, Food, Drugstore, new Dictionary<string, long>(StringComparer.Ordinal));
        var payment = Lines.Sum(l => l.TotalCents) - 100;
        var parts = ReceiptCategorizer.Parts(Lines, categories, -100, -payment, Food);

        Assert.Equal(2, parts.Count);
        Assert.Equal(-payment, parts.Sum(p => p.AmountCents));
        var drugstore = parts.Single(p => p.CategoryId == Drugstore);
        Assert.Equal([2, 4], drugstore.LineIds);
        var food = parts.Single(p => p.CategoryId == Food);
        Assert.Equal([1, 3, 5, 6], food.LineIds);
        Assert.All(parts, p => Assert.True(p.AmountCents < 0));
    }

    [Fact]
    public void A_booking_that_differs_from_the_receipt_puts_the_difference_on_the_largest_part()
    {
        var categories = ReceiptCategorizer.Assign(Lines, Food, Drugstore, new Dictionary<string, long>(StringComparer.Ordinal));
        var parts = ReceiptCategorizer.Parts(Lines, categories, 0, -1000, Food);
        Assert.Equal(-1000, parts.Sum(p => p.AmountCents));
    }

    [Fact]
    public void Signatures_ignore_order()
    {
        Assert.Equal(ReceiptCategorizer.Signature([(-100, 1), (-200, null)]), ReceiptCategorizer.Signature([(-200, null), (-100, 1)]));
        Assert.NotEqual(ReceiptCategorizer.Signature([(-100, 1)]), ReceiptCategorizer.Signature([(-100, 2)]));
    }
}
