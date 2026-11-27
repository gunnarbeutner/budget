using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The read-only MCP API (/api/mcp/v1) end to end over the AccessWorld: personal API tokens (created once, stored as a
/// hash, revocable), the bearer scheme that works only there, and per-user scoping of every endpoint.
/// </summary>
public sealed class McpApiHttpTests : IDisposable
{
    private readonly AppTestHost _host = new(("McpApi:Enabled", "true"));

    public void Dispose() => _host.Dispose();

    private async Task<string> UserIdAsync(string name)
    {
        await using var db = await _host.System.CreateDbContextAsync();
        return await db.Users.Where(u => u.UserName == name).Select(u => u.Id).SingleAsync();
    }

    /// <summary>Runs <paramref name="action"/> with the app's services acting for <paramref name="userName"/>.</summary>
    private async Task<T> AsUserAsync<T>(string userName, Func<ApiTokenService, Task<T>> action)
    {
        var userId = await UserIdAsync(userName);
        await using var scope = _host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ActingUser>().Set(userId);
        return await action(scope.ServiceProvider.GetRequiredService<ApiTokenService>());
    }

    private Task<CreatedApiToken> CreateTokenAsync(string userName, DateTime? expires = null) =>
        AsUserAsync(userName, s => s.CreateAsync("Claude", expires));

    private HttpClient BearerClient(string token)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> GetJsonAsync(HttpClient client, string path)
    {
        var (status, body) = await AppTestHost.GetAsync(client, path);
        Assert.True(status != HttpStatusCode.InternalServerError, $"{path}: {string.Join("\n", _host.LoggedProblems)}");
        return (status, string.IsNullOrEmpty(body) ? default : JsonElement.Parse(body));
    }

    private static List<string> Names(JsonElement array, string property = "name") =>
        array.EnumerateArray().Select(e => e.GetProperty(property).GetString()!).ToList();

    [Fact]
    public async Task A_created_token_is_shown_once_and_stored_only_as_its_hash()
    {
        await _host.SeedWorldAsync();
        var created = await CreateTokenAsync("alice", DateTime.UtcNow.AddDays(30));

        Assert.StartsWith(ApiTokenService.TokenPrefix, created.Token, StringComparison.Ordinal);
        Assert.True(created.Token.Length >= ApiTokenService.TokenPrefix.Length + 43, "256 random bits");
        Assert.Equal(created.Token[..ApiTokenService.DisplayPrefixLength], created.Info.Prefix);

        await using (var db = await _host.System.CreateDbContextAsync())
        {
            var row = await db.ApiTokens.SingleAsync();
            Assert.Equal(ApiTokenService.Hash(created.Token), row.TokenHash);
            Assert.Equal(64, row.TokenHash.Length);
            Assert.NotEqual(created.Token, row.TokenHash);
            Assert.Equal(await UserIdAsync("alice"), row.OwnerUserId);
            Assert.Null(row.LastUsedAt);
            Assert.NotNull(row.ExpiresAt);
        }

        // The list never carries the token, only its prefix; another user doesn't see it.
        var listed = Assert.Single(await AsUserAsync("alice", s => s.ListAsync()));
        Assert.Equal("Claude", listed.Name);
        Assert.Empty(await AsUserAsync("bob", s => s.ListAsync()));

        // Two tokens never collide.
        var second = await CreateTokenAsync("alice");
        Assert.NotEqual(created.Token, second.Token);
    }

    [Fact]
    public async Task A_revoked_token_stops_working_at_once_and_only_its_owner_can_revoke_it()
    {
        await _host.SeedWorldAsync();
        var created = await CreateTokenAsync("alice");
        using var client = BearerClient(created.Token);
        Assert.Equal(HttpStatusCode.OK, (await GetJsonAsync(client, "/api/mcp/v1/accounts")).Status);

        await using (var db = await _host.System.CreateDbContextAsync())
            Assert.NotNull((await db.ApiTokens.SingleAsync()).LastUsedAt);

        Assert.False(await AsUserAsync("bob", s => s.RevokeAsync(created.Info.Id)));
        Assert.Equal(HttpStatusCode.OK, (await GetJsonAsync(client, "/api/mcp/v1/accounts")).Status);

        Assert.True(await AsUserAsync("alice", s => s.RevokeAsync(created.Info.Id)));
        var (status, json) = await GetJsonAsync(client, "/api/mcp/v1/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("unauthorized", json.GetProperty("error").GetString());
        Assert.NotNull(Assert.Single(await AsUserAsync("alice", s => s.ListAsync())).RevokedAt);
    }

    [Fact]
    public async Task Missing_unknown_expired_and_cookie_credentials_are_refused()
    {
        await _host.SeedWorldAsync();

        using (var anonymous = _host.CreateClient())
        {
            var (status, json) = await GetJsonAsync(anonymous, "/api/mcp/v1/accounts");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("unauthorized", json.GetProperty("error").GetString());
        }

        using (var unknown = BearerClient(ApiTokenService.NewToken()))
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetJsonAsync(unknown, "/api/mcp/v1/accounts")).Status);

        // An expired token (written directly: the service refuses a past expiry).
        var expired = ApiTokenService.NewToken();
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            db.ApiTokens.Add(new ApiToken
            {
                OwnerUserId = await UserIdAsync("alice"),
                Name = "old",
                TokenHash = ApiTokenService.Hash(expired),
                Prefix = expired[..12],
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }
        using (var client = BearerClient(expired))
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetJsonAsync(client, "/api/mcp/v1/accounts")).Status);

        // The login cookie alone doesn't open the API.
        using var cookie = await _host.LoggedInClientAsync("alice");
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetJsonAsync(cookie, "/api/mcp/v1/accounts")).Status);
    }

    [Fact]
    public async Task An_api_token_opens_no_route_outside_the_mcp_api()
    {
        await _host.SeedWorldAsync();
        var created = await CreateTokenAsync("alice");
        using var client = BearerClient(created.Token);

        foreach (var path in new[] { "/", "/projects/1/browse", "/Account", "/api-tokens", "/connections", "/statements/1", "/api/other" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            // Not signed in: a redirect to the login page, or 401 for API-style paths.
            Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
                $"{path} -> {(int)response.StatusCode}");
            if (response.StatusCode == HttpStatusCode.Redirect)
                Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }
        using var form = new FormUrlEncodedContent([KeyValuePair.Create("ids", "1")]);
        using var post = await client.PostAsync(new Uri("/statements/zip", UriKind.Relative), form);
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, post.StatusCode);   // refused at authentication, not looked up
    }

    [Fact]
    public async Task Each_token_sees_only_what_its_user_sees()
    {
        await _host.SeedWorldAsync(ProjectRole.Viewer);
        using var alice = BearerClient((await CreateTokenAsync("alice")).Token);
        using var bob = BearerClient((await CreateTokenAsync("bob")).Token);

        var aliceAccounts = Names((await GetJsonAsync(alice, "/api/mcp/v1/accounts")).Json.GetProperty("accounts"));
        Assert.Contains("Giro Private", aliceAccounts);
        Assert.DoesNotContain("Bob Giro", aliceAccounts);

        var (_, bobJson) = await GetJsonAsync(bob, "/api/mcp/v1/accounts");
        var bobAccounts = Names(bobJson.GetProperty("accounts"));
        Assert.Contains("Bob Giro", bobAccounts);
        Assert.Contains("Giro Shared", bobAccounts);   // shared with bob
        Assert.DoesNotContain("Giro Private", bobAccounts);
        var shared = bobJson.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Giro Shared");
        Assert.Equal("shared", shared.GetProperty("ownership").GetString());

        var bobBalances = Names((await GetJsonAsync(bob, "/api/mcp/v1/balances")).Json.GetProperty("balances"));
        Assert.DoesNotContain("Giro Private", bobBalances);

        var bobTx = (await GetJsonAsync(bob, "/api/mcp/v1/transactions?limit=500")).Json.GetProperty("transactions");
        var bobPayees = Names(bobTx, "payee");
        Assert.Contains("Payee Shared", bobPayees);
        Assert.Contains("Payee Bob", bobPayees);
        Assert.DoesNotContain("Payee Private", bobPayees);
        // Asking for the private account by id finds nothing rather than an error.
        var (status, byId) = await GetJsonAsync(bob, $"/api/mcp/v1/transactions?accountId={AccessWorld.AccountPrivate}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, byId.GetProperty("transactions").GetArrayLength());

        var alicePayees = Names((await GetJsonAsync(alice, "/api/mcp/v1/transactions")).Json.GetProperty("transactions"), "payee");
        Assert.Contains("Payee Private", alicePayees);
        Assert.DoesNotContain("Payee Bob", alicePayees);

        var bobSpending = (await GetJsonAsync(bob, "/api/mcp/v1/spending/summary?from=2026-03-01&to=2026-03-31")).Json;
        var bobExpenses = bobSpending.GetProperty("totals").EnumerateArray().Single().GetProperty("expenses").GetDecimal();
        Assert.DoesNotContain(-AccessWorld.PrivateCents / 100m, bobSpending.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("expenses").GetDecimal()));
        Assert.True(bobExpenses < 500m, $"bob's expenses {bobExpenses} include alice's private booking");

        var bobBudgets = Names((await GetJsonAsync(bob, "/api/mcp/v1/budgets?year=2026")).Json.GetProperty("projects"), "projectName");
        Assert.Contains("Shared", bobBudgets);
        Assert.DoesNotContain("Alice Private", bobBudgets);
        var aliceBudgets = Names((await GetJsonAsync(alice, "/api/mcp/v1/budgets?year=2026")).Json.GetProperty("projects"), "projectName");
        Assert.Contains("Alice Private", aliceBudgets);
    }

    [Fact]
    public async Task The_endpoints_answer_their_documented_shapes()
    {
        await _host.SeedWorldAsync();
        using var alice = BearerClient((await CreateTokenAsync("alice")).Token);

        var account = (await GetJsonAsync(alice, "/api/mcp/v1/accounts")).Json.GetProperty("accounts")[0];
        Assert.Equal(JsonValueKind.Number, account.GetProperty("id").ValueKind);
        Assert.Equal("EUR", account.GetProperty("currency").GetString());

        var balances = (await GetJsonAsync(alice, "/api/mcp/v1/balances")).Json;
        var wp = balances.GetProperty("balances").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "Giro Shared");
        Assert.Equal(AccessWorld.SharedCents / 100m, wp.GetProperty("balance").GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", wp.GetProperty("balance").GetProperty("currency").GetString());

        var (_, search) = await GetJsonAsync(alice, "/api/mcp/v1/transactions?query=payee%20shared&from=2026-03-01&to=2026-03-10");
        var tx = Assert.Single(search.GetProperty("transactions").EnumerateArray());
        Assert.Equal("2026-03-10", tx.GetProperty("date").GetString());
        Assert.Equal(-100m, tx.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("Giro Shared", tx.GetProperty("account").GetProperty("name").GetString());
        Assert.Equal($"Leaf{AccessWorld.SharedLeaf}", tx.GetProperty("category").GetProperty("name").GetString());

        // The date range is inclusive of 'to' and excludes what lies outside.
        Assert.Equal(0, (await GetJsonAsync(alice, "/api/mcp/v1/transactions?to=2026-03-09")).Json.GetProperty("transactions").GetArrayLength());
        // Category by group name covers its categories; by an unknown name, nothing.
        Assert.Equal(3, (await GetJsonAsync(alice, $"/api/mcp/v1/transactions?category=Group{AccessWorld.SharedGroup}")).Json.GetProperty("transactions").GetArrayLength());
        Assert.Equal(0, (await GetJsonAsync(alice, "/api/mcp/v1/transactions?category=Nope")).Json.GetProperty("transactions").GetArrayLength());

        var limited = (await GetJsonAsync(alice, "/api/mcp/v1/transactions?limit=1")).Json;
        Assert.Equal(1, limited.GetProperty("transactions").GetArrayLength());
        Assert.Equal(3, limited.GetProperty("totalCount").GetInt32());
        Assert.Equal(500, (await GetJsonAsync(alice, "/api/mcp/v1/transactions?limit=100000")).Json.GetProperty("limit").GetInt32());
        Assert.Equal(50, (await GetJsonAsync(alice, "/api/mcp/v1/transactions")).Json.GetProperty("limit").GetInt32());

        var byCategory = (await GetJsonAsync(alice, "/api/mcp/v1/spending/summary?from=2026-03-01&to=2026-03-31&groupBy=category")).Json;
        var group = Assert.Single(byCategory.GetProperty("groups").EnumerateArray());
        Assert.Equal(-(AccessWorld.SharedCents + AccessWorld.PrivateCents + AccessWorld.PropertyCents) / 100m, group.GetProperty("expenses").GetDecimal());
        Assert.Equal("category", byCategory.GetProperty("groupBy").GetString());
        Assert.Equal("2026-03-31", byCategory.GetProperty("to").GetString());
        var byMonth = (await GetJsonAsync(alice, "/api/mcp/v1/spending/summary?from=2026-01-01&to=2026-06-30&groupBy=month")).Json;
        Assert.Equal("2026-03", Assert.Single(byMonth.GetProperty("groups").EnumerateArray()).GetProperty("key").GetString());

        var budgets = (await GetJsonAsync(alice, "/api/mcp/v1/budgets?year=2026")).Json.GetProperty("projects");
        var sharedBudget = budgets.EnumerateArray().Single(p => p.GetProperty("projectName").GetString() == "Shared");
        Assert.Equal("owner", sharedBudget.GetProperty("role").GetString());
        Assert.True(sharedBudget.GetProperty("totalBudget").GetDecimal() > 0);
        Assert.NotEqual(0, sharedBudget.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task The_settings_page_lists_the_users_tokens_by_prefix_only()
    {
        await _host.SeedWorldAsync();
        var created = await CreateTokenAsync("alice");
        using var alice = await _host.LoggedInClientAsync("alice");
        var (status, html) = await AppTestHost.GetAsync(alice, "/api-tokens");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("settings-nav-api-tokens", html, StringComparison.Ordinal);
        Assert.Contains(created.Info.Prefix, html, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Token, html, StringComparison.Ordinal);

        using var bob = await _host.LoggedInClientAsync("bob");
        var (_, bobHtml) = await AppTestHost.GetAsync(bob, "/api-tokens");
        Assert.DoesNotContain(created.Info.Prefix, bobHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_parameters_and_unknown_paths_answer_in_the_error_format()
    {
        await _host.SeedWorldAsync();
        using var alice = BearerClient((await CreateTokenAsync("alice")).Token);
        foreach (var path in new[]
                 {
                     "/api/mcp/v1/transactions?from=03/01/2026", "/api/mcp/v1/transactions?limit=0", "/api/mcp/v1/transactions?limit=abc",
                     "/api/mcp/v1/transactions?accountId=x", "/api/mcp/v1/transactions?from=2026-03-02&to=2026-03-01",
                     "/api/mcp/v1/spending/summary?groupBy=week", "/api/mcp/v1/budgets?year=twenty",
                 })
        {
            var (status, json) = await GetJsonAsync(alice, path);
            Assert.True(status == HttpStatusCode.BadRequest, $"{path} -> {(int)status}");
            Assert.Equal("invalid_parameter", json.GetProperty("error").GetString());
            Assert.False(string.IsNullOrEmpty(json.GetProperty("message").GetString()));
        }
        var (missing, missingJson) = await GetJsonAsync(alice, "/api/mcp/v1/nothing");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("not_found", missingJson.GetProperty("error").GetString());
    }
}

/// <summary>With McpApi:Enabled off (the default) every MCP path is 404, token or not.</summary>
public sealed class McpApiDisabledTests : IDisposable
{
    private readonly AppTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_api_is_not_found_while_disabled()
    {
        await _host.SeedWorldAsync();
        string userId;
        await using (var db = await _host.System.CreateDbContextAsync())
            userId = await db.Users.Where(u => u.UserName == "alice").Select(u => u.Id).SingleAsync();
        await using var scope = _host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ActingUser>().Set(userId);
        var created = await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync("x", null);

        using var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        foreach (var path in new[] { "/api/mcp/v1/accounts", "/api/mcp/v1/transactions", "/api/mcp/v1/budgets" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
