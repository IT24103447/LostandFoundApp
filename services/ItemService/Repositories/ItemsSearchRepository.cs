using ItemService.Databases;
using ItemService.Models;
using ItemService.Models.Dtos;
using MySqlConnector;

namespace ItemService.Repositories;

public class ItemsSearchRepository : IItemsSearchRepository
{
    private readonly IDbConnectionFactory _db;

    public ItemsSearchRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    // Shared predicate applied identically to both halves of the UNION.
    // Uses "hasX = 0 OR ..." flags rather than "@param IS NULL" so every
    // parameter always has a concrete, correctly-typed value bound to it.
    private const string LostWhere = """
        li.status = 'ACTIVE'
          AND (@hasType = 0 OR @typeValue = 'LOST')
          AND (@hasCategory = 0 OR li.category = @category)
          AND (@hasDateFrom = 0 OR li.date_lost >= @dateFrom)
          AND (@hasDateTo = 0 OR li.date_lost <= @dateTo)
          AND (@hasKeyword = 0
               OR LOWER(li.title) LIKE @keywordPattern ESCAPE '\\'
               OR LOWER(li.description) LIKE @keywordPattern ESCAPE '\\'
               OR LOWER(li.last_known_location) LIKE @keywordPattern ESCAPE '\\')
        """;

    private const string FoundWhere = """
        fi.status = 'ACTIVE'
          AND (@hasType = 0 OR @typeValue = 'FOUND')
          AND (@hasCategory = 0 OR fi.category = @category)
          AND (@hasDateFrom = 0 OR fi.date_found >= @dateFrom)
          AND (@hasDateTo = 0 OR fi.date_found <= @dateTo)
          AND (@hasKeyword = 0
               OR LOWER(fi.title) LIKE @keywordPattern ESCAPE '\\'
               OR LOWER(fi.description) LIKE @keywordPattern ESCAPE '\\'
               OR LOWER(fi.location_found) LIKE @keywordPattern ESCAPE '\\')
        """;

    public async Task<PagedResultDto<ItemSummaryDto>> SearchAsync(ItemSearchQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 50 ? 20 : query.PageSize;
        var offset = (page - 1) * pageSize;

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var totalCount = await GetTotalCountAsync(conn, query, ct);

        var dataSql = $"""
            SELECT * FROM (
                SELECT
                    li.id            AS id,
                    'LOST'           AS item_type,
                    li.title         AS title,
                    li.category      AS category,
                    li.description   AS description,
                    li.date_lost     AS item_date,
                    li.last_known_location AS location,
                    li.status        AS status,
                    li.created_at    AS created_at,
                    (SELECT p.url FROM lost_item_photos p
                     WHERE p.lost_item_id = li.id
                     ORDER BY p.created_at ASC LIMIT 1) AS photo_url
                FROM lost_items li
                WHERE {LostWhere}

                UNION ALL

                SELECT
                    fi.id            AS id,
                    'FOUND'          AS item_type,
                    fi.title         AS title,
                    fi.category      AS category,
                    fi.description   AS description,
                    fi.date_found    AS item_date,
                    fi.location_found AS location,
                    fi.status        AS status,
                    fi.created_at    AS created_at,
                    (SELECT p.url FROM found_item_photos p
                     WHERE p.found_item_id = fi.id
                     ORDER BY p.created_at ASC LIMIT 1) AS photo_url
                FROM found_items fi
                WHERE {FoundWhere}
            ) combined
            ORDER BY created_at DESC
            LIMIT @pageSize OFFSET @offset;
            """;

        await using var cmd = new MySqlCommand(dataSql, conn);
        AddFilterParameters(cmd, query);
        cmd.Parameters.AddWithValue("@pageSize", pageSize);
        cmd.Parameters.AddWithValue("@offset", offset);

        var items = new List<ItemSummaryDto>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                items.Add(new ItemSummaryDto
                {
                    Id = reader.GetGuid(reader.GetOrdinal("id")),
                    ItemType = reader.GetString(reader.GetOrdinal("item_type")),
                    Title = reader.GetString(reader.GetOrdinal("title")),
                    Category = reader.GetString(reader.GetOrdinal("category")),
                    Description = reader.GetString(reader.GetOrdinal("description")),
                    Date = DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("item_date"))).ToString("yyyy-MM-dd"),
                    Location = reader.GetString(reader.GetOrdinal("location")),
                    Status = reader.GetString(reader.GetOrdinal("status")),
                    PhotoUrl = reader.IsDBNull(reader.GetOrdinal("photo_url")) ? null : reader.GetString(reader.GetOrdinal("photo_url")),
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
                });
            }
        }

        return new PagedResultDto<ItemSummaryDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize),
        };
    }

    private async Task<int> GetTotalCountAsync(MySqlConnection conn, ItemSearchQuery query, CancellationToken ct)
    {
        var countSql = $"""
            SELECT
                (SELECT COUNT(*) FROM lost_items li WHERE {LostWhere}) +
                (SELECT COUNT(*) FROM found_items fi WHERE {FoundWhere}) AS total;
            """;

        await using var cmd = new MySqlCommand(countSql, conn);
        AddFilterParameters(cmd, query);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

        private static void AddFilterParameters(MySqlCommand cmd, ItemSearchQuery query)
    {
        var hasType = !string.IsNullOrWhiteSpace(query.ItemType);
        cmd.Parameters.AddWithValue("@hasType", hasType ? 1 : 0);
        cmd.Parameters.AddWithValue("@typeValue", hasType ? query.ItemType!.ToUpperInvariant() : string.Empty);

        var hasCategory = !string.IsNullOrWhiteSpace(query.Category);
        cmd.Parameters.AddWithValue("@hasCategory", hasCategory ? 1 : 0);
        cmd.Parameters.AddWithValue("@category", hasCategory ? query.Category : string.Empty);

        var hasDateFrom = query.DateFrom.HasValue;
        cmd.Parameters.AddWithValue("@hasDateFrom", hasDateFrom ? 1 : 0);
        cmd.Parameters.AddWithValue("@dateFrom", hasDateFrom ? query.DateFrom!.Value.ToDateTime(TimeOnly.MinValue) : DateTime.MinValue);

        var hasDateTo = query.DateTo.HasValue;
        cmd.Parameters.AddWithValue("@hasDateTo", hasDateTo ? 1 : 0);
        cmd.Parameters.AddWithValue("@dateTo", hasDateTo ? query.DateTo!.Value.ToDateTime(TimeOnly.MinValue) : DateTime.MinValue);

        var hasKeyword = !string.IsNullOrWhiteSpace(query.Keyword);
        cmd.Parameters.AddWithValue("@hasKeyword", hasKeyword ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "@keywordPattern",
            hasKeyword ? $"%{EscapeLikePattern(query.Keyword!.Trim().ToLowerInvariant())}%" : "%");
    }

    // Escapes the characters that are meaningful to SQL LIKE ('%' and '_') as well as
    // the escape character itself ('\'), so a keyword like "50%" or "file_name" is
    // matched literally instead of being interpreted as a wildcard. Must be paired
    // with "LIKE @keywordPattern ESCAPE '\\'" in the SQL.
    private static string EscapeLikePattern(string value) =>
        value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
}