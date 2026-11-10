using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBudget.Compat;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Pins backward compatibility with the already-installed MoneyMoneyExport tool: it POSTs the raw
/// SQLite file (application/octet-stream) to the old /api/moneymoney-db/{name} route with
/// "Authorization: Bearer" carrying the token the server knows as MONEYMONEY_DB_UPLOAD_TOKEN.
/// These shims exist purely for that tool and can go once it is retired.
/// </summary>
public sealed class LegacyExporterCompatTests : IDisposable
{
    private const string Token = "legacy-token";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ob-compat-{Guid.NewGuid():N}.db");
    private readonly string _snapshotPath = Path.Combine(Path.GetTempPath(), $"ob-compat-src-{Guid.NewGuid():N}.sqlite");
    private readonly WebApplicationFactory<Program> _factory;

    public LegacyExporterCompatTests()
    {
        BuildSnapshot(_snapshotPath);
        MigratedDatabase.CopyTo(_dbPath);

#pragma warning disable CA2000 // the derived factory (_factory) is disposed in Dispose and owns the root factory
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            // Host settings rather than process environment variables, so this class runs in parallel with others.
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [LegacyCompat.UploadTokenVariable] = "",
                [LegacyCompat.LegacyUploadTokenVariable] = Token,
            }));
            b.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<BudgetDbContext>)
                             || d.ServiceType == typeof(DbContextOptions<BudgetDbContext>)
                             || d.ServiceType == typeof(DbContextOptions))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContextFactory<BudgetDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
            });
        });
#pragma warning restore CA2000
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _snapshotPath })
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
    }

    /// <summary>A minimal database in the shape the old exporter produces.</summary>
    private static void BuildSnapshot(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
            CREATE TABLE account_groups (name TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
            CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, unformatted_purpose TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER);
            INSERT INTO accounts (name, currency, type, balances) VALUES ('Giro', 'EUR', 1, '11:100.000000,3:EUR,');
            INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, category_key, checkmark, splitted)
                VALUES (1, -12.5, 'EUR', 1700000000, 'Shop', 0, 0, 0);
            """;
        cmd.ExecuteNonQuery();
    }

    private byte[] SnapshotBytes() => File.ReadAllBytes(_snapshotPath);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[]? body, string? token = Token)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        return await client.SendAsync(request);
    }

    private async Task<(int Accounts, int Transactions)> CountsAsync()
    {
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<BudgetDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Accounts.CountAsync(), await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Old_exporter_route_with_legacy_token_imports_into_renamed_tables()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/moneymoney-db/household", SnapshotBytes());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("household", json.RootElement.GetProperty("sourceName").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("accountsImported").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("transactionsImported").GetInt32());
        Assert.Equal((1, 1), await CountsAsync());
    }

    [Fact]
    public async Task New_route_accepts_the_same_request()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/import/moneymoney/household", SnapshotBytes());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((1, 1), await CountsAsync());
    }

    [Fact]
    public async Task Old_route_rejects_wrong_token_and_missing_token()
    {
        using var wrong = await SendAsync(HttpMethod.Post, "/api/moneymoney-db/household", SnapshotBytes(), "wrong");
        using var missing = await SendAsync(HttpMethod.Post, "/api/moneymoney-db/household", SnapshotBytes(), null);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal((0, 0), await CountsAsync());
    }

    [Fact]
    public async Task Old_route_rejects_non_sqlite_payload_with_400()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/moneymoney-db/household", new byte[64]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Old_portfolioperformance_route_is_still_served()
    {
        // Not a ZIP: reaching the payload check (400) proves the route + auth are intact.
        using var response = await SendAsync(HttpMethod.Post, "/api/portfolioperformance/household", new byte[64]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Old_delete_route_removes_the_source()
    {
        using var upload = await SendAsync(HttpMethod.Post, "/api/moneymoney-db/household", SnapshotBytes());
        Assert.Equal((1, 1), await CountsAsync());

        using var response = await SendAsync(HttpMethod.Delete, "/api/moneymoney-sources/household", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((0, 0), await CountsAsync());
    }
}

public class LegacyCompatTokenTests
{
    private static Func<string, string?> Env(params (string, string)[] vars) =>
        name => vars.Where(v => v.Item1 == name).Select(v => v.Item2).FirstOrDefault();

    [Fact]
    public void New_variable_wins_over_legacy()
    {
        var token = LegacyCompat.ResolveUploadToken(
            Env(("OPENBUDGET_UPLOAD_TOKEN", "new"), ("MONEYMONEY_DB_UPLOAD_TOKEN", "old")), null);
        Assert.Equal("new", token);
    }

    [Fact]
    public void Falls_back_to_legacy_variable()
    {
        Assert.Equal("old", LegacyCompat.ResolveUploadToken(Env(("MONEYMONEY_DB_UPLOAD_TOKEN", "old")), null));
    }

    [Fact]
    public void Returns_null_when_neither_is_set()
    {
        Assert.Null(LegacyCompat.ResolveUploadToken(Env(), null));
    }
}
