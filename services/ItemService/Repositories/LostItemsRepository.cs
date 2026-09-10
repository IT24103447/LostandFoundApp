using ItemService.Databases;
using ItemService.Models;
using MySqlConnector;

namespace ItemService.Repositories;

public class LostItemsRepository : ILostItemsRepository
{
    private readonly IDbConnectionFactory _db;

    public LostItemsRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task CreateAsync(LostItem item, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO lost_items
                (id, user_id, title, category, description, date_lost, last_known_location, hidden_information, status)
            VALUES
                (@id, @userId, @title, @category, @description, @dateLost, @lastKnownLocation, @hiddenInformation, @status);
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@userId", item.UserId.ToString());
        cmd.Parameters.AddWithValue("@title", item.Title);
        cmd.Parameters.AddWithValue("@category", item.Category);
        cmd.Parameters.AddWithValue("@description", item.Description);
        cmd.Parameters.AddWithValue("@dateLost", item.DateLost.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@lastKnownLocation", item.LastKnownLocation);
        cmd.Parameters.AddWithValue("@hiddenInformation", item.HiddenInformation);
        cmd.Parameters.AddWithValue("@status", item.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<LostItemPhoto> AddPhotoAsync(Guid lostItemId, string photoUrl, CancellationToken ct = default)
    {
        var photo = new LostItemPhoto
        {
            Id = Guid.NewGuid(),
            LostItemId = lostItemId,
            Url = photoUrl,
            CreatedAt = DateTime.UtcNow
        };

        const string sql = """
            INSERT INTO lost_item_photos (id, lost_item_id, url)
            VALUES (@id, @lostItemId, @url);
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", photo.Id.ToString());
        cmd.Parameters.AddWithValue("@lostItemId", lostItemId.ToString());
        cmd.Parameters.AddWithValue("@url", photoUrl);
        await cmd.ExecuteNonQueryAsync(ct);

        return photo;
    }

    public async Task DeletePhotosAsync(Guid lostItemId, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM lost_item_photos WHERE lost_item_id = @lostItemId;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@lostItemId", lostItemId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(LostItem item, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE lost_items
            SET title = @title,
                category = @category,
                description = @description,
                date_lost = @dateLost,
                last_known_location = @lastKnownLocation,
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
        cmd.Parameters.AddWithValue("@dateLost", item.DateLost.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@lastKnownLocation", item.LastKnownLocation);
        cmd.Parameters.AddWithValue("@hiddenInformation", item.HiddenInformation);
        cmd.Parameters.AddWithValue("@updatedAt", item.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<LostItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        const string itemSql = """
            SELECT id, user_id, title, category, description, date_lost, last_known_location,
                   hidden_information, status, created_at, updated_at
            FROM lost_items WHERE id = @id LIMIT 1;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        LostItem? item;
        await using (var cmd = new MySqlCommand(itemSql, conn))
        {
            cmd.Parameters.AddWithValue("@id", id.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            item = await reader.ReadAsync(ct) ? MapItem(reader) : null;
        }

        if (item is null) return null;

        const string photosSql = """
            SELECT id, lost_item_id, url, created_at
            FROM lost_item_photos WHERE lost_item_id = @id
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

    private static LostItem MapItem(MySqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        UserId = r.GetGuid(1),
        Title = r.GetString(2),
        Category = r.GetString(3),
        Description = r.GetString(4),
        DateLost = DateOnly.FromDateTime(r.GetDateTime(5)),
        LastKnownLocation = r.GetString(6),
        HiddenInformation = r.GetString(7),
        Status = Enum.Parse<LostItemStatus>(r.GetString(8)),
        CreatedAt = r.GetDateTime(9),
        UpdatedAt = r.GetDateTime(10),
    };

    private static LostItemPhoto MapPhoto(MySqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        LostItemId = r.GetGuid(1),
        Url = r.GetString(2),
        CreatedAt = r.GetDateTime(3),
    };
}
