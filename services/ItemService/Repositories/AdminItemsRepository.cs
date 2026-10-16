using ItemService.Databases;
using ItemService.Models;
using MySqlConnector;

namespace ItemService.Repositories;

public class AdminItemsRepository : IAdminItemsRepository
{
    private const string LostSelectSql = """
        SELECT id, user_id, title, description, status, created_at, deleted_at
        FROM lost_items WHERE id = @id LIMIT 1;
        """;

    private const string FoundSelectSql = """
        SELECT id, user_id, title, description, status, created_at, deleted_at
        FROM found_items WHERE id = @id LIMIT 1;
        """;

    private const string LostSoftDeleteSql = """
        UPDATE lost_items
        SET deleted_at = @deletedAt,
            updated_at = @deletedAt
        WHERE id = @id AND deleted_at IS NULL AND status = 'ACTIVE';
        """;

    private const string FoundSoftDeleteSql = """
        UPDATE found_items
        SET deleted_at = @deletedAt,
            updated_at = @deletedAt
        WHERE id = @id AND deleted_at IS NULL AND status = 'ACTIVE';
        """;

    private const string LostPhotosSql = """
        SELECT url FROM lost_item_photos
        WHERE lost_item_id = @id
        ORDER BY created_at, id;
        """;

    private const string FoundPhotosSql = """
        SELECT url FROM found_item_photos
        WHERE found_item_id = @id
        ORDER BY created_at, id;
        """;

    private readonly IDbConnectionFactory _db;
    private readonly IDbSession _session;

    public AdminItemsRepository(IDbConnectionFactory db, IDbSession session)
    {
        _db = db;
        _session = session;
    }

    public async Task<AdminItemRecord?> GetByIdAsync(AdminItemType type, Guid id, CancellationToken ct = default)
    {
        var sql = type == AdminItemType.LOST ? LostSelectSql : FoundSelectSql;

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader, type) : null;
    }

    public async Task<bool> SoftDeleteActiveAsync(AdminItemType type, Guid id, DateTime deletedAt, CancellationToken ct = default)
    {
        var sql = type == AdminItemType.LOST ? LostSoftDeleteSql : FoundSoftDeleteSql;

        await using var lease = await _session.AcquireAsync(ct);
        await using var cmd = new MySqlCommand(sql, lease.Connection, lease.Transaction);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@deletedAt", deletedAt);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<string>> GetPhotoUrlsAsync(AdminItemType type, Guid id, CancellationToken ct = default)
    {
        var sql = type == AdminItemType.LOST ? LostPhotosSql : FoundPhotosSql;

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var urls = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            urls.Add(reader.GetString(0));
        }

        return urls;
    }

    private static AdminItemRecord Map(MySqlDataReader r, AdminItemType type) => new()
    {
        Id = r.GetGuid(0),
        Type = type,
        UserId = r.GetGuid(1),
        Title = r.GetString(2),
        Description = r.GetString(3),
        Status = r.GetString(4),
        CreatedAt = r.GetDateTime(5),
        DeletedAt = r.IsDBNull(6) ? null : r.GetDateTime(6),
    };
}
