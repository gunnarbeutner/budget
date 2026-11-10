using System.Globalization;
using System.Text;

namespace OpenBudget.Connectors.FinTs;

/// <summary>The bank or the wire format did something the client cannot make sense of.</summary>
public sealed class FinTsProtocolException : Exception
{
    public FinTsProtocolException()
    {
    }

    public FinTsProtocolException(string message) : base(message)
    {
    }

    public FinTsProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// One parsed segment. Elements are data elements or data element groups; each is the list of its
/// components (a plain data element has exactly one). Values are unescaped; binary payloads are kept
/// as the ISO-8859-1 characters that carried them (one char per byte).
/// </summary>
public sealed class FinTsSegment
{
    private static readonly IReadOnlyList<string> NoComponents = [];

    public FinTsSegment(string name, int number, int version, int? reference, IReadOnlyList<IReadOnlyList<string>> elements)
    {
        Name = name;
        Number = number;
        Version = version;
        Reference = reference;
        Elements = elements;
    }

    public string Name { get; }

    public int Number { get; }

    public int Version { get; }

    /// <summary>The request segment this one answers (fourth header component), when present.</summary>
    public int? Reference { get; }

    /// <summary>Everything after the header.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Elements { get; }

    public IReadOnlyList<string> Deg(int index) => index >= 0 && index < Elements.Count ? Elements[index] : NoComponents;

    /// <summary>First component of element <paramref name="index"/>; empty when absent.</summary>
    public string Element(int index) => Deg(index) is { Count: > 0 } d ? d[0] : string.Empty;

    public string Component(int element, int component) => Deg(element) is { } d && component >= 0 && component < d.Count ? d[component] : string.Empty;
}

/// <summary>Parsing and writing of the FinTS syntax: <c>'</c> ends a segment, <c>+</c> an element, <c>:</c> a component, <c>?</c> escapes, <c>@n@</c> introduces n raw bytes.</summary>
public static class FinTsWire
{
    public static string Escape(string value)
    {
        if (value.AsSpan().IndexOfAny("?+:'@") < 0) return value;
        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (c is '?' or '+' or ':' or '\'' or '@') sb.Append('?');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Writes a binary data element: <c>@length@bytes</c>, unescaped.</summary>
    public static string Binary(string data) => "@" + data.Length.ToString(CultureInfo.InvariantCulture) + "@" + data;

    public static List<FinTsSegment> Parse(string text)
    {
        var segments = new List<FinTsSegment>();
        var elements = new List<List<string>>();
        var components = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '?':
                    if (i + 1 < text.Length) current.Append(text[i + 1]);
                    i += 2;
                    break;
                case '@' when current.Length == 0 && TryReadBinary(text, i, out var data, out var next):
                    current.Append(data);
                    i = next;
                    break;
                case ':':
                    components.Add(current.ToString());
                    current.Clear();
                    i++;
                    break;
                case '+':
                    components.Add(current.ToString());
                    current.Clear();
                    elements.Add(components);
                    components = [];
                    i++;
                    break;
                case '\'':
                    components.Add(current.ToString());
                    current.Clear();
                    elements.Add(components);
                    components = [];
                    AddSegment(segments, elements);
                    elements = [];
                    i++;
                    break;
                default:
                    current.Append(c);
                    i++;
                    break;
            }
        }
        if (current.Length > 0 || components.Count > 0 || elements.Count > 0)
        {
            components.Add(current.ToString());
            elements.Add(components);
            AddSegment(segments, elements);
        }
        return segments;
    }

    private static bool TryReadBinary(string text, int at, out string data, out int next)
    {
        data = string.Empty;
        next = at;
        var end = at + 1;
        while (end < text.Length && text[end] is >= '0' and <= '9') end++;
        if (end == at + 1 || end >= text.Length || text[end] != '@') return false;
        if (!int.TryParse(text.AsSpan(at + 1, end - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var length)) return false;
        if (end + 1 + length > text.Length) throw new FinTsProtocolException("A binary data element is longer than its message.");
        data = text.Substring(end + 1, length);
        next = end + 1 + length;
        return true;
    }

    private static void AddSegment(List<FinTsSegment> segments, List<List<string>> elements)
    {
        if (elements.Count == 0) return;
        var header = elements[0];
        if (header.Count < 3 || header[0].Length == 0)
        {
            if (elements.Count == 1 && header.All(string.IsNullOrEmpty)) return;
            throw new FinTsProtocolException("A segment header is malformed.");
        }
        if (!int.TryParse(header[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || !int.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new FinTsProtocolException("A segment header is malformed.");
        int? reference = header.Count > 3 && int.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : null;
        segments.Add(new FinTsSegment(header[0], number, version, reference, elements.Skip(1).Select(e => (IReadOnlyList<string>)e).ToList()));
    }

    /// <summary>Serializes a segment (including its terminating apostrophe).</summary>
    public static string Write(FinTsSegment segment)
    {
        var sb = new StringBuilder();
        sb.Append(segment.Name).Append(':').Append(segment.Number.ToString(CultureInfo.InvariantCulture)).Append(':').Append(segment.Version.ToString(CultureInfo.InvariantCulture));
        if (segment.Reference is { } r) sb.Append(':').Append(r.ToString(CultureInfo.InvariantCulture));
        foreach (var element in segment.Elements)
        {
            sb.Append('+');
            for (var k = 0; k < element.Count; k++)
            {
                if (k > 0) sb.Append(':');
                sb.Append(Escape(element[k]));
            }
        }
        return sb.Append('\'').ToString();
    }

    public static string Write(IEnumerable<FinTsSegment> segments) => string.Concat(segments.Select(Write));
}

/// <summary>Builds an outgoing segment; the segment number is assigned when the message is assembled.</summary>
public sealed class FinTsSegmentBuilder
{
    private readonly List<string> _elements = [];

    public FinTsSegmentBuilder(string name, int version)
    {
        Name = name;
        Version = version;
    }

    public string Name { get; }

    public int Version { get; }

    /// <summary>How many data elements were added (for structure-only diagnostics).</summary>
    public int ElementCount => _elements.Count;

    /// <summary>Adds one data element or group; trailing empty components are dropped.</summary>
    public FinTsSegmentBuilder Add(params string?[] components)
    {
        var last = components.Length;
        while (last > 1 && string.IsNullOrEmpty(components[last - 1])) last--;
        _elements.Add(string.Join(':', components.Take(last).Select(c => FinTsWire.Escape(c ?? string.Empty))));
        return this;
    }

    public string Build(int number)
    {
        var elements = _elements.ToList();
        while (elements.Count > 0 && elements[^1].Length == 0) elements.RemoveAt(elements.Count - 1);
        var header = $"{Name}:{number.ToString(CultureInfo.InvariantCulture)}:{Version.ToString(CultureInfo.InvariantCulture)}";
        return elements.Count == 0 ? header + "'" : header + "+" + string.Join('+', elements) + "'";
    }
}
