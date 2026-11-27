using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A token as the settings page lists it (never the token itself).</summary>
public sealed record ApiTokenInfo(int Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? ExpiresAt, DateTime? RevokedAt)
{
    public bool IsActive(DateTime nowUtc) => RevokedAt == null && (ExpiresAt == null || ExpiresAt > nowUtc);
}

/// <summary>A newly created token: <see cref="Token"/> is shown once and never stored.</summary>
public sealed record CreatedApiToken(ApiTokenInfo Info, string Token);

/// <summary>
/// The signed-in user's personal read-only API tokens (create, list, revoke) for the MCP API (docs/mcp-api.md).
/// Works through the user-scoped context, so a user only ever sees and changes their own tokens.
/// </summary>
public sealed class ApiTokenService
{
    /// <summary>Every token starts with this, so a leaked one is recognisable.</summary>
    public const string TokenPrefix = "obk_";

    /// <summary>Characters of the token kept for display (the marker plus 8 random characters).</summary>
    public const int DisplayPrefixLength = 12;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _time;

    public ApiTokenService(IDbContextFactory<BudgetDbContext> dbFactory, ICurrentUser currentUser, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _currentUser = currentUser;
        _time = time;
    }

    /// <summary>A new random token: the marker and 256 random bits, base64url.</summary>
    public static string NewToken() => TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lower-case hex SHA-256 of <paramref name="token"/>, as stored.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<List<ApiTokenInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ApiTokens.AsNoTracking().ToListAsync(ct);
        return rows.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Select(Info).ToList();
    }

    /// <summary>Creates a token for the signed-in user; the returned token value is not kept anywhere.</summary>
    public async Task<CreatedApiToken> CreateAsync(string name, DateTime? expiresAtUtc, CancellationToken ct = default)
    {
        var userId = await _currentUser.GetUserIdAsync() ?? throw new AccessDeniedException("Not signed in.");
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A name is required.", nameof(name));
        if (trimmed.Length > ApiToken.NameMaxLength) trimmed = trimmed[..ApiToken.NameMaxLength].TrimEnd();
        var now = _time.GetUtcNow().UtcDateTime;
        if (expiresAtUtc is { } exp && exp <= now) throw new ArgumentException("The expiry must be in the future.", nameof(expiresAtUtc));

        var token = NewToken();
        var row = new ApiToken
        {
            OwnerUserId = userId,
            Name = trimmed,
            TokenHash = Hash(token),
            Prefix = token[..DisplayPrefixLength],
            CreatedAt = now,
            ExpiresAt = expiresAtUtc,
        };
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ApiTokens.Add(row);
        await db.SaveChangesAsync(ct);
        return new CreatedApiToken(Info(row), token);
    }

    /// <summary>Revokes one of the user's tokens at once; false when there is no such (own) token.</summary>
    public async Task<bool> RevokeAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row == null) return false;
        row.RevokedAt ??= _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static ApiTokenInfo Info(ApiToken t) => new(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.ExpiresAt, t.RevokedAt);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
