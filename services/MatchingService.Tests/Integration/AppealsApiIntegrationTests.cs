using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MatchingService.Tests.Support;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story LF-81 integration tests for api/matches/appeals, over real HTTP against ClaimsApiFactory: real JWT
/// bearer validation, real routing, real model binding, real status codes and JSON, and a real MySQL behind
/// them. Item Service is faked on ClaimItemClient's primary handler.
///
/// Appeals are created the way a user creates them - POST /api/matches/preview then POST /api/matches/appeals -
/// so nothing here depends on hand-written database rows.
/// </summary>
[Collection("Docker Integration Tests 13")]
public sealed class AppealsApiIntegrationTests : IClassFixture<ClaimsApiFactory>, IAsyncLifetime
{
    private readonly ClaimsApiFactory _factory;

    public AppealsApiIntegrationTests(ClaimsApiFactory factory)
    {
        _factory = factory;
    }

    /* match_appeals and matches are shared container state within this class, and /mine pages 20 rows at a
       time, so an appeal left behind by an earlier test would otherwise show up in the next test's list. */
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

    private HttpClient ClientFor(Guid userId) =>
        ClientWithToken(JwtTestTokenFactory.CreateVerifiedUserToken(userId));

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// <summary>
    /// Two unrelated reports owned by two different users, so the pair scores far below the 60% threshold -
    /// the only situation an appeal is legal in. The reports exist only in the faked Item Service.
    /// </summary>
    private (Guid LostId, Guid FoundId, Guid LostOwner, Guid Finder) SeedPair(
        string lostTitle = "Green kettle",
        string lostCategory = "Kitchen",
        string lostDescription = "A green kettle with a whistle.",
        string foundTitle = "Orange bicycle",
        string foundCategory = "Vehicles",
        string foundDescription = "An orange bicycle with a flat tyre.")
    {
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        var lostOwner = Guid.NewGuid();
        var finder = Guid.NewGuid();

        _factory.ItemServiceHandler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, title: lostTitle, category: lostCategory,
                description: lostDescription));

        _factory.ItemServiceHandler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, finder, title: foundTitle, category: foundCategory,
                description: foundDescription));

        return (lostId, foundId, lostOwner, finder);
    }

    private static async Task<string> PreviewAsync(
        HttpClient client,
        Guid lostId,
        Guid foundId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/matches/preview",
            new { lostItemId = lostId, foundItemId = foundId });

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("previewVersion").GetString()!;
    }

    private static Task<HttpResponseMessage> SendAppealAsync(
        HttpClient client,
        Guid lostId,
        Guid foundId,
        string previewVersion,
        string? note = "These are the same item. Please review.") =>
        client.PostAsJsonAsync(
            "/api/matches/appeals",
            new { lostItemId = lostId, foundItemId = foundId, previewVersion, note });

    /// <summary>Previews and sends an appeal, returning the created appeal's id.</summary>
    private async Task<Guid> SendAppealAndGetIdAsync(
        HttpClient client,
        Guid lostId,
        Guid foundId)
    {
        var previewVersion = await PreviewAsync(client, lostId, foundId);
        var response = await SendAppealAsync(client, lostId, foundId, previewVersion);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("error").GetString()!;
    }

    // ---- The JWT bearer pipeline protects every route -------------------------------------------

    public static IEnumerable<object[]> ProtectedRequests()
    {
        yield return new object[] { HttpMethod.Post, "/api/matches/appeals" };
        yield return new object[] { HttpMethod.Get, "/api/matches/appeals/mine" };
        yield return new object[]
        {
            HttpMethod.Get,
            $"/api/matches/appeals/pair-status?lostItemId={Guid.NewGuid()}&foundItemId={Guid.NewGuid()}"
        };
        yield return new object[]
        {
            HttpMethod.Get,
            $"/api/matches/appeals/edit-warning?type=LOST&id={Guid.NewGuid()}"
        };
    }

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task ProtectedEndpoint_NoToken_Returns401(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task ProtectedEndpoint_TokenSignedWithWrongSecret_Returns401(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTestTokenFactory.CreateTokenWithWrongSecret(Guid.NewGuid()));

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task ProtectedEndpoint_ExpiredToken_Returns401(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTestTokenFactory.CreateExpiredToken(Guid.NewGuid()));

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /* VerifiedClaimUser guards only the send. The three reads carry plain [Authorize], so an unverified but
       otherwise valid token is refused on POST with 403 and accepted on the reads - a real distinction worth
       pinning, since the policy is not applied at the controller level.

       The preview has to be taken with a *verified* token for the same user, because /api/matches/preview is
       itself VerifiedClaimUser-protected - an unverified caller never gets as far as the send. */
    [Fact]
    public async Task Send_UnverifiedEmail_Returns403()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();

        var previewVersion = await PreviewAsync(ClientFor(lostOwner), lostId, foundId);

        var response = await SendAppealAsync(
            ClientWithToken(JwtTestTokenFactory.CreateUnverifiedUserToken(lostOwner)),
            lostId,
            foundId,
            previewVersion);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mine_UnverifiedEmail_Succeeds()
    {
        var client = ClientWithToken(JwtTestTokenFactory.CreateUnverifiedUserToken(Guid.NewGuid()));

        var response = await client.GetAsync("/api/matches/appeals/mine");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Sending an appeal ---------------------------------------------------------------------

    [Fact]
    public async Task Send_BelowThresholdPair_Returns201WithTheAppeal()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var client = ClientFor(lostOwner);

        var previewVersion = await PreviewAsync(client, lostId, foundId);
        var response = await SendAppealAsync(client, lostId, foundId, previewVersion, "Same kettle.");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;

        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
        Assert.Equal("PENDING", body.GetProperty("status").GetString());
        Assert.Equal("LOST", body.GetProperty("role").GetString());
        Assert.Equal("Same kettle.", body.GetProperty("note").GetString());
        Assert.Equal("Green kettle", body.GetProperty("lost").GetProperty("title").GetString());
        Assert.Equal("Orange bicycle", body.GetProperty("found").GetProperty("title").GetString());

        // The DoD promises the user never learns who the deciding admin is.
        Assert.False(body.TryGetProperty("decidedBy", out _));
    }

    /* Scenario 3 / DoD: a second appeal on the same pair is refused. */
    [Fact]
    public async Task Send_SecondAppealOnTheSamePair_Returns409()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var client = ClientFor(lostOwner);

        var previewVersion = await PreviewAsync(client, lostId, foundId);
        await SendAppealAsync(client, lostId, foundId, previewVersion);

        var response = await SendAppealAsync(client, lostId, foundId, previewVersion);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "This pair has already been submitted for appeal.",
            await ReadErrorAsync(response));
    }

    /* Scenario 4 / DoD: a pair that can be claimed directly must not become an appeal. */
    [Fact]
    public async Task Send_PairThatMeetsTheThreshold_Returns422()
    {
        const string shared = "A distinctive teal bicycle helmet with a cracked visor.";
        var (lostId, foundId, lostOwner, _) = SeedPair(
            lostTitle: "Teal bicycle helmet",
            lostCategory: "Sports",
            lostDescription: shared,
            foundTitle: "Teal bicycle helmet",
            foundCategory: "Sports",
            foundDescription: shared);

        var client = ClientFor(lostOwner);
        var previewVersion = await PreviewAsync(client, lostId, foundId);

        var response = await SendAppealAsync(client, lostId, foundId, previewVersion);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("This pair can be claimed directly.", await ReadErrorAsync(response));
    }

    /* The preview version is a hash of the report details: if the reports changed after the user looked at
       them, the appeal is refused so they look again. */
    [Fact]
    public async Task Send_StalePreviewVersion_Returns409()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var client = ClientFor(lostOwner);

        var staleVersion = await PreviewAsync(client, lostId, foundId);

        var response = await SendAppealAsync(client, lostId, foundId, staleVersion + "x");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "The report information changed. Preview it again.",
            await ReadErrorAsync(response));
    }

    [Fact]
    public async Task Send_MissingPreviewVersion_Returns400()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var client = ClientFor(lostOwner);

        var response = await SendAppealAsync(client, lostId, foundId, previewVersion: "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Preview the reports before sending an appeal.",
            await ReadErrorAsync(response));
    }

    /* Scenario 2: only the owner of one of the two reports may appeal it. The preview has to be taken by the
       real owner, because preview is ownership-checked too - the point of the test is what the *send* does when
       the caller owns neither report. */
    [Fact]
    public async Task Send_ByAUserWhoOwnsNeitherReport_Returns403()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var stranger = Guid.NewGuid();

        var previewVersion = await PreviewAsync(ClientFor(lostOwner), lostId, foundId);

        var response = await SendAppealAsync(
            ClientFor(stranger), lostId, foundId, previewVersion);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You must own one selected report.", await ReadErrorAsync(response));
    }

    /* The finder of the found report may appeal too - Scenario 1 does not restrict this to the lost reporter. */
    [Fact]
    public async Task Send_ByTheFinder_IsRecordedWithTheFoundRole()
    {
        var (lostId, foundId, _, finder) = SeedPair();
        var client = ClientFor(finder);

        var previewVersion = await PreviewAsync(client, lostId, foundId);
        var response = await SendAppealAsync(client, lostId, foundId, previewVersion);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("FOUND", document.RootElement.GetProperty("role").GetString());
    }

    /* Scenario 7: the other user must not see the appeal in their own list. */
    [Fact]
    public async Task Mine_ReturnsOnlyTheCallersAppeals()
    {
        var (lostId, foundId, lostOwner, finder) = SeedPair();

        await SendAppealAndGetIdAsync(ClientFor(lostOwner), lostId, foundId);

        using var document = JsonDocument.Parse(
            await ClientFor(finder).GetStringAsync("/api/matches/appeals/mine"));

        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Mine_ReturnsTheCallersAppeal()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var appealId = await SendAppealAndGetIdAsync(ClientFor(lostOwner), lostId, foundId);

        using var document = JsonDocument.Parse(
            await ClientFor(lostOwner).GetStringAsync("/api/matches/appeals/mine"));

        var appeals = document.RootElement;
        Assert.Equal(1, appeals.GetArrayLength());
        Assert.Equal(appealId, appeals[0].GetProperty("id").GetGuid());

        // No decision yet, and no admin identity - Scenario 6.
        Assert.Equal(JsonValueKind.Null, appeals[0].GetProperty("decidedAt").ValueKind);
        Assert.False(appeals[0].TryGetProperty("decidedBy", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public async Task Mine_PageOutOfRange_Returns400(int page)
    {
        var response = await ClientFor(Guid.NewGuid())
            .GetAsync($"/api/matches/appeals/mine?page={page}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid page number.", await ReadErrorAsync(response));
    }

    // ---- Pair status ----------------------------------------------------------------------------

    [Fact]
    public async Task PairStatus_BeforeAndAfterSendingTheAppeal()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var client = ClientFor(lostOwner);
        var path = $"/api/matches/appeals/pair-status?lostItemId={lostId}&foundItemId={foundId}";

        using (var before = JsonDocument.Parse(await client.GetStringAsync(path)))
        {
            Assert.False(before.RootElement.GetProperty("appealed").GetBoolean());
        }

        var previewVersion = await PreviewAsync(client, lostId, foundId);
        await SendAppealAsync(client, lostId, foundId, previewVersion);

        using var after = JsonDocument.Parse(await client.GetStringAsync(path));

        Assert.True(after.RootElement.GetProperty("appealed").GetBoolean());
    }

    /* Scenario 3: the appeal button is disabled for whichever user appeals, but the other owner of the pair is
       told the pair is no longer claimable without being told who appealed it or why. */
    [Fact]
    public async Task PairStatus_IsTrueForTheOtherOwnerToo()
    {
        var (lostId, foundId, lostOwner, finder) = SeedPair();

        await SendAppealAndGetIdAsync(ClientFor(lostOwner), lostId, foundId);

        using var document = JsonDocument.Parse(
            await ClientFor(finder).GetStringAsync(
                $"/api/matches/appeals/pair-status?lostItemId={lostId}&foundItemId={foundId}"));

        Assert.True(document.RootElement.GetProperty("appealed").GetBoolean());
    }

    // ---- Edit warning ---------------------------------------------------------------------------

    /* Scenario 8: both owners are warned before editing a report that is part of an appeal - including the one
       who never appealed. */
    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task EditWarning_IsTrueForBothOwnersAfterAnAppeal(string type)
    {
        var (lostId, foundId, lostOwner, finder) = SeedPair();
        await SendAppealAndGetIdAsync(ClientFor(lostOwner), lostId, foundId);

        var owner = type == "LOST" ? lostOwner : finder;
        var itemId = type == "LOST" ? lostId : foundId;

        using var document = JsonDocument.Parse(
            await ClientFor(owner).GetStringAsync(
                $"/api/matches/appeals/edit-warning?type={type}&id={itemId}"));

        Assert.True(document.RootElement.GetProperty("warn").GetBoolean());
    }

    /* The warning is about this user's own report, so an unrelated report - or someone else's - is not warned. */
    [Fact]
    public async Task EditWarning_IsFalseForAnUnrelatedReport()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        await SendAppealAndGetIdAsync(ClientFor(lostOwner), lostId, foundId);

        var strangerReportId = Guid.NewGuid();

        using var document = JsonDocument.Parse(
            await ClientFor(lostOwner).GetStringAsync(
                $"/api/matches/appeals/edit-warning?type=LOST&id={strangerReportId}"));

        Assert.False(document.RootElement.GetProperty("warn").GetBoolean());
    }

    [Theory]
    [InlineData("BAGS")]
    [InlineData("lost-and-found")]
    public async Task EditWarning_UnknownType_Returns400(string type)
    {
        var response = await ClientFor(Guid.NewGuid())
            .GetAsync($"/api/matches/appeals/edit-warning?type={type}&id={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Type must be lost or found.", await ReadErrorAsync(response));
    }

    /* An empty type is rejected too, but by [ApiController]'s implicit model validation rather than by the
       service, so the body is a ValidationProblemDetails instead of the { "error": ... } shape every other
       failure on this controller returns. Still a 400 - just not the documented error envelope, which is worth
       knowing before a client tries to parse the message. */
    [Fact]
    public async Task EditWarning_EmptyType_Returns400FromModelValidation()
    {
        var response = await ClientFor(Guid.NewGuid())
            .GetAsync($"/api/matches/appeals/edit-warning?type=&id={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    // ---- DoD: two simultaneous appeals on the same pair -----------------------------------------

    /* The DoD requires two appeals for the same pair submitted at the same time to end with one appeal saved
       and one 409. Both requests run concurrently against the real database; whichever loses the INSERT hits
       the unique key on (lost_item_id, found_item_id). */
    [Fact]
    public async Task Send_TwoSimultaneousAppealsOnTheSamePair_OneSucceedsAndOneConflicts()
    {
        var (lostId, foundId, lostOwner, _) = SeedPair();
        var client = ClientFor(lostOwner);
        var previewVersion = await PreviewAsync(client, lostId, foundId);

        var responses = await Task.WhenAll(
            SendAppealAsync(client, lostId, foundId, previewVersion),
            SendAppealAsync(client, lostId, foundId, previewVersion));

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());

        await using var connection = new MySqlConnection(_factory.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM match_appeals WHERE lost_item_id = @lostId AND found_item_id = @foundId;",
            connection);
        command.Parameters.AddWithValue("@lostId", lostId);
        command.Parameters.AddWithValue("@foundId", foundId);

        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }
}