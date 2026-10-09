using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AdminVerifyService.Tests.Support;
using MySqlConnector;

namespace AdminVerifyService.Tests.Integration;

/// <summary>
/// Story LF-87, Step 9 - API integration for the Spam Review endpoint
/// (AdminSpamRecordsController + SpamReviewModels) through the REAL Program.cs host
/// (AdminVerifyApiFactory: Development environment, real migrations, real MySQL, real JwtBearer
/// handler and the real AdminOnly policy, real CORS middleware). Requests are authenticated with
/// real signed HS256 tokens minted by AdminTestTokenFactory.
///
/// Pins over HTTP:
///  - the auth boundary: 401 unauthenticated/expired/wrong-secret, 403 non-admin or missing claim;
///  - the response schema (exact camelCase property set of SpamRecordRow/SpamRecordPage);
///  - the tab/date/userIds filters, sort=score ordering and the PageSize=20 pagination boundary;
///  - parser rejections surfacing as 400 with { error } through the controller;
///  - the deleted-user case: records created for users that no longer exist stay listed and
///    filterable by that user id (ADD - contrast with the LF-79 user-facing unlistable-by-id gap);
///  - CORS: allowed origin reflected (with credentials), disallowed origin not reflected,
///    preflight accepted.
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamReviewApiTests : IClassFixture<AdminVerifyApiFactory>
{
    private const string RecordsPath = "/api/admin/spam-records";
    private const string AllowedOrigin = "http://localhost:5173";

    // A user id with no row in any users table - stands in for a soft-deleted account.
    private static readonly Guid DeletedUserId = Guid.Parse("20000000-0000-0000-0000-000000000002");

    private static readonly DateTime RefNow = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly AdminVerifyApiFactory _factory;
    private readonly HttpClient _client;

    public SpamReviewApiTests(AdminVerifyApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ---- request helpers ----

    private async Task<HttpResponseMessage> GetAsync(string path, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static string AdminToken() =>
        AdminTestTokenFactory.CreateAdminToken(Guid.NewGuid());

    // ---- seed helpers (the API host's own database) ----

    private static async Task ResetAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            DELETE FROM spam_record_notifications;
            DELETE FROM spam_record_listings;
            DELETE FROM spam_records;
            DELETE FROM spam_alert_recipients;
            DELETE FROM tracked_listings;
            DELETE FROM processed_events;
            """,
            connection);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedRecordAsync(
        string connectionString,
        Guid id,
        Guid userId,
        int scoreA,
        string status,
        DateTime createdAt)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO spam_records
                (id, user_id, collecting_user_id, score_a, status, collecting_until, created_at, updated_at)
            VALUES
                (@id, @userId, NULL, @scoreA, @status, @until, @createdAt, @createdAt);
            """,
            connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@scoreA", scoreA);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@until", createdAt.AddMinutes(30));
        command.Parameters.AddWithValue("@createdAt", createdAt);

        await command.ExecuteNonQueryAsync();
    }

    // ---- auth boundary ----

    [Fact] // All list endpoints are behind the AdminOnly policy: no token means 401, not a leak.
    public async Task A01_NoToken_Returns401()
    {
        var response = await GetAsync(RecordsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact] // Authenticated, but AdminOnly's RequireClaim("is_admin","1") fails -> 403, not 401.
    public async Task A02_NonAdminToken_Returns403()
    {
        var token = AdminTestTokenFactory.CreateNonAdminToken(Guid.Parse("20000000-0000-0000-0000-000000000003"));

        var response = await GetAsync(RecordsPath, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact] // The claim must be present - a token carrying no is_admin claim at all is also 403.
    public async Task A03_TokenWithoutAdminClaim_Returns403()
    {
        var token = AdminTestTokenFactory.CreateTokenWithoutAdminClaim(Guid.Parse("20000000-0000-0000-0000-000000000004"));

        var response = await GetAsync(RecordsPath, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact] // Real JwtBearer lifetime validation: an expired token is rejected with 401.
    public async Task A04_ExpiredToken_Returns401()
    {
        var token = AdminTestTokenFactory.CreateExpiredToken(Guid.Parse("20000000-0000-0000-0000-000000000005"));

        var response = await GetAsync(RecordsPath, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact] // Real signature validation: a token minted with a different secret is rejected with 401.
    public async Task A05_WrongSecretToken_Returns401()
    {
        var token = AdminTestTokenFactory.CreateTokenWithWrongSecret(Guid.Parse("20000000-0000-0000-0000-000000000006"));

        var response = await GetAsync(RecordsPath, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- listing behaviour ----

    [Fact] // An empty database returns an empty page over HTTP, with the page's exact property set.
    public async Task A06_EmptyDatabase_ReturnsEmptyPage()
    {
        await ResetAsync(_factory.ConnectionString);

        var response = await GetAsync(RecordsPath, AdminToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("items").ValueKind);
        Assert.Empty(root.GetProperty("items").EnumerateArray());
        Assert.False(root.GetProperty("hasMore").GetBoolean());

        var names = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["hasMore", "items"], names);
    }

    [Fact] // A seeded row comes back with the exact camelCase schema of SpamRecordRow.
    public async Task A07_ListReturnsRows_WithExactSchema()
    {
        await ResetAsync(_factory.ConnectionString);
        var recordId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        await SeedRecordAsync(_factory.ConnectionString, recordId, DeletedUserId, scoreA: 3, status: "NEEDS_REVIEW", createdAt: RefNow);

        var response = await GetAsync($"{RecordsPath}?tab=active", AdminToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());

        // Exact schema pin: the row property set is fully enumerated, so a rename or an added
        // serialized field breaks this test deliberately.
        var names = item.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["flaggedAt", "id", "listingCount", "scoreA", "status", "userId"], names);

        Assert.Equal(recordId, Guid.Parse(item.GetProperty("id").GetString()!));
        Assert.Equal(DeletedUserId, Guid.Parse(item.GetProperty("userId").GetString()!));
        Assert.Equal(3, item.GetProperty("scoreA").GetInt32());
        Assert.Equal("NEEDS_REVIEW", item.GetProperty("status").GetString());
        // ListingCount is populated from score_a today (documented LF-87 schema observation).
        Assert.Equal(3, item.GetProperty("listingCount").GetInt32());

        var flaggedAt = item.GetProperty("flaggedAt").GetString();
        Assert.NotNull(flaggedAt);
        Assert.StartsWith("2026-06-01T10:00:00", flaggedAt); // created_at round-trips as UTC ISO-8601
        Assert.EndsWith("Z", flaggedAt);

        Assert.False(root.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("active", 2)]
    [InlineData("solved", 1)]
    [InlineData("dismissed", 0)]
    [InlineData(null, 2)] // no tab parameter defaults to the active tab
    public async Task A08_TabFilter_ReturnsOnlyThatTabsStatuses(string? tab, int expectedCount)
    {
        await ResetAsync(_factory.ConnectionString);
        var needsReview = Guid.Parse("30000000-0000-0000-0000-000000000101");
        var pendingSolve = Guid.Parse("30000000-0000-0000-0000-000000000102");
        var solved = Guid.Parse("30000000-0000-0000-0000-000000000103");

        await SeedRecordAsync(_factory.ConnectionString, needsReview, Guid.NewGuid(), scoreA: 3, status: "NEEDS_REVIEW", createdAt: RefNow);
        await SeedRecordAsync(_factory.ConnectionString, pendingSolve, Guid.NewGuid(), scoreA: 2, status: "PENDING_SOLVE", createdAt: RefNow.AddMinutes(1));
        await SeedRecordAsync(_factory.ConnectionString, solved, Guid.NewGuid(), scoreA: 5, status: "SOLVED", createdAt: RefNow.AddMinutes(2));

        var query = tab is null ? "" : $"?tab={tab}";
        var response = await GetAsync($"{RecordsPath}{query}", AdminToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = (await ReadJsonAsync(response)).GetProperty("items")
            .EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        Assert.Equal(expectedCount, items.Length);

        // The active tab means NEEDS_REVIEW + UNDER_REVIEW + PENDING_SOLVE - never SOLVED/DISMISSED.
        if (tab is null || tab == "active")
        {
            Assert.Contains(needsReview.ToString(), items);
            Assert.Contains(pendingSolve.ToString(), items);
            Assert.DoesNotContain(solved.ToString(), items);
        }
    }

    [Theory] // Parser rejections surface as 400 with { error } through the controller.
    [InlineData("tab=bogus", "Tab must be active, dismissed or solved.")]
    [InlineData("sort=bogus", "Sort must be score or date.")]
    [InlineData("page=0", "Invalid page number.")]
    [InlineData("page=100001", "Invalid page number.")]
    [InlineData("from=2026-13-01", "The from date must use the format yyyy-MM-dd.")]
    [InlineData("from=2026-06-02&to=2026-06-01", "The from date must not be after the to date.")]
    [InlineData("userIds=not-a-guid", "User IDs must be valid IDs.")]
    public async Task A09_InvalidQuery_Returns400_WithError(string query, string expectedError)
    {
        var response = await GetAsync($"{RecordsPath}?{query}", AdminToken());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var root = await ReadJsonAsync(response);
        Assert.Equal(expectedError, root.GetProperty("error").GetString());
    }

    [Fact] // sort=score orders by score_a DESC; it is also the default sort.
    public async Task A10_SortByScore_OrdersDescending()
    {
        await ResetAsync(_factory.ConnectionString);
        var high = Guid.Parse("30000000-0000-0000-0000-000000000201"); // score 9
        var mid = Guid.Parse("30000000-0000-0000-0000-000000000202");  // score 5
        var low = Guid.Parse("30000000-0000-0000-0000-000000000203");  // score 1

        await SeedRecordAsync(_factory.ConnectionString, low, Guid.NewGuid(), scoreA: 1, status: "NEEDS_REVIEW", createdAt: RefNow);
        await SeedRecordAsync(_factory.ConnectionString, high, Guid.NewGuid(), scoreA: 9, status: "NEEDS_REVIEW", createdAt: RefNow.AddMinutes(1));
        await SeedRecordAsync(_factory.ConnectionString, mid, Guid.NewGuid(), scoreA: 5, status: "NEEDS_REVIEW", createdAt: RefNow.AddMinutes(2));

        var explicitResponse = await GetAsync($"{RecordsPath}?tab=active&sort=score", AdminToken());
        var explicitIds = (await ReadJsonAsync(explicitResponse)).GetProperty("items")
            .EnumerateArray().Select(e => Guid.Parse(e.GetProperty("id").GetString()!)).ToArray();
        Assert.Equal([high, mid, low], explicitIds);

        var defaultResponse = await GetAsync($"{RecordsPath}?tab=active", AdminToken());
        var defaultIds = (await ReadJsonAsync(defaultResponse)).GetProperty("items")
            .EnumerateArray().Select(e => Guid.Parse(e.GetProperty("id").GetString()!)).ToArray();
        Assert.Equal([high, mid, low], defaultIds);
    }

    [Theory] // flaggedAt window: created_at >= from and created_at < to+1 day (end-inclusive).
    [InlineData("", 2)]
    [InlineData("from=2026-06-10", 1)]
    [InlineData("to=2026-06-10", 1)]
    [InlineData("from=2026-06-01&to=2026-06-15", 2)]
    [InlineData("from=2026-06-16", 0)]
    public async Task A11_DateFilters_FlaggedWindow(string query, int expectedCount)
    {
        await ResetAsync(_factory.ConnectionString);
        var early = Guid.Parse("30000000-0000-0000-0000-000000000301"); // 2026-06-01
        var late = Guid.Parse("30000000-0000-0000-0000-000000000302");  // 2026-06-15

        await SeedRecordAsync(_factory.ConnectionString, early, Guid.NewGuid(), scoreA: 2, status: "NEEDS_REVIEW", createdAt: RefNow);
        await SeedRecordAsync(_factory.ConnectionString, late, Guid.NewGuid(), scoreA: 2, status: "NEEDS_REVIEW", createdAt: RefNow.AddDays(14));

        var path = query.Length == 0 ? RecordsPath : $"{RecordsPath}?{query}";
        var response = await GetAsync(path, AdminToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(expectedCount, (await ReadJsonAsync(response)).GetProperty("items").GetArrayLength());
    }

    [Fact] // ADD: a record created for a user that no longer exists stays listed and filterable by that id.
    public async Task A12_DeletedUser_StillListed_AndFilterableById()
    {
        await ResetAsync(_factory.ConnectionString);
        var deletedUserRecord = Guid.Parse("30000000-0000-0000-0000-000000000401");
        var otherRecord = Guid.Parse("30000000-0000-0000-0000-000000000402");
        var liveUser = Guid.Parse("20000000-0000-0000-0000-000000000001");

        await SeedRecordAsync(_factory.ConnectionString, deletedUserRecord, DeletedUserId, scoreA: 4, status: "NEEDS_REVIEW", createdAt: RefNow);
        await SeedRecordAsync(_factory.ConnectionString, otherRecord, liveUser, scoreA: 2, status: "NEEDS_REVIEW", createdAt: RefNow.AddMinutes(1));

        // The deleted user's record still appears in the review list.
        var listResponse = await GetAsync($"{RecordsPath}?tab=active", AdminToken());
        var listedIds = (await ReadJsonAsync(listResponse)).GetProperty("items")
            .EnumerateArray().Select(e => Guid.Parse(e.GetProperty("id").GetString()!)).ToArray();
        Assert.Contains(deletedUserRecord, listedIds);

        // And it stays retrievable via the userIds filter using the (now deleted) user id.
        var filteredResponse = await GetAsync($"{RecordsPath}?userIds={DeletedUserId}", AdminToken());
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);

        var row = Assert.Single((await ReadJsonAsync(filteredResponse)).GetProperty("items").EnumerateArray());
        Assert.Equal(DeletedUserId, Guid.Parse(row.GetProperty("userId").GetString()!));
        Assert.Equal(4, row.GetProperty("scoreA").GetInt32());
    }

    [Fact] // PageSize (20) boundary: page 1 carries 20 and hasMore=true, page 2 the remaining 5.
    public async Task A13_Pagination_20ThenRemainder_HasMoreFlag()
    {
        await ResetAsync(_factory.ConnectionString);
        for (var i = 0; i < 25; i++)
        {
            var recordId = Guid.Parse($"30000000-0000-0000-0000-{i:000000000000}");
            await SeedRecordAsync(
                _factory.ConnectionString,
                recordId,
                Guid.NewGuid(),
                scoreA: 1 + (i % 5),
                status: "NEEDS_REVIEW",
                createdAt: RefNow.AddMinutes(i));
        }

        var page1Response = await GetAsync($"{RecordsPath}?page=1", AdminToken());
        Assert.Equal(HttpStatusCode.OK, page1Response.StatusCode);
        var page1 = await ReadJsonAsync(page1Response);
        Assert.Equal(20, page1.GetProperty("items").GetArrayLength());
        Assert.True(page1.GetProperty("hasMore").GetBoolean());

        var page2Response = await GetAsync($"{RecordsPath}?page=2", AdminToken());
        Assert.Equal(HttpStatusCode.OK, page2Response.StatusCode);
        var page2 = await ReadJsonAsync(page2Response);
        Assert.Equal(5, page2.GetProperty("items").GetArrayLength());
        Assert.False(page2.GetProperty("hasMore").GetBoolean());

        var seen = page1.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .Concat(page2.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetString()))
            .ToHashSet();
        Assert.Equal(25, seen.Count); // the two pages are disjoint - no duplicated rows
    }

    // ---- CORS ----

    [Fact] // An origin allowed by the admin-frontend policy is reflected (credentials allowed).
    public async Task A14_Cors_AllowedOrigin_IsReflected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, RecordsPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken());
        request.Headers.Add("Origin", AllowedOrigin);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact] // A disallowed origin gets no CORS headers at all - the browser would block the read.
    public async Task A15_Cors_DisallowedOrigin_IsNotReflected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, RecordsPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken());
        request.Headers.Add("Origin", "http://evil.example");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact] // A real preflight for an allowed origin is accepted with the requested method allowed.
    public async Task A16_Cors_Preflight_AllowedOrigin_IsAccepted()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, RecordsPath);
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("GET", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }
}