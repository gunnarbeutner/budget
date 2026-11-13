using System.Net;
using Microsoft.Extensions.Logging;
using OpenBudget.Assist;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>Validation of model answers and the HTTP client (retries, the key never logged).</summary>
public sealed class AssistSchemaTests
{
    private static readonly Dictionary<string, PromptCategory> Categories = new(StringComparer.Ordinal)
    {
        ["c1"] = new("c1", 101, "Haushalt › Lebensmittel", false),
        ["c2"] = new("c2", 102, "Einnahmen › Gehalt", true),
    };

    private static AssistResponse Answer(string? json, string? refusal = null) => new(json, refusal, "fake", 1, 0, 1, TimeSpan.Zero);

    private static AssistRedactor RedactorWith(params long[] ids)
    {
        var r = new AssistRedactor([]);
        foreach (var id in ids) r.IdFor(id);
        return r;
    }

    [Fact]
    public void Unknown_bookings_and_category_ids_are_dropped_and_confidence_clamped()
    {
        var json = """
            {"suggestions":[
              {"tx":"t1","categoryId":"c1","confidence":1.7,"rationale":"Supermarkt","rule":{"expression":"name:\"Example Market\"","scope":"payee"}},
              {"tx":"t2","categoryId":"c99","confidence":0.9,"rationale":"?","rule":null},
              {"tx":"t9","categoryId":"c1","confidence":0.9,"rationale":"unknown booking","rule":null},
              {"tx":"t1","categoryId":"c2","confidence":0.9,"rationale":"duplicate","rule":null}
            ]}
            """;
        var list = CategoryPrompt.ParseAnswer(Answer(json), RedactorWith(10, 20), Categories);

        Assert.Equal(2, list.Count);
        Assert.Equal(new AiCategorySuggestion(10, 101, 1, "Supermarkt", "name:\"Example Market\"", "payee"), list[0]);
        Assert.Equal(20, list[1].TransactionId);
        Assert.Null(list[1].CategoryId);
        Assert.Equal(0, list[1].Confidence);
    }

    [Theory]
    [InlineData(null, "refused")]
    [InlineData("", null)]
    [InlineData("{not json", null)]
    [InlineData("{\"other\":[]}", null)]
    public void Refusals_and_broken_answers_fail_clearly(string? json, string? refusal) =>
        Assert.Throws<AssistException>(() => CategoryPrompt.ParseAnswer(Answer(json, refusal), RedactorWith(1), Categories));

    [Fact]
    public void Schemas_are_strict()
    {
        foreach (var schema in new[] { CategoryPrompt.Schema(), BudgetSuggestionService.Schema() })
        {
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            var text = schema.ToJsonString();
            Assert.DoesNotContain("\"additionalProperties\":true", text, StringComparison.Ordinal);
        }
        var body = OpenAiAssistClient.BuildBody("m", new AssistRequest("x", CategoryPrompt.Schema(), "sys", "user"));
        Assert.True(body["text"]!["format"]!["strict"]!.GetValue<bool>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("system", body["input"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void The_responses_output_is_read_with_usage_and_refusal()
    {
        var ok = OpenAiAssistClient.Parse("""
            {"model":"m1","output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"{\"a\":1}"}]}],
             "usage":{"input_tokens":1200,"input_tokens_details":{"cached_tokens":1024},"output_tokens":80}}
            """, TimeSpan.FromSeconds(1));
        Assert.Equal(("{\"a\":1}", null, 1200, 1024, 80), (ok.Json, ok.Refusal, ok.InputTokens, ok.CachedTokens, ok.OutputTokens));

        var no = OpenAiAssistClient.Parse("""{"output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""", TimeSpan.Zero);
        Assert.Equal("no", no.Refusal);
    }

    private sealed class Handler(Queue<HttpResponseMessage> answers) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(answers.Dequeue());
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ListLogger : ILogger<OpenAiAssistClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + " " + exception);
    }

    [Fact]
    public async Task Rate_limits_and_server_errors_are_retried_and_the_key_is_never_logged()
    {
        const string Key = "sk-test-not-a-real-key-123";
        using var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        using var broken = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{}") };
        using var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"output":[{"type":"message","content":[{"type":"output_text","text":"{}"}]}]}"""),
        };
        using var handler = new Handler(new Queue<HttpResponseMessage>([limited, broken, ok]));
        var log = new ListLogger();
        var waits = new List<TimeSpan>();
        var client = new OpenAiAssistClient(new Factory(handler), AssistOptions.From(n => n == AssistOptions.ApiKeyVariable ? Key : null), log,
            (d, _) => { waits.Add(d); return Task.CompletedTask; });

        var answer = await client.CompleteAsync(new AssistRequest("x", CategoryPrompt.Schema(), "sys", "user"));

        Assert.Equal("{}", answer.Json);
        Assert.Equal(3, handler.Seen.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
        Assert.Equal("Bearer " + Key, handler.Seen[0].Headers.Authorization!.ToString());
        Assert.Equal("https://api.openai.com/v1/responses", handler.Seen[0].RequestUri!.ToString());
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains(Key, StringComparison.Ordinal));
        Assert.DoesNotContain(Key, AssistOptions.From(n => n == AssistOptions.ApiKeyVariable ? Key : null).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        using var bad = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"Invalid schema"}}""") };
        using var handler = new Handler(new Queue<HttpResponseMessage>([bad]));
        var client = new OpenAiAssistClient(new Factory(handler), AssistOptions.From(n => n == AssistOptions.ApiKeyVariable ? "k" : null), new ListLogger(),
            (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<AssistException>(() => client.CompleteAsync(new AssistRequest("x", CategoryPrompt.Schema(), "s", "u")));
        Assert.Contains("Invalid schema", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public void Without_a_key_the_ai_is_off_and_defaults_apply()
    {
        var o = AssistOptions.From(_ => null);
        Assert.False(o.Enabled);
        Assert.Equal("gpt-6.1-terra", o.Model);
        var custom = AssistOptions.From(n => n switch
        {
            AssistOptions.ApiKeyVariable => "k",
            AssistOptions.BaseUrlVariable => "http://localhost:11434/v1",
            _ => null,
        });
        Assert.Equal("http://localhost:11434/v1/", custom.BaseUrl.ToString());
    }
}
