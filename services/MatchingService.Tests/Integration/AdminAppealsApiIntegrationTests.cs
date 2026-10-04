using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatchingService.Tests.Support;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story LF-338 integration tests for api/admin/match-appeals, over real HTTP against ClaimsApiFactory: the
/// real AdminOnly authorization policy, real routing, real model binding and real status codes.
///
/// Every appeal these tests act on is created the way a user creates it - POST /api/matches/preview then
/// POST /api/matches/appeals - so the admin routes are only ever reached from genuine state.
/// </summary>
[Collection("Docker Integration Tests 13")]
public sealed class AdminAppealsApiIntegrationTests : IClassFixture<ClaimsApiFactory>, IAsyncLifetime
{
    private readonly ClaimsApiFactory _factory;

    public AdminAppealsApiIntegrationTests(ClaimsApiFactory factory)
    {
        _factory = factory;
    }

    /* match_appeals and matches are shared container state within this class, and the admin list pages 20 rows
       at a time, so an appeal left behind by an earlier test would otherwise show up in the next test's list. */
    public async Task InitializeAsync()
    {
        await using var connection = new MySqlConnection(_factory.GetConnectionString());
        await connection.OpenAsync();

        foreach (var table in new[] { "match_appeals", "matches" })
        {
            await using var command = new MySqlCommand($"DELETE FROM {table};", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Wiring -------------------------------------------------------------------------------

    private HttpClient AdminClient() =>
        ClientWithToken(JwtTestTokenFactory.CreateAdminToken(Guid.NewGuid()));

    private HttpClient UserClient(Guid userId) =>
        ClientWithToken(JwtTestTokenFactory.CreateVerifiedUserToken(userId));

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// <summary>
    /// Two unrelated reports owned by two different users, scoring far below the 60% threshold so an appeal is
    /// legal. They exist only in the faked Item Service.
    /// </summary>
    private (Guid LostId, Guid FoundId, Guid LostOwner, Guid Finder) SeedPair()
    {
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        var lostOwner = Guid.NewGuid();
        var finder = Guid.NewGuid();

        _factory.ItemServiceHandler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, title: "Green kettle", category: "Kitchen",
                description: "A green kettle with a whistle."));

        _factory.ItemServiceHandler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, finder, title: "Orange bicycle", category: "Vehicles",
                description: "An orange bicycle with a flat tyre."));

