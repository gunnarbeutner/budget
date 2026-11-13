using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Assist;

/// <summary>
/// One POST to the OpenAI API with retries: 429 and 5xx are retried with exponential backoff (honouring Retry-After),
/// every attempt has its own timeout. The key is only ever put into the Authorization header and never logged.
/// </summary>
internal static class OpenAiTransport
{
    public static async Task<(string Body, TimeSpan Elapsed)> PostAsync(
        IHttpClientFactory http, AssistOptions options, ILogger log, Func<TimeSpan, CancellationToken, Task> delay, string path, string body, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var attempt = 0;
        while (true)
        {
            attempt++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);
            HttpStatusCode? status = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl, path));
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                var client = http.CreateClient(AssistOptions.HttpClientName);
                using var response = await client.SendAsync(message, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                if (response.IsSuccessStatusCode) return (text, watch.Elapsed);
                status = response.StatusCode;
                retryAfter = response.Headers.RetryAfter?.Delta;
                if (!IsTransient(response.StatusCode) || attempt >= options.MaxAttempts)
                    throw new AssistException($"The AI service answered {(int)response.StatusCode}: {ErrorMessage(text)}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < options.MaxAttempts)
            {
                // per-attempt timeout: retry
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AssistException("The AI service did not answer in time.");
            }
            catch (HttpRequestException ex) when (attempt < options.MaxAttempts)
            {
                log.LogInformation(ex, "AI request failed; retrying");
            }
            catch (HttpRequestException ex)
            {
                throw new AssistException("The AI service cannot be reached.", ex);
            }
            var wait = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            if (wait > TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
            log.LogInformation("AI request attempt {Attempt} gave {Status}; next try in {Wait}", attempt, status, wait);
            await delay(wait, ct);
        }
    }

    private static bool IsTransient(HttpStatusCode code) => code == HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static string ErrorMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? "no details";
        }
        catch (JsonException)
        {
            return "no details";
        }
    }
}
