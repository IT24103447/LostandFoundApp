using System.Net;
using System.Net.Http.Json;
using MatchingService.Claims;
using MatchingService.Matches;
using MatchingService.Tests.Support;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Integration tests proving the real JWT bearer flow Story 2 adds (ClaimRegistration.AddManualClaims,
/// [Authorize(Policy = "VerifiedClaimUser")]) actually protects every claims/matches endpoint end to
/// end: a real token, signed and validated with a real key, not a stand-in for authentication. Covers
/// both ClaimsController (Story 2's write side) and MatchQueriesController (Story 3's Matches page read
/// side) over real HTTP, since both sit behind the same policy on the same real Program.cs pipeline.
/// JwtTestTokenFactory mints the tokens; ClaimsApiFactory hosts that real pipeline (real MySQL, faked
/// Item Service) that validates them.
/// </summary>
public sealed class ClaimsAuthorizationIntegrationTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;

    public ClaimsAuthorizationIntegrationTests(ClaimsApiFactory factory)
    {
        _factory = factory;
    }

    public static IEnumerable<object[]> ProtectedRequests()
    {
        yield return new object[] { HttpMethod.Get, "/api/matches/candidates?type=lost" };
        yield return new object[] { HttpMethod.Post, "/api/matches/preview" };
        yield return new object[] { HttpMethod.Post, "/api/matches/claim" };
        yield return new object[] { HttpMethod.Get, "/api/matches/mine" };
        yield return new object[] { HttpMethod.Get, "/api/matches" };
        yield return new object[] { HttpMethod.Get, $"/api/matches/{Guid.NewGuid()}" };
    }

    // Every protected route rejects a direct, unauthenticated attempt, not just the claim endpoint itself. No Authorization header at all.
    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task ProtectedEndpoint_NoToken_Returns401(HttpMethod method, string path)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A token that fails signature validation outright (wrong key) is an authentication failure, not merely a policy failure.
    [Fact]
    public async Task Candidates_TokenSignedWithWrongSecret_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateTokenWithWrongSecret(Guid.NewGuid()));

        var response = await client.GetAsync("/api/matches/candidates?type=lost");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Candidates_ExpiredToken_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateExpiredToken(Guid.NewGuid()));

        var response = await client.GetAsync("/api/matches/candidates?type=lost");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /* A token that authenticates successfully (valid signature, not expired) but whose owner is not
       email-verified fails the VerifiedClaimUser policy specifically: 403, not 401. */
    [Fact]
    public async Task Candidates_ValidTokenButEmailNotVerified_Returns403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateUnverifiedUserToken(Guid.NewGuid()));

        var response = await client.GetAsync("/api/matches/candidates?type=lost");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /* ClaimsController's own inline validation (page out of range), reached and mapped to a real HTTP
       response only through the real ExecuteAsync/ClaimException pipeline, not by calling ClaimService
       directly the way ClaimServiceTests does. */
    [Fact]
    public async Task Mine_PageOutOfRange_Returns400ThroughTheRealController()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateVerifiedUserToken(Guid.NewGuid()));

        var response = await client.GetAsync("/api/matches/mine?page=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The same real ExecuteAsync/ClaimException mapping, exercised through MatchQueriesController instead of ClaimsController.
    [Fact]
    public async Task List_UnknownSection_Returns400ThroughTheRealController()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateVerifiedUserToken(Guid.NewGuid()));

        var response = await client.GetAsync("/api/matches?section=not-a-real-section");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /* Story 3 Scenario 9 ("Isolation Between Users"), proven over real HTTP rather than by calling
       MatchReadService directly (already covered in MatchReadServiceTests): a genuine, valid, verified
       token belonging to neither party gets 403 from the real controller, not 404 or a leaked 200. */
    [Fact]
    public async Task Details_TokenBelongsToNeitherParty_Returns403ThroughTheRealController()
    {
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        const string sharedText = "A distinctive teal bicycle helmet with a cracked visor.";

        _factory.ItemServiceHandler
            .RespondWithJson(
                $"api/items/lost/{lostId}",
                HttpStatusCode.OK,
                FakeItemServiceHandler.Report(
                    lostId, lostOwner, title: "Teal bicycle helmet", category: "Sports", description: sharedText))
            .RespondWithJson(
                $"api/items/found/{foundId}",
                HttpStatusCode.OK,
                FakeItemServiceHandler.Report(
                    foundId, Guid.NewGuid(), title: "Teal bicycle helmet", category: "Sports", description: sharedText));

        var ownerClient = _factory.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateVerifiedUserToken(lostOwner));

        var previewResponse = await ownerClient.PostAsJsonAsync(
            "/api/matches/preview", new { lostItemId = lostId, foundItemId = foundId });
        var preview = await previewResponse.Content.ReadFromJsonAsync<ClaimPreview>();

        var claimResponse = await ownerClient.PostAsJsonAsync(
            "/api/matches/claim",
            new { lostItemId = lostId, foundItemId = foundId, previewVersion = preview!.PreviewVersion });
        var created = await claimResponse.Content.ReadFromJsonAsync<MatchView>();

        var strangerClient = _factory.CreateClient();
        strangerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateVerifiedUserToken(Guid.NewGuid()));

        var response = await strangerClient.GetAsync($"/api/matches/{created!.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The unknown-id counterpart of the test above, over real HTTP: a match id nothing created reads as not found, not forbidden.
    [Fact]
    public async Task Details_UnknownId_Returns404ThroughTheRealController()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateVerifiedUserToken(Guid.NewGuid()));

        var response = await client.GetAsync($"/api/matches/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /* The positive case: a genuine, valid, verified token reaches all the way through the real
       controller into the real ClaimItemClient, which calls the faked Item Service and returns its
       (filtered) result. Proves the identity extracted from the token (User.FindFirstValue) is the one
       actually used, not just that the request was let through. */
    [Fact]
    public async Task Candidates_ValidVerifiedToken_ReachesRealHandlerAndReturnsFilteredReports()
    {
        var userId = Guid.NewGuid();

        _factory.ItemServiceHandler.RespondWithJson(
            "api/items/lost/mine",
            HttpStatusCode.OK,
            new[]
            {
                FakeItemServiceHandler.Report(Guid.NewGuid(), userId, status: "ACTIVE", title: "My active report")
            });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateVerifiedUserToken(userId));

        var response = await client.GetAsync("/api/matches/candidates?type=lost");
        response.EnsureSuccessStatusCode();

        var reports = await response.Content.ReadFromJsonAsync<List<ClaimItemView>>();

        var report = Assert.Single(reports!);
        Assert.Equal("My active report", report.Title);
    }

    /* Full stack, one happy path, through real HTTP: a real minted token creates and confirms a claim
       against real MySQL, then reads it back through both Matches endpoints. This is the "JWT creation
       and use through and through" proof for the write path, not just the read-only candidates check
       above. The bulk of ClaimService's own branching logic is covered far more cheaply in
       ClaimServiceTests, which calls it directly; this test exists to prove the HTTP/JWT layer wires
       up to that same logic correctly, not to re-prove the logic itself. */
    [Fact]
    public async Task FullClaimFlow_ValidToken_PreviewThenClaimThenAppearsInMatchesAndDetails()
    {
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        const string sharedText = "A distinctive orange rain jacket with a broken zipper.";

        _factory.ItemServiceHandler
            .RespondWithJson(
                $"api/items/lost/{lostId}",
                HttpStatusCode.OK,
                FakeItemServiceHandler.Report(
                    lostId, lostOwner, title: "Orange rain jacket", category: "Clothing", description: sharedText))
            .RespondWithJson(
                $"api/items/found/{foundId}",
                HttpStatusCode.OK,
                FakeItemServiceHandler.Report(
                    foundId, Guid.NewGuid(), title: "Orange rain jacket", category: "Clothing", description: sharedText));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JwtTestTokenFactory.CreateVerifiedUserToken(lostOwner));

        var previewResponse = await client.PostAsJsonAsync(
            "/api/matches/preview", new { lostItemId = lostId, foundItemId = foundId });
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<ClaimPreview>();
        Assert.True(preview!.CanClaim);

        var claimResponse = await client.PostAsJsonAsync(
            "/api/matches/claim",
            new { lostItemId = lostId, foundItemId = foundId, previewVersion = preview.PreviewVersion });

        Assert.Equal(HttpStatusCode.Created, claimResponse.StatusCode);
        var created = await claimResponse.Content.ReadFromJsonAsync<MatchView>();
        Assert.Equal("LOST_REPORTER_CONFIRMED", created!.Status);

        var listResponse = await client.GetAsync("/api/matches?section=active");
        listResponse.EnsureSuccessStatusCode();
        var page = await listResponse.Content.ReadFromJsonAsync<MatchPage>();
        Assert.Contains(page!.Items, entry => entry.Id == created.Id);

        var detailsResponse = await client.GetAsync($"/api/matches/{created.Id}");
        detailsResponse.EnsureSuccessStatusCode();
        var details = await detailsResponse.Content.ReadFromJsonAsync<MatchListEntry>();
        Assert.Equal(created.Id, details!.Id);
    }
}
