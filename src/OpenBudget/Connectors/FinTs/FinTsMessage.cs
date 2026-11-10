using System.Globalization;
using System.Text;

namespace OpenBudget.Connectors.FinTs;

/// <summary>Who is talking: the bank, the user and the customer system id of this installation ("0" until synchronized).</summary>
public sealed record FinTsIdentity(string Blz, string UserId, string Pin, string SystemId);

/// <summary>A return code from HIRMG (message level) or HIRMS (segment level).</summary>
public sealed record FinTsReturnCode(int Code, string Text, IReadOnlyList<string> Parameters, int? ForSegment)
{
    public bool IsError => Code >= 9000;

    public bool IsWarning => Code is >= 3000 and < 9000;
}

/// <summary>A parsed bank message: dialog identity, business segments and return codes.</summary>
public sealed class FinTsResponse
{
    private FinTsResponse(string dialogId, int messageNumber, IReadOnlyList<FinTsSegment> segments, IReadOnlyList<FinTsReturnCode> returns)
    {
        DialogId = dialogId;
        MessageNumber = messageNumber;
        Segments = segments;
        Returns = returns;
    }

    public string DialogId { get; }

    public int MessageNumber { get; }

    /// <summary>Everything the bank sent inside the envelope, return segments included.</summary>
    public IReadOnlyList<FinTsSegment> Segments { get; }

    public IReadOnlyList<FinTsReturnCode> Returns { get; }

    public bool HasError => Returns.Any(r => r.IsError);

    public bool Has(int code) => Returns.Any(r => r.Code == code);

    public FinTsReturnCode? Find(int code) => Returns.FirstOrDefault(r => r.Code == code);

    public IEnumerable<FinTsSegment> Of(string name) => Segments.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>First error text (codes and texts only, no customer data).</summary>
    /// <remarks>
    /// Banks often answer with a generic "message/dialog aborted" (9050, 9800) next to the code that names the cause, so
    /// every distinct error is listed, the specific ones first.
    /// </remarks>
    public string? FirstError()
    {
        var errors = Returns.Where(r => r.IsError)
            .DistinctBy(r => (r.Code, r.Text))
            .OrderBy(r => r.Code is 9050 or 9800 ? 1 : 0)
            .Select(r => $"{r.Text} ({r.Code})")
            .ToList();
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    public static FinTsResponse Parse(string message)
    {
        var outer = FinTsWire.Parse(message);
        var header = outer.FirstOrDefault(s => string.Equals(s.Name, "HNHBK", StringComparison.Ordinal))
            ?? throw new FinTsProtocolException("The bank's answer is not a FinTS message.");
        var dialogId = header.Element(2);
        _ = int.TryParse(header.Element(3), NumberStyles.None, CultureInfo.InvariantCulture, out var msgNo);

        var data = outer.FirstOrDefault(s => string.Equals(s.Name, "HNVSD", StringComparison.Ordinal));
        var inner = data != null
            ? FinTsWire.Parse(data.Element(0))
            : outer.Where(s => !s.Name.StartsWith("HN", StringComparison.Ordinal)).ToList();
        inner = inner.Where(s => !s.Name.StartsWith("HN", StringComparison.Ordinal)).ToList();

        var returns = new List<FinTsReturnCode>();
        foreach (var segment in inner.Where(s => s.Name is "HIRMG" or "HIRMS"))
        {
            foreach (var element in segment.Elements)
            {
                if (element.Count == 0 || !int.TryParse(element[0], NumberStyles.None, CultureInfo.InvariantCulture, out var code)) continue;
                returns.Add(new FinTsReturnCode(
                    code,
                    element.Count > 2 ? element[2] : string.Empty,
                    element.Skip(3).ToList(),
                    string.Equals(segment.Name, "HIRMS", StringComparison.Ordinal) ? segment.Reference : null));
            }
        }
        return new FinTsResponse(dialogId, msgNo, inner, returns);
    }
}

/// <summary>Assembles a PIN/TAN message: HNHBK, the sign/seal envelope around the business segments, HNHBS.</summary>
public static class FinTsMessageBuilder
{
    /// <summary>The number the first business segment gets (HNHBK is 1, HNSHK 2).</summary>
    public const int FirstBusinessSegment = 3;

    public static string Build(
        FinTsIdentity identity, string dialogId, int messageNumber, string securityFunction, int controlReference,
        DateTime nowUtc, IReadOnlyList<FinTsSegmentBuilder> segments, string? tan = null)
    {
        var date = nowUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var time = nowUtc.ToString("HHmmss", CultureInfo.InvariantCulture);
        var sysId = FinTsWire.Escape(identity.SystemId);
        var user = FinTsWire.Escape(identity.UserId);
        var blz = FinTsWire.Escape(identity.Blz);
        var control = controlReference.ToString(CultureInfo.InvariantCulture);

        var inner = new StringBuilder();
        inner.Append(CultureInfo.InvariantCulture, $"HNSHK:2:4+PIN:2+{FinTsWire.Escape(securityFunction)}+{control}+1+1+1::{sysId}+2+1:{date}:{time}+1:999:1+6:10:16+280:{blz}:{user}:S:0:0'");
        var number = FirstBusinessSegment;
        foreach (var segment in segments) inner.Append(segment.Build(number++));
        var pin = tan == null ? FinTsWire.Escape(identity.Pin) : FinTsWire.Escape(identity.Pin) + ":" + FinTsWire.Escape(tan);
        inner.Append(CultureInfo.InvariantCulture, $"HNSHA:{number.ToString(CultureInfo.InvariantCulture)}:2+{control}++{pin}'");
        number++;

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"HNVSK:998:3+PIN:2+998+1+1::{sysId}+1:{date}:{time}+2:2:13:{FinTsWire.Binary(new string('\0', 8))}:5:1+280:{blz}:{user}:V:0:0+0'");
        body.Append(CultureInfo.InvariantCulture, $"HNVSD:999:1+{FinTsWire.Binary(inner.ToString())}'");
        body.Append(CultureInfo.InvariantCulture, $"HNHBS:{number.ToString(CultureInfo.InvariantCulture)}:1+{messageNumber.ToString(CultureInfo.InvariantCulture)}'");

        var tail = string.Create(CultureInfo.InvariantCulture, $"+300+{FinTsWire.Escape(dialogId)}+{messageNumber}'");
        var size = "HNHBK:1:3+".Length + 12 + tail.Length + body.Length;
        return string.Create(CultureInfo.InvariantCulture, $"HNHBK:1:3+{size:D12}{tail}") + body;
    }
}