        return (lostId, foundId, lostOwner, finder);
    }

    /// <summary>Creates a genuine PENDING appeal through the user API and returns its id.</summary>
    private async Task<Guid> SeedPendingAppealAsync()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var client = UserClient(lostOwner);

        var preview = await client.PostAsJsonAsync(
            "/api/matches/preview",
            new { lostItemId = lostId, foundItemId = foundId });
        preview.EnsureSuccessStatusCode();

        using var previewDocument = JsonDocument.Parse(
            await preview.Content.ReadAsStringAsync());

        var appeal = await client.PostAsJsonAsync(
            "/api/matches/appeals",
            new
            {
                lostItemId = lostId,
                foundItemId = foundId,
                previewVersion = previewDocument.RootElement
                    .GetProperty("previewVersion").GetString(),
                note = "Both reports describe the same kettle."
            });

        Assert.Equal(HttpStatusCode.Created, appeal.StatusCode);

        using var appealDocument = JsonDocument.Parse(
            await appeal.Content.ReadAsStringAsync());

        return appealDocument.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("error").GetString()!;
    }

    private async Task<long> CountMatchesAsync(Guid lostId, Guid foundId)
    {
        await using var connection = new MySqlConnection(_factory.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM matches WHERE lost_item_id = @lostId AND found_item_id = @foundId;",
            connection);
        command.Parameters.AddWithValue("@lostId", lostId);
        command.Parameters.AddWithValue("@foundId", foundId);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountAppealsInStatusAsync(string status)
    {
        await using var connection = new MySqlConnection(_factory.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM match_appeals WHERE status = @status;", connection);
        command.Parameters.AddWithValue("@status", status);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    // ---- AdminOnly is enforced on every route --------------------------------------------------

    public static IEnumerable<object[]> AdminRoutes()
    {
        yield return new object[] { HttpMethod.Get, "/api/admin/match-appeals" };
        yield return new object[] { HttpMethod.Get, $"/api/admin/match-appeals/{Guid.NewGuid()}" };
        yield return new object[]
        {
            HttpMethod.Post, $"/api/admin/match-appeals/{Guid.NewGuid()}/verify"
        };
        yield return new object[]
        {
            HttpMethod.Post, $"/api/admin/match-appeals/{Guid.NewGuid()}/reject"
        };
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminRoute_NoToken_Returns401(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /* A valid, email-verified token whose owner is not an admin satisfies RequireAuthenticatedUser but fails
       RequireClaim("is_admin", "1") - a 403, not a 401. */
    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminRoute_VerifiedNonAdminToken_Returns403(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTestTokenFactory.CreateVerifiedUserToken(Guid.NewGuid()));

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /* An expired token fails ValidateLifetime before the AdminOnly claim is ever consulted, so it is a 401
       rather than the 403 a merely non-admin token gets. */
    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminRoute_ExpiredAdminToken_Returns401(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestTokenFactory.CreateExpiredToken(Guid.NewGuid()));

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Scenario 1: the admin queue -----------------------------------------------------------

    [Fact]
    public async Task List_DefaultsToPendingAppeals()
    {
        await SeedPendingAppealAsync();

        var response = await AdminClient().GetAsync("/api/admin/match-appeals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var appeals = document.RootElement;

        Assert.Equal(1, appeals.GetArrayLength());
        Assert.Equal("PENDING", appeals[0].GetProperty("status").GetString());
    }

    /* Scenario 1 / 5 / 6: the admin has to see who reported what, the score, and the note in order to judge. */
    [Fact]
    public async Task List_ReturnsTheAdminFacingAppealFields()
    {
        var (lostId, foundId, lostOwner, finder) = SeedPair();
        var client = UserClient(lostOwner);

        var preview = await client.PostAsJsonAsync(
            "/api/matches/preview", new { lostItemId = lostId, foundItemId = foundId });
        preview.EnsureSuccessStatusCode();

        using var previewDocument = JsonDocument.Parse(
            await preview.Content.ReadAsStringAsync());

        await client.PostAsJsonAsync(
            "/api/matches/appeals",
            new
            {
                lostItemId = lostId,
                foundItemId = foundId,
                previewVersion = previewDocument.RootElement
                    .GetProperty("previewVersion").GetString(),
                note = "Both reports describe the same kettle."
            });

        using var document = JsonDocument.Parse(
            await AdminClient().GetStringAsync("/api/admin/match-appeals?status=PENDING"));

        var appeal = document.RootElement[0];

        Assert.Equal(lostOwner, appeal.GetProperty("appellantId").GetGuid());
        Assert.Equal("LOST", appeal.GetProperty("appellantRole").GetString());
        Assert.Equal(lostOwner, appeal.GetProperty("lostReporterId").GetGuid());
        Assert.Equal(finder, appeal.GetProperty("finderId").GetGuid());
        Assert.Equal("Both reports describe the same kettle.", appeal.GetProperty("note").GetString());
        Assert.Equal("Green kettle", appeal.GetProperty("lost").GetProperty("title").GetString());
        Assert.Equal("Orange bicycle", appeal.GetProperty("found").GetProperty("title").GetString());
        Assert.True(appeal.GetProperty("score").GetDecimal() < 60m);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("VERIFIED")]
    [InlineData("REJECTED")]
    public async Task List_AcceptsEveryDecidableStatus(string status)
    {
        await SeedPendingAppealAsync();

        var response = await AdminClient()
            .GetAsync($"/api/admin/match-appeals?status={status}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task List_UnknownStatus_Returns400()
    {
        var response = await AdminClient()
            .GetAsync("/api/admin/match-appeals?status=APPROVED");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Status must be PENDING, VERIFIED or REJECTED.",
            await ReadErrorAsync(response));
    }

    /* Worth pinning rather than assuming: ListAsync calls status.Trim() with no null guard, so if an empty
       status ever reached it as null or "" this would be a 500. It cannot - an empty query value for a
       parameter with a default is treated as "not supplied", so status falls back to PENDING and the request
       succeeds. Confirmed against the real binder, not reasoned about. */
    [Fact]
    public async Task List_EmptyStatusQueryParameter_FallsBackToThePendingDefault()
    {
        await SeedPendingAppealAsync();

        var response = await AdminClient()
            .GetAsync("/api/admin/match-appeals?status=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var appeals = document.RootElement;

        Assert.Equal(1, appeals.GetArrayLength());
        Assert.Equal("PENDING", appeals[0].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public async Task List_PageOutOfRange_Returns400(int page)
    {
        var response = await AdminClient()
            .GetAsync($"/api/admin/match-appeals?page={page}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid page number.", await ReadErrorAsync(response));
    }

    // ---- Scenario 2: the appeal detail ---------------------------------------------------------

    [Fact]
    public async Task Open_ReturnsTheSavedScoreAndTheCurrentScore()
    {
        var appealId = await SeedPendingAppealAsync();

        var response = await AdminClient()
            .GetAsync($"/api/admin/match-appeals/{appealId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = document.RootElement;

        var saved = detail.GetProperty("appeal").GetProperty("score").GetDecimal();
        var current = detail.GetProperty("current").GetProperty("score").GetDecimal();

        Assert.True(saved < 60m);
        Assert.Equal(saved, current);
        Assert.Equal(
            JsonValueKind.Null,
            detail.GetProperty("currentUnavailableReason").ValueKind);
    }

    [Fact]
    public async Task Open_UnknownAppeal_Returns404()
    {
        var response = await AdminClient()
            .GetAsync($"/api/admin/match-appeals/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Appeal not found.", await ReadErrorAsync(response));
    }

    /* Scenario 2: a report resolved since the appeal was sent must not stop the admin opening the appeal. */
    [Fact]
    public async Task Open_ReportResolvedInItemService_ReportsInactiveInsteadOfFailing()
    {
        var (lostId, foundId, lostOwner, finder) = SeedPair();
        var client = UserClient(lostOwner);

        var preview = await client.PostAsJsonAsync(
            "/api/matches/preview", new { lostItemId = lostId, foundItemId = foundId });
        preview.EnsureSuccessStatusCode();

        using var previewDocument = JsonDocument.Parse(
            await preview.Content.ReadAsStringAsync());

        var created = await client.PostAsJsonAsync(
            "/api/matches/appeals",
            new
            {
                lostItemId = lostId,
                foundItemId = foundId,
                previewVersion = previewDocument.RootElement
                    .GetProperty("previewVersion").GetString(),
                note = "Please review."
            });

        using var createdDocument = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var appealId = createdDocument.RootElement.GetProperty("id").GetGuid();

        // The found report is resolved after the appeal was sent.
        _factory.ItemServiceHandler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, finder, status: "RESOLVED", title: "Orange bicycle",
                category: "Vehicles", description: "An orange bicycle with a flat tyre."));

        var response = await AdminClient()
            .GetAsync($"/api/admin/match-appeals/{appealId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(
            "REPORT_INACTIVE",
            document.RootElement.GetProperty("currentUnavailableReason").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("current").ValueKind);
    }

    // ---- Scenario 3: verifying creates the match ------------------------------------------------

    [Fact]
    public async Task Verify_CreatesTheMatchAndMarksTheAppealVerified()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var appealId = await SeedPendingAppealAsyncFor(lostId, foundId, lostOwner);

        var response = await AdminClient()
            .PostAsync($"/api/admin/match-appeals/{appealId}/verify", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var appeal = document.RootElement;

        Assert.Equal("VERIFIED", appeal.GetProperty("status").GetString());
        Assert.NotEqual(Guid.Empty, appeal.GetProperty("decidedBy").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, appeal.GetProperty("decidedAt").ValueKind);
        Assert.Equal(1, await CountMatchesAsync(lostId, foundId));
    }

    /* Scenario 4: the appeal is already decided, so the second attempt is refused and adds no second match. */
    [Fact]
    public async Task Verify_AlreadyVerifiedAppeal_Returns409AndCreatesNoSecondMatch()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var appealId = await SeedPendingAppealAsyncFor(lostId, foundId, lostOwner);
        var client = AdminClient();

        await client.PostAsync($"/api/admin/match-appeals/{appealId}/verify", null);

        var response = await client.PostAsync(
            $"/api/admin/match-appeals/{appealId}/verify", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "This appeal has already been decided.",
            await ReadErrorAsync(response));
        Assert.Equal(1, await CountMatchesAsync(lostId, foundId));
    }

    [Fact]
    public async Task Verify_UnknownAppeal_Returns404()
    {
        var response = await AdminClient()
            .PostAsync($"/api/admin/match-appeals/{Guid.NewGuid()}/verify", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Appeal not found.", await ReadErrorAsync(response));
    }

    /* ---- Scenario 5 / 6: rejecting ------------------------------------------------------------- */

    [Fact]
    public async Task Reject_WithReason_Returns200AndRecordsTheReason()
    {
        var appealId = await SeedPendingAppealAsync();

        var response = await AdminClient().PostAsJsonAsync(
            $"/api/admin/match-appeals/{appealId}/reject",
            new { reason = "The reports describe different items." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var appeal = document.RootElement;

        Assert.Equal("REJECTED", appeal.GetProperty("status").GetString());
        Assert.Equal(
            "The reports describe different items.",
            appeal.GetProperty("rejectionReason").GetString());
        Assert.NotEqual(Guid.Empty, appeal.GetProperty("decidedBy").GetGuid());
    }

    /* The action is declared with EmptyBodyBehavior.Allow, so an admin can reject without typing a reason. */
    [Fact]
    public async Task Reject_WithNoRequestBody_Returns200AndRecordsNoReason()
    {
        var appealId = await SeedPendingAppealAsync();

        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/admin/match-appeals/{appealId}/reject")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
        };

        var response = await AdminClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("REJECTED", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("rejectionReason").ValueKind);
    }

    [Fact]
    public async Task Reject_ReasonLongerThan300Characters_Returns400AndLeavesTheAppealPending()
    {
        var appealId = await SeedPendingAppealAsync();

        var response = await AdminClient().PostAsJsonAsync(
            $"/api/admin/match-appeals/{appealId}/reject",
            new { reason = new string('r', 301) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "The reason can be at most 300 characters.",
            await ReadErrorAsync(response));
        Assert.Equal(1, await CountAppealsInStatusAsync("PENDING"));
    }

    [Fact]
    public async Task Reject_AfterVerify_Returns409AndKeepsTheVerification()
    {
        var appealId = await SeedPendingAppealAsync();
        var client = AdminClient();

        await client.PostAsync($"/api/admin/match-appeals/{appealId}/verify", null);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/match-appeals/{appealId}/reject",
            new { reason = "Too late." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await CountAppealsInStatusAsync("VERIFIED"));
        Assert.Equal(0, await CountAppealsInStatusAsync("REJECTED"));
    }

    [Fact]
    public async Task Reject_UnknownAppeal_Returns404()
    {
        var response = await AdminClient().PostAsJsonAsync(
            $"/api/admin/match-appeals/{Guid.NewGuid()}/reject",
            new { reason = "No such appeal." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Appeal not found.", await ReadErrorAsync(response));
    }

    // ---- DoD: two simultaneous decisions on the same appeal --------------------------------------

    /* The DoD requires two decisions for the same appeal submitted at the same time to end with one decision
       and at most one match. Both requests run concurrently against the real database; the appeal's
       PENDING -> decided update is a compare-and-set, so exactly one of them can win it. */
    [Fact]
    public async Task Verify_TwoSimultaneousVerifications_OneDecisionAndAtMostOneMatch()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var appealId = await SeedPendingAppealAsyncFor(lostId, foundId, lostOwner);

        var responses = await Task.WhenAll(
            AdminClient().PostAsync($"/api/admin/match-appeals/{appealId}/verify", null),
            AdminClient().PostAsync($"/api/admin/match-appeals/{appealId}/verify", null));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());

        Assert.Equal(1, await CountAppealsInStatusAsync("VERIFIED"));
        Assert.Equal(1, await CountMatchesAsync(lostId, foundId));
    }

    [Fact]
    public async Task Reject_TwoSimultaneousRejections_OneDecision()
    {
        var appealId = await SeedPendingAppealAsync();

        var responses = await Task.WhenAll(
            AdminClient().PostAsJsonAsync(
                $"/api/admin/match-appeals/{appealId}/reject", new { reason = "First reason." }),
            AdminClient().PostAsJsonAsync(
                $"/api/admin/match-appeals/{appealId}/reject", new { reason = "Second reason." }));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());

        Assert.Equal(1, await CountAppealsInStatusAsync("REJECTED"));
    }

    /* A verify and a reject racing each other must still leave exactly one decision behind. */
    [Fact]
    public async Task Verify_AndReject_AtTheSameTime_StillLeaveOneDecision()
    {
        var appealId = await SeedPendingAppealAsync();

        var responses = await Task.WhenAll(
            AdminClient().PostAsync($"/api/admin/match-appeals/{appealId}/verify", null),
            AdminClient().PostAsJsonAsync(
                $"/api/admin/match-appeals/{appealId}/reject", new { reason = "Rejected." }));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());

        Assert.Equal(
            1,
            await CountAppealsInStatusAsync("VERIFIED") +
            await CountAppealsInStatusAsync("REJECTED"));
    }

    // Creates an appeal for an already-seeded pair, so the test keeps hold of the ids it needs afterwards.
    private async Task<Guid> SeedPendingAppealAsyncFor(Guid lostId, Guid foundId, Guid appellantId)
    {
        var client = UserClient(appellantId);

        var preview = await client.PostAsJsonAsync(
            "/api/matches/preview", new { lostItemId = lostId, foundItemId = foundId });
        preview.EnsureSuccessStatusCode();

        using var previewDocument = JsonDocument.Parse(
            await preview.Content.ReadAsStringAsync());

        var created = await client.PostAsJsonAsync(
            "/api/matches/appeals",
            new
            {
                lostItemId = lostId,
                foundItemId = foundId,
                previewVersion = previewDocument.RootElement
                    .GetProperty("previewVersion").GetString(),
                note = "Both reports describe the same kettle."
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdDocument = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());

        return createdDocument.RootElement.GetProperty("id").GetGuid();
    }
}