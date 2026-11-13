using OpenBudget.Assist;

namespace OpenBudget.Tests.Assist;

/// <summary>Answers with canned JSON (computed from the request) and records every request; never touches the network.</summary>
internal sealed class FakeAssistClient : IAssistClient
{
    private readonly Func<AssistRequest, AssistResponse> _answer;

    public FakeAssistClient(Func<AssistRequest, string?> json, bool enabled = true)
        : this(r => new AssistResponse(json(r), null, "fake", 100, 0, 50, TimeSpan.FromMilliseconds(5)), enabled)
    {
    }

    public FakeAssistClient(Func<AssistRequest, AssistResponse> answer, bool enabled = true)
    {
        _answer = answer;
        Enabled = enabled;
    }

    public bool Enabled { get; }

    public string Model => "fake-model";

    public List<AssistRequest> Requests { get; } = [];

    public Task<AssistResponse> CompleteAsync(AssistRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(_answer(request));
    }
}
