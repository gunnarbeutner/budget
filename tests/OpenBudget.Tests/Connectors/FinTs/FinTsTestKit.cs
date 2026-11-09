#pragma warning disable CA2000 // test harness: lifetimes are owned by the test
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

/// <summary>A recorded dialog (anonymized, from the files under Fixtures).</summary>
internal sealed class FinTsFixture
{
    private FinTsFixture(List<(bool Client, string Message)> messages) => Messages = messages;

    public List<(bool Client, string Message)> Messages { get; }

    public IEnumerable<string> ClientMessages => Messages.Where(m => m.Client).Select(m => m.Message);

    public IEnumerable<string> BankMessages => Messages.Where(m => !m.Client).Select(m => m.Message);

    public static FinTsFixture Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Connectors", "FinTs", "Fixtures", name + "-dialog.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var list = new List<(bool, string)>();
        foreach (var item in doc.RootElement.GetProperty("dialog").EnumerateArray())
            list.Add((item.GetProperty("direction").GetString() == "client", item.GetProperty("message").GetString()!));
        return new FinTsFixture(list);
    }

    /// <summary>The state a login would have after the recorded first response: parameter data, customer system id, TAN method.</summary>
    public ConnectorSession SeedSession()
    {
        var first = FinTsResponse.Parse(BankMessages.First());
        var request = MockRequest.Parse(ClientMessages.First());
        var session = new ConnectorSession();
        session.Values[FinTsClient.SystemIdKey] = request.SystemId;
        session.Values[FinTsClient.TanMethodKey] = request.SecurityFunction;
        if (FinTsBankData.ExtractBpd(first.Segments) is { } bpd) session.Values[FinTsClient.BpdKey] = FinTsWire.Write(bpd);
        if (FinTsBankData.ExtractUpd(first.Segments) is { } upd) session.Values[FinTsClient.UpdKey] = FinTsWire.Write(upd);
        return session;
    }
}

/// <summary>A client message as the mock bank sees it.</summary>
internal sealed class MockRequest
{
    public string Raw { get; private init; } = string.Empty;

    public string DialogId { get; private init; } = string.Empty;

    public int MessageNumber { get; private init; }

    public string SecurityFunction { get; private init; } = string.Empty;

    public string SystemId { get; private init; } = string.Empty;

    public string UserId { get; private init; } = string.Empty;

    public string Pin { get; private init; } = string.Empty;

    public string? Tan { get; private init; }

    /// <summary>Everything inside the envelope except the envelope segments.</summary>
    public List<FinTsSegment> Segments { get; private init; } = [];

    /// <summary>The whole message, envelope included, as segments.</summary>
    public List<FinTsSegment> All { get; private init; } = [];

    public static MockRequest Parse(string message)
    {
        var outer = FinTsWire.Parse(message);
        var header = outer.First(s => s.Name == "HNHBK");
        var data = outer.First(s => s.Name == "HNVSD");
        var inner = FinTsWire.Parse(data.Element(0));
        var shk = inner.First(s => s.Name == "HNSHK");
        var sha = inner.First(s => s.Name == "HNSHA");
        var key = outer.First(s => s.Name == "HNVSK");
        return new MockRequest
        {
            Raw = message,
            DialogId = header.Element(2),
            MessageNumber = int.Parse(header.Element(3), CultureInfo.InvariantCulture),
            SecurityFunction = shk.Element(1),
            SystemId = shk.Component(5, 2),
            UserId = key.Component(6, 2),
            Pin = sha.Component(2, 0),
            Tan = sha.Deg(2).Count > 1 ? sha.Component(2, 1) : null,
            Segments = inner.Where(s => !s.Name.StartsWith("HN", StringComparison.Ordinal)).ToList(),
            All = [.. outer.Where(s => s.Name != "HNVSD"), .. inner],
        };
    }

    /// <summary>
    /// The message reduced to what must match a recording: every segment with its number, version and data, with
    /// the values that differ between runs (sizes, ids, timestamps, references, secrets) replaced by "*".
    /// </summary>
    public static List<string> Normalize(string message, bool ignoreBpdVersion = false)
    {
        var request = Parse(message);
        var lines = new List<string>();
        foreach (var s in request.All)
        {
            var elements = s.Elements.Select(e => e.ToList()).ToList();
            void Mask(int element, int component)
            {
                if (element < elements.Count && component < elements[element].Count) elements[element][component] = "*";
            }
            switch (s.Name)
            {
                case "HNHBK": Mask(0, 0); Mask(2, 0); break;
                case "HNVSK": Mask(3, 2); Mask(4, 1); Mask(4, 2); Mask(5, 3); break;
                case "HNSHK": Mask(2, 0); Mask(5, 2); Mask(7, 1); Mask(7, 2); break;
                case "HNSHA": Mask(0, 0); Mask(2, 0); break;
                case "HKIDN": Mask(2, 0); break;
                case "HKVVB":
                    Mask(3, 0);
                    Mask(4, 0);
                    if (ignoreBpdVersion)
                    {
                        Mask(0, 0);
                    }
                    break;
                case "HKEND": Mask(0, 0); break;
                default: break;
            }
            var masked = new FinTsSegment(s.Name, s.Number, s.Version, s.Reference, elements.Select(e => (IReadOnlyList<string>)e).ToList());
            lines.Add(FinTsWire.Write(masked));
        }
        return lines;
    }
}

/// <summary>Builds a bank answer: HIRMG/HIRMS plus data segments, numbered the way the real banks do.</summary>
internal sealed class ReplyBuilder
{
    private readonly List<string> _returnsGlobal = [];
    private readonly List<string> _body = [];
    private int _next = 3;

