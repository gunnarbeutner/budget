using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Rules in plain words: the shapes a suggestion's rule is recognised by, and what falls back to the expression.</summary>
public sealed class RuleDescriptionTests
{
    [Theory]
    [InlineData("mandate:\"M-0001\"", RuleShape.Mandate, "M-0001")]
    [InlineData("Mandate:ostrom-123-1 OR \"MREF+ostrom-123-1\"", RuleShape.Mandate, "ostrom-123-1")]
    [InlineData("\"MREF+M-0001\" OR mandate:M-0001", RuleShape.Mandate, "M-0001")]
    [InlineData("creditor_id:\"DE00ZZZ0000001\"", RuleShape.Creditor, "DE00ZZZ0000001")]
    [InlineData("creditor_id:DE00ZZZ0000001 OR \"CRED+DE00ZZZ0000001\"", RuleShape.Creditor, "DE00ZZZ0000001")]
    [InlineData("name:\"Example Grocer\"", RuleShape.Text, "Example Grocer")]
    [InlineData("netflix", RuleShape.Text, "netflix")]
    public void Recognises_the_common_shapes(string expression, RuleShape shape, string value)
    {
        var d = RuleDescription.Of(expression);
        Assert.NotNull(d);
        Assert.Equal(shape, d.Shape);
        Assert.Equal(value, d.Value);
    }

    [Fact]
    public void Payee_and_amount_carries_the_amount()
    {
        var d = RuleDescription.Of("name:\"Example Gym\" AND amount=-29.90");
        Assert.Equal(new RuleDescription(RuleShape.PayeeAmount, "Example Gym", -29.90m), d);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mandate:A OR \"MREF+B\"")]
    [InlineData("netflix OR spotify")]
    [InlineData("NOT netflix")]
    [InlineData("name:/net.*/")]
    [InlineData("name:\"Example Gym\" AND amount<0")]
    [InlineData("(((")]
    public void Anything_else_has_no_description(string expression) => Assert.Null(RuleDescription.Of(expression));
}
