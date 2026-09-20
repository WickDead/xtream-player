using Dapper;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Persistence;

/// <summary>
/// Per-profile hidden categories, keyed by id with a name fallback.
/// </summary>
/// <remarks>
/// The previous app kept this in localStorage as a JSON array of category
/// <em>display names</em>, per scope but shared across every provider
/// (<c>src/hooks/useHiddenCategories.ts</c>). That broke in two directions: a
/// provider renaming a category silently unhid it, and hiding a category in one
/// provider hid a same-named category in another.
///
/// Storing both id and name fixes both. See
/// <see cref="HiddenCategoryResolver"/> for the matching rule.
/// </remarks>
public sealed class HiddenCategoryRepository(IDbConnectionFactory connections)
    : IHiddenCategoryRepository
{
    public async Task<IReadOnlyList<HiddenCategory>> ListAsync(
        ProfileScope scope, CategoryScope categoryScope, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT profile_id, scope, category_id, category_name, created_at
            FROM hidden_categories
            WHERE profile_id = @ProfileId AND scope = @Scope;
            """,
            new { ProfileId = scope.Value, Scope = categoryScope.ToStorageString() },
            cancellationToken: ct));

        return [.. rows.Select(Map)];
    }

    public async Task HideAsync(
        ProfileScope scope,
        CategoryScope categoryScope,
        string categoryId,
        string? categoryName,
        CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO hidden_categories (profile_id, scope, category_id, category_name, created_at)
            VALUES (@ProfileId, @Scope, @CategoryId, @CategoryName, @CreatedAt)
            ON CONFLICT (profile_id, scope, category_id) DO UPDATE SET
                category_name = excluded.category_name;
            """,
            new
            {
                ProfileId = scope.Value,
                Scope = categoryScope.ToStorageString(),
                CategoryId = categoryId,
                CategoryName = categoryName,
                CreatedAt = SqlDate.Iso(DateTimeOffset.UtcNow),
            },
            cancellationToken: ct));
    }

    public async Task UnhideAsync(
        ProfileScope scope, CategoryScope categoryScope, string categoryId,
        CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM hidden_categories
            WHERE profile_id = @ProfileId AND scope = @Scope AND category_id = @CategoryId;
            """,
            new
            {
                ProfileId = scope.Value,
                Scope = categoryScope.ToStorageString(),
                CategoryId = categoryId,
            },
            cancellationToken: ct));
    }

    public async Task RepointAsync(
        ProfileScope scope,
        CategoryScope categoryScope,
        string oldCategoryId,
        string newCategoryId,
        CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        // OR IGNORE: if the new id is already hidden, the stale row is simply
        // redundant and gets dropped below.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE OR IGNORE hidden_categories
            SET category_id = @NewId
            WHERE profile_id = @ProfileId AND scope = @Scope AND category_id = @OldId;

            DELETE FROM hidden_categories
            WHERE profile_id = @ProfileId AND scope = @Scope AND category_id = @OldId;
            """,
            new
            {
                ProfileId = scope.Value,
                Scope = categoryScope.ToStorageString(),
                OldId = oldCategoryId,
                NewId = newCategoryId,
            },
            cancellationToken: ct));
    }

    public async Task ClearAsync(
        ProfileScope scope, CategoryScope? categoryScope = null, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM hidden_categories
            WHERE profile_id = @ProfileId
              AND (@Scope IS NULL OR scope = @Scope);
            """,
            new { ProfileId = scope.Value, Scope = categoryScope?.ToStorageString() },
            cancellationToken: ct));
    }

    private static HiddenCategory Map(Row row) => new()
    {
        ProfileId = row.profile_id,
        Scope = CategoryScopeExtensions.TryParseStorageString(row.scope, out var scope)
            ? scope
            : CategoryScope.Live,
        CategoryId = row.category_id,
        CategoryName = row.category_name,
        CreatedAt = SqlDate.Parse(row.created_at),
    };

    private sealed class Row
    {
        public string profile_id { get; set; } = string.Empty;
        public string scope { get; set; } = string.Empty;
        public string category_id { get; set; } = string.Empty;
        public string? category_name { get; set; }
        public string? created_at { get; set; }
    }
}