    /// <summary>Message-level return code, for instance <c>"0010::Nachricht entgegengenommen."</c>.</summary>
    public ReplyBuilder Global(params string[] codes)
    {
        _returnsGlobal.AddRange(codes);
        return this;
    }

    /// <summary>Segment-level return codes for the request segment <paramref name="forSegment"/>.</summary>
    public ReplyBuilder Codes(int forSegment, params string[] codes)
    {
        _body.Add($"HIRMS:{_next++}:2:{forSegment}+{string.Join('+', codes)}'");
        return this;
    }

    /// <summary>A data segment; elements are given in wire form (already escaped).</summary>
    public ReplyBuilder Segment(string name, int version, int forSegment, params string[] elements)
    {
        _body.Add($"{name}:{_next++}:{version}:{forSegment}+{string.Join('+', elements)}'");
        return this;
    }

    /// <summary>Segments copied from a recording (numbers kept as recorded).</summary>
    public ReplyBuilder Raw(IEnumerable<FinTsSegment> segments)
    {
        _body.Add(FinTsWire.Write(segments));
        return this;
    }

    public string Inner => $"HIRMG:2:2+{string.Join('+', _returnsGlobal.DefaultIfEmpty("0010::Nachricht entgegengenommen."))}'" + string.Concat(_body);

    public string Wrap(string dialogId, int messageNumber)
    {
        var inner = Inner;
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"HNVSK:998:3+PIN:1+998+1+2+1:20261109:210002+2:2:13:{FinTsWire.Binary(new string('\0', 8))}:5:1+280:99999999:99999999:V:0:0+0'");
        body.Append(CultureInfo.InvariantCulture, $"HNVSD:999:1+{FinTsWire.Binary(inner)}'");
        body.Append(CultureInfo.InvariantCulture, $"HNHBS:{_next + 1}:1+{messageNumber}'");
        var tail = $"+300+{dialogId}+{messageNumber}+{dialogId}:{messageNumber}'";
        var size = "HNHBK:1:3+".Length + 12 + tail.Length + body.Length;
        return $"HNHBK:1:3+{size:D12}{tail}{body}";
    }
}

/// <summary>An HTTP handler that plays the bank: every POST body is a Base64 FinTS message.</summary>
internal sealed class MockFinTsServer : HttpMessageHandler
{
    private readonly Func<MockRequest, string> _respond;

    public MockFinTsServer(Func<MockRequest, string> respond) => _respond = respond;

    public List<MockRequest> Requests { get; } = [];

    public HttpStatusCode? FailWith { get; set; }

    public Uri? LastUrl { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri;
        if (FailWith is { } status) return new HttpResponseMessage(status);
        Assert.Equal(HttpMethod.Post, request.Method);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var message = Encoding.Latin1.GetString(Convert.FromBase64String(body));
        var parsed = MockRequest.Parse(message);
        Requests.Add(parsed);
        var answer = _respond(parsed);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Convert.ToBase64String(Encoding.Latin1.GetBytes(answer)), Encoding.ASCII, "text/plain"),
        };
    }
}

/// <summary>
/// Replays a recording: checks each client message against the recorded one, answers with the recorded bank message.
/// <c>ignoreBpdVersion</c>: MoneyMoney announced BPD version 0 although the bank has version 9; we announce the cached version.
/// </summary>
internal sealed class ReplayBank
{
    private readonly List<string> _client;
    private readonly List<string> _bank;
    private readonly bool _ignoreBpdVersion;
    private int _next;

    public ReplayBank(FinTsFixture fixture, bool ignoreBpdVersion = false)
    {
        _ignoreBpdVersion = ignoreBpdVersion;
        _client = fixture.ClientMessages.ToList();
        _bank = fixture.BankMessages.ToList();
    }

    public int Served => _next;

    public int Expected => _client.Count;

    public string Respond(MockRequest request)
    {
        Assert.True(_next < _client.Count, "the client sent more messages than were recorded");
        Assert.Equal(MockRequest.Normalize(_client[_next], _ignoreBpdVersion), MockRequest.Normalize(request.Raw, _ignoreBpdVersion));
        return _bank[_next++];
    }
}

/// <summary>Test plumbing: a connector wired to a mock bank and a driver that answers challenges like the app does.</summary>
internal static class FinTsHarness
{
    public const string FakeProductId = "FAKE-PRODUCT-ID-0000";

    public static FinTsOptions Options => new(FakeProductId, "9.9.9");

    public static ConnectorContext Context(MockFinTsServer server, TimeProvider? clock = null) =>
        new(new BankHttpClient(server), clock ?? TimeProvider.System, NullLogger.Instance);

    /// <summary>Calls <paramref name="call"/> again and again like the sync runner; <paramref name="answerer"/> answers each challenge (null = give up).</summary>
    public static async Task<(ConnectorStep<T> Step, List<ConnectorChallenge> Challenges)> DriveAsync<T>(
        Func<ChallengeAnswer?, Task<ConnectorStep<T>>> call, Func<ConnectorChallenge, string?>? answerer = null)
    {
        var challenges = new List<ConnectorChallenge>();
        ChallengeAnswer? answer = null;
        for (var i = 0; i < 200; i++)
        {
            var step = await call(answer);
            if (step.Status != StepStatus.Challenge) return (step, challenges);
            challenges.Add(step.Challenge!);
            var value = answerer?.Invoke(step.Challenge!);
            if (answerer != null && value == null && step.Challenge!.Kind != ChallengeKind.Approval) return (step, challenges);
            answer = new ChallengeAnswer(step.Challenge!, value);
        }
        throw new TimeoutException("too many challenge rounds");
    }
}
