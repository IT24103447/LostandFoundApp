using ItemService.Databases;
using ItemService.Models;
using MySqlConnector;

namespace ItemService.Repositories;

public class FoundItemsRepository : IFoundItemsRepository
{
    private readonly IDbConnectionFactory _db;

    public FoundItemsRepository(IDbConnectionFactory db)
    {
        _db = db;
    }
    
    // HiddenInformation is stored internally and not exposed to frontend DTOs.
    public async Task CreateAsync(FoundItem item, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO found_items
                (id, user_id, title, category, description, date_found, location_found, hidden_information, status)
            VALUES
                (@id, @userId, @title, @category, @description, @dateFound, @locationFound, @hiddenInformation, @status);
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@userId", item.UserId.ToString());
        cmd.Parameters.AddWithValue("@title", item.Title);
        cmd.Parameters.AddWithValue("@category", item.Category);
        cmd.Parameters.AddWithValue("@description", item.Description);
        cmd.Parameters.AddWithValue("@dateFound", item.DateFound.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@locationFound", item.LocationFound);
        cmd.Parameters.AddWithValue("@hiddenInformation", item.HiddenInformation);
        cmd.Parameters.AddWithValue("@status", item.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<FoundItemPhoto> AddPhotoAsync(Guid foundItemId, string photoUrl, CancellationToken ct = default)
    {
        var photo = new FoundItemPhoto
        {
            Id = Guid.NewGuid(),
            FoundItemId = foundItemId,
            Url = photoUrl,
            CreatedAt = DateTime.UtcNow
        };

        const string sql = """
            INSERT INTO found_item_photos (id, found_item_id, url)
            VALUES (@id, @foundItemId, @url);
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", photo.Id.ToString());
        cmd.Parameters.AddWithValue("@foundItemId", foundItemId.ToString());
        cmd.Parameters.AddWithValue("@url", photoUrl);
        await cmd.ExecuteNonQueryAsync(ct);

        return photo;
    }

    public async Task DeletePhotosAsync(Guid foundItemId, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM found_item_photos WHERE found_item_id = @foundItemId;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@foundItemId", foundItemId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(FoundItem item, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE found_items
            SET title = @title,
                category = @category,
                description = @description,
                date_found = @dateFound,
                location_found = @locationFound,
                hidden_information = @hiddenInformation,
                updated_at = @updatedAt
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@title", item.Title);
        cmd.Parameters.AddWithValue("@category", item.Category);
        cmd.Parameters.AddWithValue("@description", item.Description);
        cmd.Parameters.AddWithValue("@dateFound", item.DateFound.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@locationFound", item.LocationFound);
        cmd.Parameters.AddWithValue("@hiddenInformation", item.HiddenInformation);
        cmd.Parameters.AddWithValue("@updatedAt", item.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<FoundItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        const string itemSql = """
            SELECT id, user_id, title, category, description, date_found, location_found,
                   hidden_information, status, created_at, updated_at
            FROM found_items WHERE id = @id LIMIT 1;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        FoundItem? item;
        await using (var cmd = new MySqlCommand(itemSql, conn))
        {
            cmd.Parameters.AddWithValue("@id", id.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            item = await reader.ReadAsync(ct) ? MapItem(reader) : null;
        }

        if (item is null) return null;

        const string photosSql = """
            SELECT id, found_item_id, url, created_at
            FROM found_item_photos WHERE found_item_id = @id
            ORDER BY created_at ASC;
            """;
        await using (var cmd = new MySqlCommand(photosSql, conn))
        {
            cmd.Parameters.AddWithValue("@id", id.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                item.Photos.Add(MapPhoto(reader));
            }
        }

        return item;
    }

    private static FoundItem MapItem(MySqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        UserId = r.GetGuid(1),
        Title = r.GetString(2),
        Category = r.GetString(3),
        Description = r.GetString(4),
        DateFound = DateOnly.FromDateTime(r.GetDateTime(5)),
        LocationFound = r.GetString(6),
        HiddenInformation = r.GetString(7),
        Status = Enum.Parse<FoundItemStatus>(r.GetString(8)),
        CreatedAt = r.GetDateTime(9),
        UpdatedAt = r.GetDateTime(10),
    };

    private static FoundItemPhoto MapPhoto(MySqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        FoundItemId = r.GetGuid(1),
        Url = r.GetString(2),
        CreatedAt = r.GetDateTime(3),
    };
}
