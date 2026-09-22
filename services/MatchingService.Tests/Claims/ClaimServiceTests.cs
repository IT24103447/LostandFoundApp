using System.Net;
using MatchingService.Claims;
using MatchingService.Databases;
using MatchingService.Services;
using MatchingService.Tests.Integration;
using MatchingService.Tests.Support;
using Microsoft.AspNetCore.Http;
using MySqlConnector;

namespace MatchingService.Tests.Claims;

/// <summary>
/// Story 2 (claim and match) contract tests for ClaimService, driven against a real, disposable
/// MySQL database (Testcontainers, via ClaimServiceDbFixture) and a faked Item Service
/// (FakeItemServiceHandler). ClaimService/ClaimRepository/ClaimItemClient have no interfaces, so they
/// are constructed directly rather than through DI, the same "one real dependency, one faked" shape
/// Story 1's MatchingServiceDbApiFactory tests use.
///
/// Important, read before extending this class: the shipped code does not implement the two-step
/// claim-then-confirm workflow the written acceptance criteria describe (Scenarios 10-14). SubmitAsync
/// writes a match directly with a claimant-role-confirmed terminal status
/// (LOST_REPORTER_CONFIRMED/FINDER_CONFIRMED), not AWAITING_CLAIMANT_CONFIRMATION, and there is no
/// confirm/cancel endpoint at all. A below-threshold submission throws and persists nothing, rather
/// than storing an AUTO_REJECTED_LOW_CONFIDENCE row. Whether this is the intended design or a gap is
/// an OPEN question, not yet resolved: dev flagged it needs to be rechecked with the BA. See
/// Bugs_Sprint3.md Acceptance Criteria Conflict #1 and Story2-ClaimAndMatch.md for the full breakdown.
/// The tests below assert what the code currently does, not a claim about which side is correct.
/// </summary>
public sealed class ClaimServiceTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;

    public ClaimServiceTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    private static ClaimItemClient BuildItemClient(FakeItemServiceHandler handler)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer test-token";

        var accessor = new HttpContextAccessor { HttpContext = context };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://item-service.test/") };

        return new ClaimItemClient(httpClient, accessor);
    }

    private ClaimService BuildService(FakeItemServiceHandler handler) =>
        new(BuildItemClient(handler), new ClaimRepository(_fixture.Connections, new BlobUrlPhotoKeyGenerator()));

    private async Task InsertImageDescriptionAsync(
        Guid itemId,
        string itemType,
        string photoUrl,
        string processingStatus,
        string? description = null,
        string? attributesJson = null,
        bool isSuperseded = false)
    {
        var (_, photoKey) = new BlobUrlPhotoKeyGenerator().Create(photoUrl);
        var now = DateTime.UtcNow;

        const string sql = """
            INSERT INTO image_descriptions (
                id, source_event_id, source_event_type, source_occurred_at, photo_key, item_id,
                item_type, blob_url, description, attributes_json, processing_status, attempts,
                created_at, updated_at, is_superseded
            ) VALUES (
                @id, @sourceEventId, 'CREATED', @now, @photoKey, @itemId, @itemType, @blobUrl,
                @description, @attributesJson, @status, 1, @now, @now, @isSuperseded
            );
            """;

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@sourceEventId", Guid.NewGuid());
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@photoKey", photoKey);
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@itemType", itemType);
        command.Parameters.AddWithValue("@blobUrl", photoUrl);
        command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("@attributesJson", (object?)attributesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", processingStatus);
        command.Parameters.AddWithValue("@isSuperseded", isSuperseded);

        await command.ExecuteNonQueryAsync();
    }

    private static void Seed(
        FakeItemServiceHandler handler,
        Guid lostId, Guid lostOwner,
        Guid foundId, Guid foundOwner,
        string title = "Blue backpack",
        string category = "Bags",
        string description = "A blue backpack with a front pocket.",
        List<string>? lostPhotoUrls = null,
        List<string>? foundPhotoUrls = null,
        string lostStatus = "ACTIVE",
        string foundStatus = "ACTIVE")
    {
        handler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, status: lostStatus, title: title, category: category,
                description: description, photoUrls: lostPhotoUrls));

        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, foundOwner, status: foundStatus, title: title, category: category,
                description: description, photoUrls: foundPhotoUrls));
    }

    // Scenario 5 (input validation): the same item cannot be offered as both halves of the pair.
    [Fact]
    public async Task PreviewAsync_LostAndFoundIdsAreTheSame_ThrowsBadRequest()
    {
        var handler = new FakeItemServiceHandler();
        var id = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(handler).PreviewAsync(
                new PairRequest(id, id), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
    }

    /* Scenario 3: a user who owns neither side of the pair (in practice, someone with no active
       report of the opposite type to claim with) is rejected by the backend directly, not just hidden
       from the UI. */
    [Fact]
    public async Task PreviewAsync_CallerOwnsNeitherReport_ThrowsForbidden()
    {
        var handler = new FakeItemServiceHandler();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, Guid.NewGuid(), foundId, Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(handler).PreviewAsync(
                new PairRequest(lostId, foundId), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
    }

    // A user cannot match their own lost report against their own found report.
    [Fact]
    public async Task PreviewAsync_SameOwnerOnBothSides_ThrowsBadRequest()
    {
        var handler = new FakeItemServiceHandler();
        var owner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, owner, foundId, owner);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(handler).PreviewAsync(
                new PairRequest(lostId, foundId), owner, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
    }

    // Scenario 18: a report that is no longer ACTIVE cannot be previewed, even by its own owner.
    [Fact]
    public async Task PreviewAsync_FoundReportNoLongerActive_ThrowsConflict()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, lostOwner, foundId, Guid.NewGuid(), foundStatus: "RESOLVED");

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(handler).PreviewAsync(
                new PairRequest(lostId, foundId), lostOwner, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    // Scenario 5: previewing a valid, eligible pair returns both sides and a score, and creates nothing.
    [Fact]
    public async Task PreviewAsync_ValidPair_ReturnsSideBySideViewAndPersistsNoMatch()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var repository = new ClaimRepository(_fixture.Connections, new BlobUrlPhotoKeyGenerator());
        var service = new ClaimService(BuildItemClient(handler), repository);

        var preview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);

        Assert.Equal(lostId, preview.Lost.Id);
        Assert.Equal(foundId, preview.Found.Id);
        Assert.Equal("LOST", preview.ClaimantRole);
        Assert.False(await repository.PairExistsAsync(lostId, foundId, CancellationToken.None));
    }

    /* Scenario 9: with identical title/category/description and no photo on either side, matching
       still proceeds and re-normalizes across the available (text-only) components. Deliberately
       identical text gives a hand-computable, unambiguous expected score of exactly 100. */
    [Fact]
    public async Task PreviewAsync_NeitherReportHasPhoto_RenormalizesAcrossTextOnlyComponents()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(
            handler, lostId, lostOwner, foundId, Guid.NewGuid(),
            title: "Red umbrella", category: "Accessories", description: "A red umbrella with a wooden handle.");

        var preview = await BuildService(handler).PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);

        Assert.Equal(100m, preview.Score);
        Assert.True(preview.CanClaim);
        Assert.Equal(0m, preview.Breakdown.ImageDescription);
        Assert.Equal(0m, preview.Breakdown.ImageAttributes);
    }

    /* Scenario 8: only one side has a current photo. Matching still proceeds, excludes the image
       components entirely (rather than scoring them 0), and re-normalizes so the report without a
       photo is not penalized. Proven by keeping every text field identical: if the missing image
       were scored as 0 instead of excluded, the renormalized score would fall well short of 100. */
    [Fact]
    public async Task PreviewAsync_OnlyOneReportHasPhoto_ExcludesImageComponentsWithoutPenalty()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(
            handler, lostId, lostOwner, foundId, Guid.NewGuid(),
            title: "Grey rucksack", category: "Bags", description: "A grey rucksack, lightly worn.",
            lostPhotoUrls: ["https://blob.example.com/lost-only.jpg"],
            foundPhotoUrls: []);

        var preview = await BuildService(handler).PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);

        Assert.Equal(100m, preview.Score);
        Assert.Equal(0m, preview.Breakdown.ImageDescription);
    }

    /* Scenario 6: both reports have a current photo and a COMPLETED description for it. The
       confidence score now includes the image-description and image-attribute components, and the
       comparison uses only the current photo's description: a superseded row for a previously
       replaced photo (a different photo_key, kept for the record) is never looked up. */
    [Fact]
    public async Task PreviewAsync_BothCurrentPhotosHaveCompletedDescriptions_IncludesImageScoringUsingOnlyCurrentRows()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        const string currentText = "A worn brown leather wallet with a gold clasp.";

        Seed(
            handler, lostId, lostOwner, foundId, Guid.NewGuid(),
            title: "Brown wallet", category: "Accessories", description: "A brown leather wallet.",
            lostPhotoUrls: ["https://blob.example.com/lost-current.jpg"],
            foundPhotoUrls: ["https://blob.example.com/found-current.jpg"]);

        // A stale row for a photo this item no longer has, deliberately worded differently. Must be ignored.
        await InsertImageDescriptionAsync(
            lostId, "LOST", "https://blob.example.com/lost-old-replaced.jpg", "COMPLETED",
            description: "Nothing at all like the current photo.", isSuperseded: true);

        await InsertImageDescriptionAsync(
            lostId, "LOST", "https://blob.example.com/lost-current.jpg", "COMPLETED",
            description: currentText, attributesJson: """{"objectType":"wallet","colours":["brown"]}""");

        await InsertImageDescriptionAsync(
            foundId, "FOUND", "https://blob.example.com/found-current.jpg", "COMPLETED",
            description: currentText, attributesJson: """{"objectType":"wallet","colours":["brown"]}""");

        var preview = await BuildService(handler).PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);

        Assert.Equal(100m, preview.Breakdown.ImageDescription);
        Assert.Equal(100m, preview.Breakdown.ImageAttributes);
        Assert.Equal(100m, preview.Score);
    }

    // Scenario 7: a current photo's description is still PENDING. The preview fails clearly rather than silently scoring around it.
    [Fact]
    public async Task PreviewAsync_CurrentPhotoDescriptionPending_ThrowsConflictAndCreatesNoMatch()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(
            handler, lostId, lostOwner, foundId, Guid.NewGuid(),
            lostPhotoUrls: ["https://blob.example.com/lost-pending.jpg"],
            foundPhotoUrls: ["https://blob.example.com/found-pending.jpg"]);

        await InsertImageDescriptionAsync(
            lostId, "LOST", "https://blob.example.com/lost-pending.jpg", "PENDING");

        var repository = new ClaimRepository(_fixture.Connections, new BlobUrlPhotoKeyGenerator());
        var service = new ClaimService(BuildItemClient(handler), repository);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.PreviewAsync(new PairRequest(lostId, foundId), lostOwner, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.False(await repository.PairExistsAsync(lostId, foundId, CancellationToken.None));
    }

    // Scenario 7, the other named status: PROCESSING is treated the same as PENDING, not as a failure to score around.
    [Fact]
    public async Task PreviewAsync_CurrentPhotoDescriptionProcessing_ThrowsConflict()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(
            handler, lostId, lostOwner, foundId, Guid.NewGuid(),
            lostPhotoUrls: ["https://blob.example.com/lost-processing.jpg"],
            foundPhotoUrls: ["https://blob.example.com/found-processing.jpg"]);

        await InsertImageDescriptionAsync(
            lostId, "LOST", "https://blob.example.com/lost-processing.jpg", "PROCESSING");

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(handler).PreviewAsync(
                new PairRequest(lostId, foundId), lostOwner, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    /* Scenario 10, as the code actually implements it (see the class remark above): submitting a pair
       that meets the 60% threshold creates a match and stores the score used. The claimant's role
       decides which "confirmed" status is written. This is the lost-reporter-claims direction. */
    [Fact]
    public async Task SubmitAsync_LostReporterClaimsAtOrAboveThreshold_CreatesMatchWithLostReporterConfirmedStatus()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, lostOwner, foundId, Guid.NewGuid(), description: "Identical wording on both sides.");

        var service = BuildService(handler);
        var preview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);

        var match = await service.SubmitAsync(
            new SubmitClaimRequest(lostId, foundId, preview.PreviewVersion),
            lostOwner,
            CancellationToken.None);

        Assert.Equal("LOST_REPORTER_CONFIRMED", match.Status);
        Assert.Equal("LOST", match.ClaimantRole);
        Assert.Equal(preview.Score, match.Score);
    }

    // The mirror image of the test above: the finder claims a lost report, and the status reflects that role instead.
    [Fact]
    public async Task SubmitAsync_FinderClaimsAtOrAboveThreshold_CreatesMatchWithFinderConfirmedStatus()
    {
        var handler = new FakeItemServiceHandler();
        var foundOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, Guid.NewGuid(), foundId, foundOwner, description: "Identical wording on both sides.");

        var service = BuildService(handler);
        var preview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), foundOwner, CancellationToken.None);

        var match = await service.SubmitAsync(
            new SubmitClaimRequest(lostId, foundId, preview.PreviewVersion),
            foundOwner,
            CancellationToken.None);

        Assert.Equal("FINDER_CONFIRMED", match.Status);
        Assert.Equal("FOUND", match.ClaimantRole);
    }

    /* Scenario 11, as the code actually implements it: a below-threshold submission is rejected and,
       unlike the AC's AUTO_REJECTED_LOW_CONFIDENCE status, nothing is written to the matches table at
       all (see the class remark above). */
    [Fact]
    public async Task SubmitAsync_ScoreBelowThreshold_ThrowsUnprocessableAndCreatesNoMatch()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();

        handler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, title: "Green kettle", category: "Kitchen",
                description: "A green kettle with a whistle."));

        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, Guid.NewGuid(), title: "Orange bicycle", category: "Vehicles",
                description: "An orange bicycle with a flat tyre."));

        var repository = new ClaimRepository(_fixture.Connections, new BlobUrlPhotoKeyGenerator());
        var service = new ClaimService(BuildItemClient(handler), repository);

        var preview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);
        Assert.False(preview.CanClaim);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SubmitAsync(
                new SubmitClaimRequest(lostId, foundId, preview.PreviewVersion),
                lostOwner,
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.False(await repository.PairExistsAsync(lostId, foundId, CancellationToken.None));
    }

    // A submission with no prior preview is rejected before any scoring happens.
    [Fact]
    public async Task SubmitAsync_MissingPreviewVersion_ThrowsBadRequest()
    {
        var handler = new FakeItemServiceHandler();

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(handler).SubmitAsync(
                new SubmitClaimRequest(Guid.NewGuid(), Guid.NewGuid(), ""),
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
    }

    /* Scenario 19: the report changed after the preview was taken (here, the description was edited).
       The stale preview version cannot be used to submit; the caller is told to preview again rather
       than the change being silently absorbed into the match. */
    [Fact]
    public async Task SubmitAsync_ReportChangedSincePreview_ThrowsConflict()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, lostOwner, foundId, Guid.NewGuid(), description: "Original description.");

        var service = BuildService(handler);
        var stalePreview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);

        // The lost report is edited between preview and submission.
        handler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, description: "An edited description, changed after the preview."));

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SubmitAsync(
                new SubmitClaimRequest(lostId, foundId, stalePreview.PreviewVersion),
                lostOwner,
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    // Scenario 17 (submit side): a user who owns neither report cannot submit a claim for it, no matter what they pass as the preview version.
    [Fact]
    public async Task SubmitAsync_CallerOwnsNeitherReport_ThrowsForbidden()
    {
        var handler = new FakeItemServiceHandler();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, Guid.NewGuid(), foundId, Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(handler).SubmitAsync(
                new SubmitClaimRequest(lostId, foundId, "irrelevant"),
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
    }

    // Scenario 18 (submit side): a report resolved after the preview was taken is caught at submission too.
    [Fact]
    public async Task SubmitAsync_ReportResolvedSincePreview_ThrowsConflict()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler);
        var preview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);

        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(foundId, Guid.NewGuid(), status: "RESOLVED"));

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SubmitAsync(
                new SubmitClaimRequest(lostId, foundId, preview.PreviewVersion),
                lostOwner,
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    // Scenario 15: once a pair has a match, a second attempt (from either direction) is blocked, not silently duplicated.
    [Fact]
    public async Task SubmitAsync_PairAlreadyMatched_SecondAttemptThrowsConflict()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        Seed(handler, lostId, lostOwner, foundId, Guid.NewGuid(), description: "Identical wording on both sides.");

        var service = BuildService(handler);
        var firstPreview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);
        await service.SubmitAsync(
            new SubmitClaimRequest(lostId, foundId, firstPreview.PreviewVersion), lostOwner, CancellationToken.None);

        // Even a fresh, valid preview of the same pair is rejected once a match already exists.
        var secondPreviewAttempt = await Assert.ThrowsAsync<ClaimException>(() =>
            service.PreviewAsync(new PairRequest(lostId, foundId), lostOwner, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, secondPreviewAttempt.StatusCode);
    }

    /* Scenario 16: a pair whose only prior attempt fell below the threshold is not blocked from being
       claimed again. Nothing was ever persisted for the failed attempt (proven above), so a later
       attempt with improved report data is evaluated fresh and independently, exactly as if it were
       the first attempt. */
    [Fact]
    public async Task SubmitAsync_PriorAttemptWasBelowThreshold_LaterAttemptSucceedsOnFreshScore()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();

        handler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, title: "Silver watch", category: "Accessories",
                description: "A silver watch with a leather strap."));

        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, Guid.NewGuid(), title: "Yellow kayak", category: "Sports",
                description: "A yellow kayak, slightly scratched."));

        var service = BuildService(handler);
        var lowPreview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);
        Assert.False(lowPreview.CanClaim);

        await Assert.ThrowsAsync<ClaimException>(() =>
            service.SubmitAsync(
                new SubmitClaimRequest(lostId, foundId, lowPreview.PreviewVersion), lostOwner, CancellationToken.None));

        // The found report's own details are corrected, now genuinely matching the lost report.
        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, Guid.NewGuid(), title: "Silver watch", category: "Accessories",
                description: "A silver watch with a leather strap."));

        var freshPreview = await service.PreviewAsync(
            new PairRequest(lostId, foundId), lostOwner, CancellationToken.None);
        Assert.True(freshPreview.CanClaim);

        var match = await service.SubmitAsync(
            new SubmitClaimRequest(lostId, foundId, freshPreview.PreviewVersion), lostOwner, CancellationToken.None);

        Assert.Equal("LOST_REPORTER_CONFIRMED", match.Status);
    }

    /* Backs GET /api/matches/mine, a second and simpler read path than MatchReadRepository/
       MatchQueriesController (covered separately in MatchReadServiceTests): it lists the caller's own
       matches straight from the matches table, newest first, and excludes a match the caller has no
       part in. */
    [Fact]
    public async Task GetMineAsync_CallerHasOneMatchAndIsUnrelatedToAnother_ReturnsOnlyTheirOwnNewestFirst()
    {
        var repository = new ClaimRepository(_fixture.Connections, new BlobUrlPhotoKeyGenerator());
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();

        var lost = new ItemReport { Id = lostId, UserId = lostOwner, Status = "ACTIVE" };
        var found = new ItemReport { Id = foundId, UserId = Guid.NewGuid(), Status = "ACTIVE" };
        var pair = new VerifiedPair(lost, found, "LOST");
        var preview = new ClaimPreview(
            lost.ToView("LOST"), found.ToView("FOUND"), 100m, ClaimService.Threshold, true, "LOST", "v1",
            new ScoreBreakdown(100m, 100m, 100m, 0m, 0m));

        var created = await repository.CreateAsync(pair, preview, lostOwner, CancellationToken.None);

        // A second match this caller is not a party to at all: must not appear in their "mine" list.
        var otherPair = new VerifiedPair(
            new ItemReport { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = "ACTIVE" },
            new ItemReport { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = "ACTIVE" },
            "LOST");
        await repository.CreateAsync(otherPair, preview, otherPair.Lost.UserId, CancellationToken.None);

        var mine = await repository.GetMineAsync(lostOwner, page: 1, CancellationToken.None);

        var match = Assert.Single(mine);
        Assert.Equal(created.Id, match.Id);
        Assert.Equal("LOST_REPORTER_CONFIRMED", match.Status);
    }

    /* The application-level PairExistsAsync check is the normal guard against a duplicate match, but
       the database's own unique constraint on (lost_item_id, found_item_id) is the real backstop.
       Calling CreateAsync directly, bypassing ClaimService's own pre-check, proves that backstop
       exists independently of the application-level guard. */
    [Fact]
    public async Task CreateAsync_CalledTwiceForSamePairDirectly_SecondCallThrowsConflict()
    {
        var repository = new ClaimRepository(_fixture.Connections, new BlobUrlPhotoKeyGenerator());
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();

        var lost = new ItemReport { Id = lostId, UserId = Guid.NewGuid(), Status = "ACTIVE" };
        var found = new ItemReport { Id = foundId, UserId = Guid.NewGuid(), Status = "ACTIVE" };
        var pair = new VerifiedPair(lost, found, "LOST");

        var preview = new ClaimPreview(
            lost.ToView("LOST"), found.ToView("FOUND"), 100m, ClaimService.Threshold, true, "LOST", "v1",
            new ScoreBreakdown(100m, 100m, 100m, 0m, 0m));

        await repository.CreateAsync(pair, preview, lost.UserId, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            repository.CreateAsync(pair, preview, lost.UserId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }
}
