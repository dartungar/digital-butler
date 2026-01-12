using Dapper;
using DigitalButler.Common;

namespace DigitalButler.Data.Repositories;

public sealed class GoogleOAuthTokenRepository
{
    private readonly IButlerDb _db;

    public GoogleOAuthTokenRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<GoogleOAuthToken?> GetByUserIdAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, UserId, AccessToken, RefreshToken, ExpiresAt, Scope, CreatedAt, UpdatedAt
            FROM GoogleOAuthTokens
            WHERE UserId = @UserId;
            """;

        return await conn.QuerySingleOrDefaultAsync<GoogleOAuthToken>(sql, new { UserId = userId });
    }

    public async Task UpsertAsync(GoogleOAuthToken token, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        if (token.Id == Guid.Empty) token.Id = Guid.NewGuid();
        if (token.CreatedAt == default) token.CreatedAt = DateTimeOffset.UtcNow;
        token.UpdatedAt = DateTimeOffset.UtcNow;

        const string sql = """
            INSERT INTO GoogleOAuthTokens (Id, UserId, AccessToken, RefreshToken, ExpiresAt, Scope, CreatedAt, UpdatedAt)
            VALUES (@Id, @UserId, @AccessToken, @RefreshToken, @ExpiresAt, @Scope, @CreatedAt, @UpdatedAt)
            ON CONFLICT(UserId) DO UPDATE SET
                AccessToken = excluded.AccessToken,
                RefreshToken = COALESCE(excluded.RefreshToken, GoogleOAuthTokens.RefreshToken),
                ExpiresAt = excluded.ExpiresAt,
                Scope = excluded.Scope,
                UpdatedAt = excluded.UpdatedAt;
            """;

        await conn.ExecuteAsync(sql, new
        {
            token.Id,
            token.UserId,
            token.AccessToken,
            token.RefreshToken,
            token.ExpiresAt,
            token.Scope,
            token.CreatedAt,
            token.UpdatedAt
        });
    }

    public async Task DeleteByUserIdAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM GoogleOAuthTokens WHERE UserId = @UserId;", new { UserId = userId });
    }
}
