using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using MySqlConnector;

namespace AdminVerifyService.Tests.Integration;

/// <summary>
/// Story LF-267 / LF-268 / LF-269, Step B - API integration for the spam case actions
/// (AdminSpamRecordsController + SpamCaseRepository + SpamSolveRepository) through the REAL
/// Program.cs host (AdminVerifyApiFactory: Development environment, real migrations, real MySQL,
/// real JwtBearer, real AdminOnly policy). Requests are authenticated with real signed HS256 tokens
/// from AdminTestTokenFactory.
///
/// Pins over HTTP:
///  - the auth boundary on every case verb (401 anonymous, 403 non-admin);
///  - the detail contract: the exact camelCase schema and the listingCount = score_a known-gap
///    (documented at Step 11 and replicated by ReadDetailAsync - pinned as-is, not fixed);
///  - the open /dismiss /solve verbs and their exact { error } bodies;
///  - the solve sub-verbs (listing result, kick result, finish) and the model-validation 400
///    (SolveRequest members are JsonRequired) vs the repository's { error } 400s;
///  - the three concurrency races (double open, double dismiss, double solve start) against real
///    MySQL with Task.WhenAll: exactly one success, the DB ends in exactly one state.
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamReviewCaseApiTests : IClassFixture<AdminVerifyApiFactory>
{
    private const string RecordsPath = "/api/admin/spam-records";

    private static readonly Guid RecordId = Guid.Parse("30000000-0000-0000-0000-00000000B100");
    private static readonly Guid SecondRecordId = Guid.Parse("30000000-0000-0000-0000-00000000B101");
    private static readonly Guid ListingId = Guid.Parse("10000000-0000-0000-0000-00000000B201");
    private static readonly Guid OtherListingId = Guid.Parse("10000000-0000-0000-0000-00000000B202");
    private static readonly Guid RefUser = Guid.Parse("20000000-0000-0000-0000-00000000BEEF");
    private static readonly Guid CollectingAdmin = Guid.Parse("20000000-0000-0000-0000-00000000BADD");

    private static readonly DateTime RefNow = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly AdminVerifyApiFactory _factory;
    private readonly HttpClient _client;

    public SpamReviewCaseApiTests(AdminVerifyApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ---- request helpers ----

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = content;
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string AdminToken() =>
        AdminTestTokenFactory.CreateAdminToken(Guid.NewGuid());

    private static string BuildPath(string suffix) =>
        $"{RecordsPath}/{RecordId}{suffix.Replace("{listing}", ListingId.ToString())}";

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
        DateTime createdAt,
        string kickStatus = KickStatus.NotRequested,
        Guid? collectingUserId = null)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO spam_records
                (id, user_id, collecting_user_id, score_a, status, kick_status, collecting_until, created_at, updated_at)
            VALUES
                (@id, @userId, @collecting, @scoreA, @status, @kickStatus, @until, @createdAt, @createdAt);
            """,
            connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@collecting", (object?)collectingUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("@scoreA", scoreA);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@kickStatus", kickStatus);
        command.Parameters.AddWithValue("@until", createdAt.AddMinutes(30));
        command.Parameters.AddWithValue("@createdAt", createdAt);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedTrackedListingAsync(
        string connectionString,
        Guid listingId,
        Guid userId,
        DateTime postedAt)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO tracked_listings (listing_id, listing_type, user_id, posted_at, created_at)
            VALUES (@listingId, 'LOST', @userId, @postedAt, @postedAt);
            """,
            connection);
        command.Parameters.AddWithValue("@listingId", listingId);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@postedAt", postedAt);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedLinkedListingAsync(
        string connectionString,
        Guid recordId,
        Guid listingId,
        DateTime addedAt,
        string? solveResult = null)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO spam_record_listings (spam_record_id, listing_id, solve_result, added_at)
            VALUES (@recordId, @listingId, @solveResult, @addedAt);
            """,
            connection);
        command.Parameters.AddWithValue("@recordId", recordId);
        command.Parameters.AddWithValue("@listingId", listingId);
        command.Parameters.AddWithValue("@solveResult", (object?)solveResult ?? DBNull.Value);
        command.Parameters.AddWithValue("@addedAt", addedAt);

        await command.ExecuteNonQueryAsync();
    }

    private sealed record RecordRow(string Status, string KickStatus, Guid? CollectingUserId);

    private static async Task<RecordRow> ReadRecordAsync(string connectionString, Guid id)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status, kick_status, collecting_user_id FROM spam_records WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new Xunit.Sdk.XunitException($"No spam_records row for {id}");
        }

        var collecting = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
        return new RecordRow(reader.GetString(0), reader.GetString(1), collecting);
    }

    private static async Task<string?> ReadListingResultAsync(string connectionString, Guid recordId, Guid listingId)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT solve_result FROM spam_record_listings WHERE spam_record_id = @recordId AND listing_id = @listingId;",
            connection);
        command.Parameters.AddWithValue("@recordId", recordId);
        command.Parameters.AddWithValue("@listingId", listingId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new Xunit.Sdk.XunitException($"No spam_record_listings row for {recordId}/{listingId}");
        }

        return reader.IsDBNull(0) ? null : reader.GetString(0);
    }

    // ---- auth boundary on every case verb ----

    public static TheoryData<string, string, string?> CaseVerbs() => new()
    {
        { "GET", "", null },
        { "POST", "/open", null },
        { "POST", "/dismiss", null },
        { "POST", "/solve", "{\"kick\":true,\"resume\":false}" },
        { "PUT", "/listings/{listing}/result", "{\"result\":\"DELETED\"}" },
        { "PUT", "/kick-result", "{\"result\":\"KICKED\"}" },
        { "POST", "/finish", null }
    };

    [Theory]
    [MemberData(nameof(CaseVerbs))]
    public async Task CaseVerbs_NoToken_Returns401(string verb, string suffix, string? json)
    {
        var content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json");

        var response = await SendAsync(HttpMethod.Parse(verb), BuildPath(suffix), token: null, content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(CaseVerbs))]
    public async Task CaseVerbs_NonAdminToken_Returns403(string verb, string suffix, string? json)
    {
        var content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json");
        var token = AdminTestTokenFactory.CreateNonAdminToken(Guid.NewGuid());

        var response = await SendAsync(HttpMethod.Parse(verb), BuildPath(suffix), token, content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- LF-267: detail ----

    [Fact] // LF267-AP-03: the detail contract - exact schema, listing order, lowercase type, solve-result passthrough.
    public async Task Detail_ReturnsFullContract_ListingCountFromScoreA()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 4, SpamRecordStatus.PendingSolve, RefNow,
            kickStatus: KickStatus.Pending);
        await SeedTrackedListingAsync(_factory.ConnectionString, ListingId, RefUser, RefNow.AddMinutes(-40));
        await SeedTrackedListingAsync(_factory.ConnectionString, OtherListingId, RefUser, RefNow.AddMinutes(-10));
        await SeedLinkedListingAsync(_factory.ConnectionString, RecordId, ListingId, RefNow.AddMinutes(-40), solveResult: "SKIPPED");
        await SeedLinkedListingAsync(_factory.ConnectionString, RecordId, OtherListingId, RefNow.AddMinutes(-10));

        var response = await SendAsync(HttpMethod.Get, $"{RecordsPath}/{RecordId}", AdminToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);

        // Exact schema pin: fully enumerated, so a rename or an added serialized field breaks this.
        var names = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["flaggedAt", "id", "kickStatus", "listingCount", "listings", "scoreA", "status", "userId"], names);

        Assert.Equal(RecordId, Guid.Parse(root.GetProperty("id").GetString()!));
        Assert.Equal(RefUser, Guid.Parse(root.GetProperty("userId").GetString()!));
        Assert.Equal(4, root.GetProperty("scoreA").GetInt32());
        // Known gap carried from Step 11 (LF-87): listingCount is fed from score_a, not a count of
        // the linked listings (SpamCaseRepository.ReadDetailAsync). Pinned as-is, open for the ADD.
        Assert.Equal(4, root.GetProperty("listingCount").GetInt32());
        Assert.Equal(SpamRecordStatus.PendingSolve, root.GetProperty("status").GetString());
        Assert.Equal(KickStatus.Pending, root.GetProperty("kickStatus").GetString());

        var flaggedAt = root.GetProperty("flaggedAt").GetString();
        Assert.NotNull(flaggedAt);
        Assert.StartsWith("2026-07-01T12:00:00", flaggedAt); // created_at round-trips as UTC ISO-8601
        Assert.EndsWith("Z", flaggedAt);

        var listings = root.GetProperty("listings").EnumerateArray().ToArray();
        Assert.Equal(2, listings.Length);
        Assert.Equal(ListingId, Guid.Parse(listings[0].GetProperty("listingId").GetString()!)); // oldest first
        Assert.Equal(OtherListingId, Guid.Parse(listings[1].GetProperty("listingId").GetString()!));
        Assert.Equal("lost", listings[0].GetProperty("listingType").GetString()); // lowercased on the wire
        Assert.Equal("SKIPPED", listings[0].GetProperty("solveResult").GetString());
        Assert.Equal(JsonValueKind.Null, listings[1].GetProperty("solveResult").ValueKind);
    }

    [Fact] // LF267-AP-04: unknown record -> the shared 404 with { error }.
    public async Task Detail_UnknownRecord_Returns404_WithError()
    {
        var response = await SendAsync(HttpMethod.Get, $"{RecordsPath}/{Guid.NewGuid()}", AdminToken());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Spam record not found.", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    // ---- LF-267: open ----

    [Fact] // LF267-AP-05: opening a NEEDS_REVIEW record moves it to UNDER_REVIEW and releases the claim.
    public async Task Open_NeedsReview_TransitionsToUnderReview_AndClearsCollectingClaim()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.NeedsReview, RefNow,
            collectingUserId: CollectingAdmin);

        var response = await SendAsync(HttpMethod.Post, $"{RecordsPath}/{RecordId}/open", AdminToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);
        Assert.Equal(SpamRecordStatus.UnderReview, root.GetProperty("status").GetString());

        var row = await ReadRecordAsync(_factory.ConnectionString, RecordId);
        Assert.Equal(SpamRecordStatus.UnderReview, row.Status);
        Assert.Null(row.CollectingUserId);
    }

    [Fact] // LF267-AP-06: an already-open record opens read-only - same 200, no change.
    public async Task Open_AlreadyOpen_ReturnsReadOnlyDetail_WithoutChangingStatus()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.UnderReview, RefNow);

        var response = await SendAsync(HttpMethod.Post, $"{RecordsPath}/{RecordId}/open", AdminToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(SpamRecordStatus.UnderReview, (await ReadJsonAsync(response)).GetProperty("status").GetString());
        Assert.Equal(SpamRecordStatus.UnderReview, (await ReadRecordAsync(_factory.ConnectionString, RecordId)).Status);
    }

    [Fact] // LF267-AP-07: unknown record -> 404 from open too.
    public async Task Open_UnknownRecord_Returns404_WithError()
    {
        var response = await SendAsync(HttpMethod.Post, $"{RecordsPath}/{Guid.NewGuid()}/open", AdminToken());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Spam record not found.", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    // ---- LF-268: dismiss ----

    [Fact] // LF268-AP-01: dismissing an UNDER_REVIEW record moves it to DISMISSED and releases the claim.
    public async Task Dismiss_UnderReview_TransitionsToDismissed_AndClearsCollectingClaim()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.UnderReview, RefNow,
            collectingUserId: CollectingAdmin);

        var response = await SendAsync(HttpMethod.Post, $"{RecordsPath}/{RecordId}/dismiss", AdminToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(SpamRecordStatus.Dismissed, (await ReadJsonAsync(response)).GetProperty("status").GetString());
        var row = await ReadRecordAsync(_factory.ConnectionString, RecordId);
        Assert.Equal(SpamRecordStatus.Dismissed, row.Status);
        Assert.Null(row.CollectingUserId);
    }

    [Fact] // LF268-AP-02: a never-opened record cannot be dismissed - 409, row untouched.
    public async Task Dismiss_NotUnderReview_Returns409_WithError()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.NeedsReview, RefNow);

        var response = await SendAsync(HttpMethod.Post, $"{RecordsPath}/{RecordId}/dismiss", AdminToken());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This record is already being handled.", (await ReadJsonAsync(response)).GetProperty("error").GetString());

        Assert.Equal(SpamRecordStatus.NeedsReview, (await ReadRecordAsync(_factory.ConnectionString, RecordId)).Status);
    }

    [Fact] // LF268-AP-03: unknown record -> 404 from dismiss too.
    public async Task Dismiss_UnknownRecord_Returns404_WithError()
    {
        var response = await SendAsync(HttpMethod.Post, $"{RecordsPath}/{Guid.NewGuid()}/dismiss", AdminToken());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Spam record not found.", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    // ---- LF-269: solve ----

    [Fact] // LF269-AP-01: start with kick checked -> PENDING_SOLVE + kick PENDING.
    public async Task Solve_Start_KickChecked_SetsPendingSolve_AndKickPending()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.UnderReview, RefNow);

        var response = await SendAsync(
            HttpMethod.Post, $"{RecordsPath}/{RecordId}/solve", AdminToken(),
            JsonBody(new { kick = true, resume = false }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);
        Assert.Equal(SpamRecordStatus.PendingSolve, root.GetProperty("status").GetString());
        Assert.Equal(KickStatus.Pending, root.GetProperty("kickStatus").GetString());

        var row = await ReadRecordAsync(_factory.ConnectionString, RecordId);
        Assert.Equal(SpamRecordStatus.PendingSolve, row.Status);
        Assert.Equal(KickStatus.Pending, row.KickStatus);
    }

    [Fact] // LF269-AP-02: start with kick unchecked -> kick NOT_REQUESTED.
    public async Task Solve_Start_KickUnchecked_SetsKickNotRequested()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.UnderReview, RefNow);

        var response = await SendAsync(
            HttpMethod.Post, $"{RecordsPath}/{RecordId}/solve", AdminToken(),
            JsonBody(new { kick = false, resume = false }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(KickStatus.NotRequested, (await ReadJsonAsync(response)).GetProperty("kickStatus").GetString());
    }

    [Fact] // LF269-AP-03: a start on a non-UNDER_REVIEW record is refused 409, row untouched.
    public async Task Solve_Start_NotUnderReview_Returns409_WithError()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.Dismissed, RefNow);

        var response = await SendAsync(
            HttpMethod.Post, $"{RecordsPath}/{RecordId}/solve", AdminToken(),
            JsonBody(new { kick = false, resume = false }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This record is already being handled.", (await ReadJsonAsync(response)).GetProperty("error").GetString());

        Assert.Equal(SpamRecordStatus.Dismissed, (await ReadRecordAsync(_factory.ConnectionString, RecordId)).Status);
    }

    [Theory] // LF269-AP-04: SolveRequest members are JsonRequired - a missing field is a model-validation 400.
    [InlineData("{\"resume\":false}")] // no kick
    [InlineData("{\"kick\":true}")]    // no resume
    public async Task Solve_MissingRequiredField_Returns400_ValidationErrors(string json)
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.UnderReview, RefNow);

        var response = await SendAsync(
            HttpMethod.Post, $"{RecordsPath}/{RecordId}/solve", AdminToken(),
            new StringContent(json, Encoding.UTF8, "application/json"));

        // A ValidationProblemDetails 400 (has "errors"), NOT the repository's { error } shape.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var root = await ReadJsonAsync(response);
        Assert.True(root.TryGetProperty("errors", out _));
        Assert.False(root.TryGetProperty("error", out _));

        Assert.Equal(SpamRecordStatus.UnderReview, (await ReadRecordAsync(_factory.ConnectionString, RecordId)).Status);
    }

    [Theory] // LF269-AP-05: each accepted listing result persists through the 204 endpoint.
    [InlineData("DELETED")]
    [InlineData("SKIPPED")]
    [InlineData("FAILED")]
    public async Task ListingResult_AcceptedValue_Returns204_AndPersists(string result)
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.PendingSolve, RefNow);
        await SeedTrackedListingAsync(_factory.ConnectionString, ListingId, RefUser, RefNow.AddMinutes(-40));
        await SeedLinkedListingAsync(_factory.ConnectionString, RecordId, ListingId, RefNow.AddMinutes(-40));

        var response = await SendAsync(
            HttpMethod.Put, $"{RecordsPath}/{RecordId}/listings/{ListingId}/result", AdminToken(),
            JsonBody(new { result }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(result, await ReadListingResultAsync(_factory.ConnectionString, RecordId, ListingId));
    }

    [Fact] // LF269-AP-06: an invalid listing result is a 400 with the exact { error }, nothing written.
    public async Task ListingResult_Invalid_Returns400_WithError()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.PendingSolve, RefNow);
        await SeedTrackedListingAsync(_factory.ConnectionString, ListingId, RefUser, RefNow.AddMinutes(-40));
        await SeedLinkedListingAsync(_factory.ConnectionString, RecordId, ListingId, RefNow.AddMinutes(-40));

        var response = await SendAsync(
            HttpMethod.Put, $"{RecordsPath}/{RecordId}/listings/{ListingId}/result", AdminToken(),
            JsonBody(new { result = "banana" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Result must be DELETED, SKIPPED or FAILED.", (await ReadJsonAsync(response)).GetProperty("error").GetString());

        Assert.Null(await ReadListingResultAsync(_factory.ConnectionString, RecordId, ListingId));
    }

    [Fact] // LF269-AP-07: the one-shot kick result - KICKED from PENDING returns the updated detail.
    public async Task KickResult_KickedFromPending_PersistsAndReturnsDetail()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.PendingSolve, RefNow,
            kickStatus: KickStatus.Pending);

        var response = await SendAsync(
            HttpMethod.Put, $"{RecordsPath}/{RecordId}/kick-result", AdminToken(),
            JsonBody(new { result = "KICKED" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(KickStatus.Kicked, (await ReadJsonAsync(response)).GetProperty("kickStatus").GetString());
        Assert.Equal(KickStatus.Kicked, (await ReadRecordAsync(_factory.ConnectionString, RecordId)).KickStatus);
    }

    [Fact] // LF269-AP-08: a kick result without a PENDING kick is refused 409.
    public async Task KickResult_NotRequested_Returns409_WithError()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.PendingSolve, RefNow);

        var response = await SendAsync(
            HttpMethod.Put, $"{RecordsPath}/{RecordId}/kick-result", AdminToken(),
            JsonBody(new { result = "KICKED" }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The kick was not requested or has already been attempted.", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact] // LF269-AP-09: the full solve happy path over HTTP - start, both listing results, kick, finish -> SOLVED.
    public async Task Finish_AllDone_Solves_AfterKickAndListingResults()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.UnderReview, RefNow);
        await SeedTrackedListingAsync(_factory.ConnectionString, ListingId, RefUser, RefNow.AddMinutes(-40));
        await SeedTrackedListingAsync(_factory.ConnectionString, OtherListingId, RefUser, RefNow.AddMinutes(-10));
        await SeedLinkedListingAsync(_factory.ConnectionString, RecordId, ListingId, RefNow.AddMinutes(-40));
        await SeedLinkedListingAsync(_factory.ConnectionString, RecordId, OtherListingId, RefNow.AddMinutes(-10));

        var start = await SendAsync(
            HttpMethod.Post, $"{RecordsPath}/{RecordId}/solve", AdminToken(),
            JsonBody(new { kick = true, resume = false }));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        foreach (var listing in new[] { ListingId, OtherListingId })
        {
            var saved = await SendAsync(
                HttpMethod.Put, $"{RecordsPath}/{RecordId}/listings/{listing}/result", AdminToken(),
                JsonBody(new { result = "DELETED" }));
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        }

        var kicked = await SendAsync(
            HttpMethod.Put, $"{RecordsPath}/{RecordId}/kick-result", AdminToken(),
            JsonBody(new { result = "KICKED" }));
        Assert.Equal(HttpStatusCode.OK, kicked.StatusCode);

        var finish = await SendAsync(HttpMethod.Post, $"{RecordsPath}/{RecordId}/finish", AdminToken());
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        Assert.Equal(SpamRecordStatus.Solved, (await ReadJsonAsync(finish)).GetProperty("status").GetString());
        Assert.Equal(SpamRecordStatus.Solved, (await ReadRecordAsync(_factory.ConnectionString, RecordId)).Status);
    }

    [Fact] // LF269-AP-10: finish is refused while a listing has no result - exact 409, row stays PENDING_SOLVE.
    public async Task Finish_ListingWithoutResult_Returns409_WithError()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.PendingSolve, RefNow);
        await SeedTrackedListingAsync(_factory.ConnectionString, ListingId, RefUser, RefNow.AddMinutes(-40));
        await SeedLinkedListingAsync(_factory.ConnectionString, RecordId, ListingId, RefNow.AddMinutes(-40));

        var response = await SendAsync(HttpMethod.Post, $"{RecordsPath}/{RecordId}/finish", AdminToken());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Some listings have not been deleted or skipped yet.", (await ReadJsonAsync(response)).GetProperty("error").GetString());

        Assert.Equal(SpamRecordStatus.PendingSolve, (await ReadRecordAsync(_factory.ConnectionString, RecordId)).Status);
    }

    // ---- the three concurrency races ----

    [Fact] // LF267-AP-08: two admins open the same NEEDS_REVIEW record at once - both see UNDER_REVIEW, one transition.
    public async Task Race_Open_TwoConcurrent_ExactlyOneTransition()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, RecordId, RefUser, scoreA: 3, SpamRecordStatus.NeedsReview, RefNow,
            collectingUserId: CollectingAdmin);

        var path = $"{RecordsPath}/{RecordId}/open";
        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, path, AdminToken()),
            SendAsync(HttpMethod.Post, path, AdminToken()));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var response in responses)
        {
            Assert.Equal(SpamRecordStatus.UnderReview, (await ReadJsonAsync(response)).GetProperty("status").GetString());
        }

        var row = await ReadRecordAsync(_factory.ConnectionString, RecordId);
        Assert.Equal(SpamRecordStatus.UnderReview, row.Status);
        Assert.Null(row.CollectingUserId);
    }

    [Fact] // LF268-AP-04: two admins dismiss the same UNDER_REVIEW record at once - exactly one success, one 409.
    public async Task Race_Dismiss_TwoConcurrent_ExactlyOneSucceeds()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, SecondRecordId, RefUser, scoreA: 3, SpamRecordStatus.UnderReview, RefNow,
            collectingUserId: CollectingAdmin);

        var path = $"{RecordsPath}/{SecondRecordId}/dismiss";
        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, path, AdminToken()),
            SendAsync(HttpMethod.Post, path, AdminToken()));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var conflicted = await ReadJsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal("This record is already being handled.", conflicted.GetProperty("error").GetString());

        var row = await ReadRecordAsync(_factory.ConnectionString, SecondRecordId);
        Assert.Equal(SpamRecordStatus.Dismissed, row.Status);
        Assert.Null(row.CollectingUserId);
    }

    [Fact] // LF269-AP-11: two admins start solving the same UNDER_REVIEW record at once - exactly one PENDING_SOLVE.
    public async Task Race_SolveStart_TwoConcurrent_ExactlyOneSucceeds()
    {
        await ResetAsync(_factory.ConnectionString);
        await SeedRecordAsync(
            _factory.ConnectionString, SecondRecordId, RefUser, scoreA: 3, SpamRecordStatus.UnderReview, RefNow);

        var path = $"{RecordsPath}/{SecondRecordId}/solve";
        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, path, AdminToken(), JsonBody(new { kick = true, resume = false })),
            SendAsync(HttpMethod.Post, path, AdminToken(), JsonBody(new { kick = true, resume = false })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var row = await ReadRecordAsync(_factory.ConnectionString, SecondRecordId);
        Assert.Equal(SpamRecordStatus.PendingSolve, row.Status);
        Assert.Equal(KickStatus.Pending, row.KickStatus);
    }
}