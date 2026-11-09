using System.Globalization;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

public sealed class FinTsWireTests
{
    [Fact]
    public void Parse_handles_escapes_groups_and_binary()
    {
        var segments = FinTsWire.Parse("HKTST:3:2:1+a?+b:c?:d+@5@x'+y!+z++?@q'HKEND:4:1+x'");
        Assert.Equal(2, segments.Count);
        var s = segments[0];
        Assert.Equal(("HKTST", 3, 2, 1), (s.Name, s.Number, s.Version, s.Reference));
        Assert.Equal("a+b", s.Component(0, 0));
        Assert.Equal("c:d", s.Component(0, 1));
        Assert.Equal("x'+y!", s.Element(1));
        Assert.Equal("z", s.Element(2));
        Assert.Equal(string.Empty, s.Element(3));
        Assert.Equal("@q", s.Element(4));
        Assert.Equal("HKEND", segments[1].Name);
    }

    [Fact]
    public void Write_round_trips_awkward_text()
    {
        var builder = new FinTsSegmentBuilder("HKTST", 1).Add("a+b", "c:d", "e'f", "g?h", "@i");
        var segment = Assert.Single(FinTsWire.Parse(builder.Build(7)));
        Assert.Equal(["a+b", "c:d", "e'f", "g?h", "@i"], segment.Deg(0));
        Assert.Equal(builder.Build(7), FinTsWire.Write(segment));
    }

    [Fact]
    public void Builder_drops_trailing_empty_components_and_elements()
    {
        var text = new FinTsSegmentBuilder("HKX", 2).Add("a", "", "b", "").Add("").Add("").Build(3);
        Assert.Equal("HKX:3:2+a::b'", text);
    }

    [Fact]
    public void Binary_that_is_longer_than_the_message_is_rejected()
    {
        Assert.Throws<FinTsProtocolException>(() => FinTsWire.Parse("HKX:1:1+@99@abc'"));
    }

    [Theory]
    [InlineData("ing")]
    [InlineData("baader")]
    [InlineData("psd")]
    public void Recorded_messages_parse_and_have_consistent_sizes(string bank)
    {
        foreach (var message in FinTsFixture.Load(bank).Messages.Select(m => m.Message))
        {
            var outer = FinTsWire.Parse(message);
            var header = outer[0];
            Assert.Equal("HNHBK", header.Name);
            Assert.Equal(message.Length, int.Parse(header.Element(0), CultureInfo.InvariantCulture));
            var data = outer.Single(s => s.Name == "HNVSD");
            Assert.NotEmpty(FinTsWire.Parse(data.Element(0)));
        }
    }

    [Fact]
    public void Response_exposes_return_codes_with_their_segments()
    {
        var bank = FinTsFixture.Load("ing");
        var response = FinTsResponse.Parse(bank.BankMessages.First());
        Assert.True(response.Has(3920));
        Assert.Equal(["900"], response.Find(3920)!.Parameters);
        Assert.Equal(4, response.Find(3920)!.ForSegment);
        Assert.False(response.HasError);
    }

    [Fact]
    public void Built_message_has_a_correct_size_and_envelope()
    {
        var identity = new FinTsIdentity("12345678", "user", "pin+1", "0");
        var message = FinTsMessageBuilder.Build(
            identity, "0", 1, "999", 1234567, new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
            [new FinTsSegmentBuilder("HKIDN", 2).Add("280", "12345678").Add("user").Add("0").Add("1")]);
        var request = MockRequest.Parse(message);
        Assert.Equal(message.Length, int.Parse(FinTsWire.Parse(message)[0].Element(0), CultureInfo.InvariantCulture));
        Assert.Equal("pin+1", request.Pin);
        Assert.Equal("HKIDN", Assert.Single(request.Segments).Name);
        Assert.Equal(3, request.Segments[0].Number);
    }
}
