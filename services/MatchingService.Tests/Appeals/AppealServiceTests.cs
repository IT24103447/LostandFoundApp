using System.Net;
using System.Text.Json;
using MatchingService.Appeals;
using MatchingService.Claims;
using MatchingService.Databases;
using MatchingService.Services;
using MatchingService.Tests.Integration;
using MatchingService.Tests.Support;
using Microsoft.AspNetCore.Http;
using MySqlConnector;

namespace MatchingService.Tests.Appeals;

/// <summary>
/// Story LF-81 contract tests for AppealService, against a real MySQL (Testcontainers) and a faked
/// Item Service (FakeItemServiceHandler), following the same shape as ClaimServiceTests.
///
/// AppealService's collaborators (ClaimService, ClaimItemClient, AppealRepository) are all sealed, so
/// they cannot be mocked. This class therefore drives the real collaborators and controls the inputs
/// through the faked Item Service, exactly as the claim tests do.
/// </summary>
[Collection("Docker Integration Tests 11")]
public sealed class AppealServiceTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;

    public AppealServiceTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    // ---- Wiring -------------------------------------------------------------------------------

    private static ClaimItemClient BuildItemClient(FakeItemServiceHandler handler)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer test-token";

        var accessor = new HttpContextAccessor { HttpContext = context };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://item-service.test/") };

        return new ClaimItemClient(httpClient, accessor);
    }

    private AppealRepository BuildAppealRepository() =>
        new(_fixture.Connections, TimeProvider.System);

    private AppealService BuildService(
        FakeItemServiceHandler handler,
        out ClaimService claims)
    {
        var items = BuildItemClient(handler);
        claims = new ClaimService(
            items,
            new ClaimRepository(_fixture.Connections, new BlobUrlPhotoKeyGenerator()));

        return new AppealService(claims, items, BuildAppealRepository());
    }

    // ---- Fixtures -----------------------------------------------------------------------------

    /* Two genuinely unrelated reports. Scoring renormalises across the text components only, and this
       pair lands far below ClaimService.Threshold, which is exactly the situation an appeal exists for
       (Scenario 1: "the match score is below 60%"). */
    private static void SeedBelowThresholdPair(
        FakeItemServiceHandler handler,
        Guid lostId,
        Guid lostOwner,
        Guid foundId,
        Guid foundOwner,
        string lostStatus = "ACTIVE",
        string foundStatus = "ACTIVE")
    {
        handler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, status: lostStatus, title: "Green kettle",
                category: "Kitchen", description: "A green kettle with a whistle."));

        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, foundOwner, status: foundStatus, title: "Orange bicycle",
                category: "Vehicles", description: "An orange bicycle with a flat tyre."));
    }

    /* Identical reports on both sides score 100, so preview.CanClaim is true. This is the pair that
       Scenario 4 ("The pair meets the threshold") and Scenario 3's last paragraph ("a rejected pair
       that later reaches 60% can still be claimed normally") are about. */
    private static void SeedAboveThresholdPair(
        FakeItemServiceHandler handler,
        Guid lostId,
        Guid lostOwner,
        Guid foundId,
        Guid foundOwner)
    {
        const string sharedText = "A distinctive teal bicycle helmet with a cracked visor.";

        handler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, title: "Teal bicycle helmet",
                category: "Sports", description: sharedText));

        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, foundOwner, title: "Teal bicycle helmet",
                category: "Sports", description: sharedText));
    }

    // Runs the real preview so the test can hand SendAsync the exact version the scorer produced.
    private static async Task<string> PreviewAsync(
        ClaimService claims,
        Guid lostId,
        Guid foundId,
        Guid callerId)
    {
        var preview = await claims.PreviewAsync(
            new PairRequest(lostId, foundId), callerId, CancellationToken.None);

        return preview.PreviewVersion;
    }

    private async Task<long> CountAppealsAsync(Guid lostId, Guid foundId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM match_appeals WHERE lost_item_id = @lostId AND found_item_id = @foundId;",
            connection);
        command.Parameters.AddWithValue("@lostId", lostId);
        command.Parameters.AddWithValue("@foundId", foundId);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountMatchesAsync(Guid lostId, Guid foundId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM matches WHERE lost_item_id = @lostId AND found_item_id = @foundId;",
            connection);
        command.Parameters.AddWithValue("@lostId", lostId);
        command.Parameters.AddWithValue("@foundId", foundId);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task SetCreatedAtAsync(Guid appealId, DateTime createdAt)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "UPDATE match_appeals SET created_at = @createdAt WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("@id", appealId);
        command.Parameters.AddWithValue("@createdAt", DateTime.SpecifyKind(createdAt, DateTimeKind.Utc));

        await command.ExecuteNonQueryAsync();
    }

    // Flips a PENDING appeal to a decided state directly, so the Verified/Rejected edit-warning rules
    // (Scenario 8) can be exercised without going through the admin endpoints under test elsewhere.
    private async Task DecideAppealAsync(
        Guid appealId,
        string status,
        Guid adminId,
        string? rejectionReason = null)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            UPDATE match_appeals
            SET status = @status,
                decided_by = @adminId,
                decided_at = UTC_TIMESTAMP(3),
                rejection_reason = @reason
            WHERE id = @id;
            """, connection);
        command.Parameters.AddWithValue("@id", appealId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@adminId", adminId);
        command.Parameters.AddWithValue(
            "@reason", (object?)rejectionReason ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    /* A match row standing in for the one Verify creates. Only the columns the edit-warning SQL reads
       (is_active and status) plus the NOT NULL columns matter here. */
    private async Task InsertMatchAsync(
        Guid lostId,
        Guid foundId,
        Guid lostOwner,
        Guid finderId,
        Guid claimantId,
        string claimantRole,
        string status,
        bool isActive)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO matches (
                id, lost_item_id, found_item_id,
                lost_reporter_id, finder_id, claimant_id, claimant_role,
                status, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, is_active,
                created_at, updated_at
            )
            VALUES (
                @id, @lostId, @foundId,
                @lostOwner, @finderId, @claimantId, @claimantRole,
                @status, 45.00, 'text-v1',
                JSON_OBJECT('id', @lostId), JSON_OBJECT('id', @foundId), @isActive,
                UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@lostId", lostId);
        command.Parameters.AddWithValue("@foundId", foundId);
        command.Parameters.AddWithValue("@lostOwner", lostOwner);
        command.Parameters.AddWithValue("@finderId", finderId);
        command.Parameters.AddWithValue("@claimantId", claimantId);
        command.Parameters.AddWithValue("@claimantRole", claimantRole);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@isActive", isActive);

        await command.ExecuteNonQueryAsync();
    }

    // ---- Scenario 4: the pair meets the threshold ---------------------------------------------

    /* Scenario 4 / DoD: "When an appeal is sent, the request returns 422 with 'This pair can be
       claimed directly.'" Nothing is written, so the pair stays claimable by the ordinary flow. */
    [Fact]
    public async Task SendAsync_PairScoresSixtyPercentOrMore_Returns422AndSavesNoAppeal()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedAboveThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, version, null),
                lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("This pair can be claimed directly.", exception.Message);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // ---- Input checks that precede scoring ---------------------------------------------------

    // DoD: the appeal is only offered from a real preview, so a request without one is refused.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_NoPreviewVersion_Returns400AndSavesNoAppeal(string previewVersion)
    {
        var handler = new FakeItemServiceHandler();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();

        var service = BuildService(handler, out _);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, previewVersion, null),
                Guid.NewGuid(), "reporter@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // DoD: "an optional note to the admin of up to 300 characters".
    [Fact]
    public async Task SendAsync_NoteLongerThan300Characters_Returns400AndSavesNoAppeal()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, version, new string('a', 301)),
                lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("The note can be at most 300 characters.", exception.Message);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // 300 characters is the documented maximum, so it must be accepted, and it is stored trimmed.
    [Fact]
    public async Task SendAsync_NoteOfExactly300Characters_IsAcceptedAndStoredTrimmed()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        var note = $"  {new string('n', 300)}  ";

        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, note),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.Equal(300, appeal.Note!.Length);
        Assert.Equal(1, await CountAppealsAsync(lostId, foundId));
    }

    // A stale preview must not be accepted: the pair is re-scored and the caller is told to preview again.
    [Fact]
    public async Task SendAsync_ReportsChangedSincePreview_Returns409AndSavesNoAppeal()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out _);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, "a-stale-preview-version", null),
                lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("The report information changed. Preview it again.", exception.Message);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // DoD: the appeal stores "the user's contact details", so a token without them cannot be saved.
    [Theory]
    [InlineData(null, "+94770000001")]
    [InlineData("", "+94770000001")]
    [InlineData("reporter@example.com", null)]
    [InlineData("reporter@example.com", "   ")]
    public async Task SendAsync_TokenWithoutContactDetails_Returns409AndSavesNoAppeal(
        string? email,
        string? phone)
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, version, null),
                lostOwner, email, phone, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Contains("contact details", exception.Message);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // ---- Scenario 3: the pair has already been appealed ---------------------------------------

    /* Scenario 3 / DoD: "A pair can be appealed only once, by either of its users ... a second request
       returns 409." The second attempt must leave the first appeal untouched, including its note. */
    [Fact]
    public async Task SendAsync_PairAlreadyAppealed_Returns409AndKeepsTheFirstAppealOnly()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        var first = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, "First appeal note."),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, version, "Second appeal note."),
                lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Contains("already been submitted", exception.Message);
        Assert.Equal(1, await CountAppealsAsync(lostId, foundId));

        var mine = await service.GetMineAsync(lostOwner, 1, CancellationToken.None);
        Assert.Equal("First appeal note.", Assert.Single(mine).Note);
        Assert.Equal(first.Id, mine[0].Id);
    }

    /* Scenario 3: the "only once" rule belongs to the pair, not to the person. The other user, who
       never sent the appeal, is refused too and learns nothing about the outcome. */
    [Fact]
    public async Task SendAsync_OtherUserAppealsTheSamePair_Returns409()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, version, null),
                finderId, "finder@example.com", "+94770000002", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(1, await CountAppealsAsync(lostId, foundId));
    }

    // ---- Scenario 5: the same checks as a claim -------------------------------------------------

    // Scenario 5: "does not own either report" is refused exactly as a claim would be.
    [Fact]
    public async Task SendAsync_CallerOwnsNeitherReport_Returns403AndSavesNoAppeal()
    {
        var handler = new FakeItemServiceHandler();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, Guid.NewGuid(), foundId, Guid.NewGuid());

        var service = BuildService(handler, out _);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, "v1", null),
                Guid.NewGuid(), "stranger@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // Scenario 5: "owns both reports" is refused exactly as a claim would be.
    [Fact]
    public async Task SendAsync_CallerOwnsBothReports_Returns400AndSavesNoAppeal()
    {
        var handler = new FakeItemServiceHandler();
        var owner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, owner, foundId, owner);

        var service = BuildService(handler, out _);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, "v1", null),
                owner, "owner@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // Scenario 5: "the report is no longer active" is refused exactly as a claim would be.
    [Theory]
    [InlineData("RESOLVED")]
    [InlineData("DELETED")]
    public async Task SendAsync_ReportNoLongerActive_Returns409AndSavesNoAppeal(string status)
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid(), foundStatus: status);

        var service = BuildService(handler, out _);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, "v1", null),
                lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // Scenario 5: "the pair already has a match" is refused exactly as a claim would be.
    [Fact]
    public async Task SendAsync_PairAlreadyHasMatch_Returns409AndSavesNoAppeal()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedAboveThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var claims = new ClaimService(
            BuildItemClient(handler),
            new ClaimRepository(_fixture.Connections, new BlobUrlPhotoKeyGenerator()));

        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        await claims.SubmitAsync(
            new SubmitClaimRequest(lostId, foundId, version),
            lostOwner, CancellationToken.None,
            "reporter@example.com", "+94770000001");

        var service = new AppealService(claims, BuildItemClient(handler), BuildAppealRepository());

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.SendAsync(
                new SendAppealRequest(lostId, foundId, version, null),
                lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(0, await CountAppealsAsync(lostId, foundId));
    }

    // ---- Scenario 2: the user sends an appeal ---------------------------------------------------

    /* Scenario 2 / DoD: the appeal is saved as Pending carrying both reports, the score, the user's
       role and their contact details. This is the single most important assertion in the story: it is
       the whole contract an admin later reviews. */
    [Fact]
    public async Task SendAsync_BelowThresholdAsLostReporter_SavesPendingAppealWithEverythingTheAdminNeeds()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, "The hidden information was worded differently."),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.Equal(AppealStatus.Pending, appeal.Status);
        Assert.Equal("LOST", appeal.Role);
        Assert.Equal(lostId, appeal.Lost.Id);
        Assert.Equal(foundId, appeal.Found.Id);
        Assert.True(appeal.Score < ClaimService.Threshold);
        Assert.Equal("The hidden information was worded differently.", appeal.Note);
        Assert.Null(appeal.DecidedAt);
        Assert.Null(appeal.RejectionReason);

        // The contact details and both report owners are persisted too, even though My appeals never
        // returns them to the user.
        var stored = await BuildAppealRepository().GetAsync(appeal.Id, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("reporter@example.com", stored!.AppellantEmail);
        Assert.Equal("+94770000001", stored.AppellantPhone);
        Assert.Equal(lostOwner, stored.LostReporterId);
        Assert.Equal(finderId, stored.FinderId);
        Assert.Equal(lostOwner, stored.AppellantId);
        Assert.Equal("LOST", stored.AppellantRole);
    }

    // The finder appealing their own report is stored with the mirrored role, which is what decides
    // whose side Verify confirms.
    [Fact]
    public async Task SendAsync_BelowThresholdAsFinder_SavesPendingAppealWithFinderRole()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, finderId);

        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            finderId, "finder@example.com", "+94770000002", CancellationToken.None);

        Assert.Equal("FOUND", appeal.Role);
        Assert.Equal(finderId, (await BuildAppealRepository().GetAsync(appeal.Id, CancellationToken.None))!.AppellantId);
    }

    /* Scenario 2 / DoD: "No match is created." The whole point of an appeal is that it asks an admin to
       make the match later, so nothing may appear in the matches table yet. */
    [Fact]
    public async Task SendAsync_SuccessfulAppeal_CreatesNoMatch()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.Equal(0, await CountMatchesAsync(lostId, foundId));
    }

    // Scenario 2: the note is optional. Leaving it out must not fail the appeal.
    [Fact]
    public async Task SendAsync_WithoutNote_SavesTheAppealWithNoNote()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.Null(appeal.Note);
        Assert.Equal(1, await CountAppealsAsync(lostId, foundId));
    }

    // A whitespace-only note is treated as no note at all rather than saved as blank text.
    [Fact]
    public async Task SendAsync_WhitespaceOnlyNote_SavesTheAppealWithNoNote()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, "   "),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.Null(appeal.Note);
    }

    // ---- Scenarios 6 and 7: My appeals is private to its sender ---------------------------------

    /* Scenario 6: "the list is taken from the signed-in user's token ... so a user can only ever see
       the appeals they sent", newest first. */
    [Fact]
    public async Task GetMineAsync_ReturnsOnlyTheCallersOwnAppealsNewestFirst()
    {
        var handler = new FakeItemServiceHandler();
        var reporter = Guid.NewGuid();
        var otherReporter = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        var otherLostId = Guid.NewGuid();
        var otherFoundId = Guid.NewGuid();

        SeedBelowThresholdPair(handler, lostId, reporter, foundId, Guid.NewGuid());
        SeedBelowThresholdPair(handler, otherLostId, otherReporter, otherFoundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var repository = BuildAppealRepository();

        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, await PreviewAsync(claims, lostId, foundId, reporter), null),
            reporter, "reporter@example.com", "+94770000001", CancellationToken.None);

        await repository.CreateAsync(
            new NewAppeal(
                otherLostId, otherFoundId, otherReporter, Guid.NewGuid(), otherReporter,
                "LOST", "other@example.com", "+94770000009", 45m,
                new ScoreBreakdown(45m, 30m, 15m, 0m, 0m),
                new ClaimItemView(otherLostId, "LOST", "L", "C", "D", "2026-09-01", "Malabe"),
                new ClaimItemView(otherFoundId, "FOUND", "F", "C", "D", "2026-09-01", "Malabe"),
                null),
            CancellationToken.None);

        var mine = await service.GetMineAsync(reporter, 1, CancellationToken.None);

        var appeal = Assert.Single(mine);
        Assert.Equal(lostId, appeal.Lost.Id);
        Assert.Equal(1, await CountAppealsAsync(otherLostId, otherFoundId));
    }

    // Scenario 6: "newest first".
    [Fact]
    public async Task GetMineAsync_ReturnsAppealsNewestFirst()
    {
        var handler = new FakeItemServiceHandler();
        var reporter = Guid.NewGuid();
        var service = BuildService(handler, out _);
        var repository = BuildAppealRepository();

        var older = await SeedAppealAsync(repository, reporter, reporter, "Older appeal");
        var newer = await SeedAppealAsync(repository, reporter, reporter, "Newer appeal");

        // Pin the two timestamps an hour apart so the assertion tests the ORDER BY, not the insert order.
        await SetCreatedAtAsync(older.Id, DateTime.UtcNow.AddHours(-1));
        await SetCreatedAtAsync(newer.Id, DateTime.UtcNow);

        var mine = await service.GetMineAsync(reporter, 1, CancellationToken.None);

        Assert.Equal(2, mine.Count);
        Assert.Equal("Newer appeal", mine[0].Note);
        Assert.Equal(older.Id, mine[1].Id);
    }

    /* Scenario 6 / DoD: "the admin who decided it is not shown." MyAppealView carries no decided-by
       field, so this is asserted at the wire level rather than by reflection on the record. */
    [Fact]
    public async Task GetMineAsync_NeverExposesTheDecidingAdmin()
    {
        var handler = new FakeItemServiceHandler();
        var reporter = Guid.NewGuid();
        var service = BuildService(handler, out _);
        var repository = BuildAppealRepository();

        var appeal = await SeedAppealAsync(repository, reporter, reporter, "Note");
        await DecideAppealAsync(appeal.Id, AppealStatus.Rejected, Guid.NewGuid(), "Not convincing.");

        var mine = await service.GetMineAsync(reporter, 1, CancellationToken.None);

        var json = JsonSerializer.Serialize(mine);

        Assert.DoesNotContain("decidedBy", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", json, StringComparison.OrdinalIgnoreCase);
    }

    /* Scenario 6 / DoD: once decided, the decision date appears, and a Rejected appeal carries the
       admin's reason as plain text for the user who sent it. */
    [Fact]
    public async Task GetMineAsync_RejectedAppeal_ReturnsRejectionReasonAndDecisionDate()
    {
        var handler = new FakeItemServiceHandler();
        var reporter = Guid.NewGuid();
        var service = BuildService(handler, out _);
        var repository = BuildAppealRepository();

        var appeal = await SeedAppealAsync(repository, reporter, reporter, "Please look again");
        await DecideAppealAsync(appeal.Id, AppealStatus.Rejected, Guid.NewGuid(), "The reports are unrelated.");

        var mine = await service.GetMineAsync(reporter, 1, CancellationToken.None);

        var view = Assert.Single(mine);
        Assert.Equal(AppealStatus.Rejected, view.Status);
        Assert.Equal("The reports are unrelated.", view.RejectionReason);
        Assert.NotNull(view.DecidedAt);
    }

    // A Verified appeal shows the same decision date and no reason.
    [Fact]
    public async Task GetMineAsync_VerifiedAppeal_ReturnsDecisionDateAndNoRejectionReason()
    {
        var handler = new FakeItemServiceHandler();
        var reporter = Guid.NewGuid();
        var service = BuildService(handler, out _);
        var repository = BuildAppealRepository();

        var appeal = await SeedAppealAsync(repository, reporter, reporter, null);
        await DecideAppealAsync(appeal.Id, AppealStatus.Verified, Guid.NewGuid());

        var view = Assert.Single(await service.GetMineAsync(reporter, 1, CancellationToken.None));

        Assert.Equal(AppealStatus.Verified, view.Status);
        Assert.NotNull(view.DecidedAt);
        Assert.Null(view.RejectionReason);
    }

    // Scenario 6: the tab is paged 20 at a time.
    [Fact]
    public async Task GetMineAsync_SecondPage_ReturnsTheNextTwentyAppeals()
    {
        var handler = new FakeItemServiceHandler();
        var reporter = Guid.NewGuid();
        var service = BuildService(handler, out _);
        var repository = BuildAppealRepository();

        for (var index = 0; index < AppealRepository.PageSize + 3; index++)
        {
            await SeedAppealAsync(repository, reporter, reporter, $"Appeal {index}");
        }

        var firstPage = await service.GetMineAsync(reporter, 1, CancellationToken.None);
        var secondPage = await service.GetMineAsync(reporter, 2, CancellationToken.None);

        Assert.Equal(AppealRepository.PageSize, firstPage.Count);
        Assert.Equal(3, secondPage.Count);
        Assert.Empty(firstPage.Select(appeal => appeal.Id).Intersect(secondPage.Select(appeal => appeal.Id)));
    }

    private async Task<AppealRecord> SeedAppealAsync(
        AppealRepository repository,
        Guid appellantId,
        Guid otherPartyId,
        string? note)
    {
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();

        return await repository.CreateAsync(
            new NewAppeal(
                lostId, foundId, appellantId, otherPartyId, appellantId,
                "LOST", "reporter@example.com", "+94770000001", 45m,
                new ScoreBreakdown(45m, 30m, 15m, 0m, 0m),
                new ClaimItemView(lostId, "LOST", "Green kettle", "Kitchen", "A green kettle.", "2026-09-01", "Malabe"),
                new ClaimItemView(foundId, "FOUND", "Orange bicycle", "Vehicles", "An orange bicycle.", "2026-09-01", "Malabe"),
                note),
            CancellationToken.None);
    }

    // ---- Scenario 3: the Appeal button state endpoint ------------------------------------------

    // Scenario 3: the button is greyed out once either user has appealed the pair.
    [Fact]
    public async Task PairHasAppealAsync_ReportsTrueForEitherUserOfAnAppealedPair()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);

        Assert.False(await service.PairHasAppealAsync(lostId, foundId, lostOwner, CancellationToken.None));

        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.True(await service.PairHasAppealAsync(lostId, foundId, lostOwner, CancellationToken.None));
        Assert.True(await service.PairHasAppealAsync(lostId, foundId, finderId, CancellationToken.None));
    }

    // A pair nobody appealed leaves the Appeal button usable.
    [Fact]
    public async Task PairHasAppealAsync_ReportsFalseForAnUnappealedPair()
    {
        var handler = new FakeItemServiceHandler();
        var service = BuildService(handler, out _);

        Assert.False(await service.PairHasAppealAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    // ---- Scenario 8: the edit warning ----------------------------------------------------------

    // Scenario 8 / DoD: whoever submitted a Pending appeal is warned when opening their own report.
    [Fact]
    public async Task HasEditWarningAsync_PendingAppeal_WarnsTheSubmittingOwner()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.True(await service.HasEditWarningAsync("LOST", lostId, lostOwner, CancellationToken.None));
    }

    /* Scenario 8 / DoD: "Both owners of the pair receive this warning, so the other user is warned
       before editing too." The other owner never sent the appeal, yet is warned about their own
       report, and learns nothing about who appealed or what the outcome is. */
    [Fact]
    public async Task HasEditWarningAsync_PendingAppeal_WarnsTheOtherOwnerAboutTheirOwnReport()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.True(await service.HasEditWarningAsync("FOUND", foundId, finderId, CancellationToken.None));
    }

    /* The warning is scoped to a report the caller actually owns, so it cannot be used to probe a pair
       the caller has no part in. Scenario 7's privacy rule, applied to this endpoint. */
    [Fact]
    public async Task HasEditWarningAsync_ReportTheCallerDoesNotOwn_DoesNotWarn()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        // The finder asking about the lost reporter's report.
        Assert.False(await service.HasEditWarningAsync("LOST", lostId, finderId, CancellationToken.None));

        // The lost reporter asking about the finder's report.
        Assert.False(await service.HasEditWarningAsync("FOUND", foundId, lostOwner, CancellationToken.None));
    }

    /* Scenario 8 / DoD: "in a Verified appeal whose match is still waiting for a decision." Once the
       appeal is Verified and the match is on hold for the other user, both owners are still warned. */
    [Fact]
    public async Task HasEditWarningAsync_VerifiedAppealWithWaitingMatch_WarnsBothOwners()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var repository = BuildAppealRepository();
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        await DecideAppealAsync(appeal.Id, AppealStatus.Verified, Guid.NewGuid());
        await InsertMatchAsync(
            lostId, foundId, lostOwner, finderId, lostOwner, "LOST",
            "LOST_REPORTER_CONFIRMED", isActive: true);

        Assert.True(await service.HasEditWarningAsync("LOST", lostId, lostOwner, CancellationToken.None));
        Assert.True(await service.HasEditWarningAsync("FOUND", foundId, finderId, CancellationToken.None));
    }

    // The finder's side of the same waiting match warns them too.
    [Fact]
    public async Task HasEditWarningAsync_VerifiedAppealWithWaitingMatch_WarnsTheFinderOnTheirOwnReport()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, finderId);
        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            finderId, "finder@example.com", "+94770000002", CancellationToken.None);

        await DecideAppealAsync(appeal.Id, AppealStatus.Verified, Guid.NewGuid());
        await InsertMatchAsync(
            lostId, foundId, lostOwner, finderId, finderId, "FOUND",
            "FINDER_CONFIRMED", isActive: true);

        Assert.True(await service.HasEditWarningAsync("FOUND", foundId, finderId, CancellationToken.None));
    }

    /* Scenario 8 / DoD: once the match has been cancelled the pair is settled, so the warning goes away
       and editing behaves normally again. */
    [Fact]
    public async Task HasEditWarningAsync_VerifiedAppealWithCancelledMatch_DoesNotWarn()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        await DecideAppealAsync(appeal.Id, AppealStatus.Verified, Guid.NewGuid());
        await InsertMatchAsync(
            lostId, foundId, lostOwner, finderId, lostOwner, "LOST",
            "LOST_REPORTER_CONFIRMED", isActive: false);

        Assert.False(await service.HasEditWarningAsync("LOST", lostId, lostOwner, CancellationToken.None));
        Assert.False(await service.HasEditWarningAsync("FOUND", foundId, finderId, CancellationToken.None));
    }

    // Scenario 8 / DoD: "Reports that are ... only in a Rejected appeal, continue to open and save exactly as they do currently, without a popup."
    [Fact]
    public async Task HasEditWarningAsync_RejectedAppealOnly_DoesNotWarnEitherOwner()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, finderId);

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        var appeal = await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        await DecideAppealAsync(appeal.Id, AppealStatus.Rejected, Guid.NewGuid(), "Unrelated reports.");

        Assert.False(await service.HasEditWarningAsync("LOST", lostId, lostOwner, CancellationToken.None));
        Assert.False(await service.HasEditWarningAsync("FOUND", foundId, finderId, CancellationToken.None));
    }

    // Scenario 8: a report in no appeal at all is never warned about.
    [Fact]
    public async Task HasEditWarningAsync_ReportInNoAppeal_DoesNotWarn()
    {
        var handler = new FakeItemServiceHandler();
        var service = BuildService(handler, out _);

        Assert.False(await service.HasEditWarningAsync(
            "LOST", Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    // A third party who owns neither report is never warned, so the warning leaks nothing about the pair.
    [Fact]
    public async Task HasEditWarningAsync_ReportOwnedByAnotherUser_DoesNotWarn()
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.False(await service.HasEditWarningAsync(
            "LOST", lostId, Guid.NewGuid(), CancellationToken.None));
    }

    // The type segment is constrained to lost/found, and the caller's bad input is refused up front.
    [Theory]
    [InlineData("stolen")]
    [InlineData("LOST_ITEM")]
    [InlineData("")]
    public async Task HasEditWarningAsync_TypeThatIsNeitherLostNorFound_Returns400(string type)
    {
        var handler = new FakeItemServiceHandler();
        var service = BuildService(handler, out _);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.HasEditWarningAsync(type, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("Type must be lost or found.", exception.Message);
    }

    // The route accepts the type in any casing, exactly like the type segment on the admin item routes.
    [Theory]
    [InlineData("lost")]
    [InlineData("Lost")]
    [InlineData("LOST")]
    public async Task HasEditWarningAsync_TypeSegment_IsCaseInsensitive(string type)
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();
        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, Guid.NewGuid());

        var service = BuildService(handler, out var claims);
        var version = await PreviewAsync(claims, lostId, foundId, lostOwner);
        await service.SendAsync(
            new SendAppealRequest(lostId, foundId, version, null),
            lostOwner, "reporter@example.com", "+94770000001", CancellationToken.None);

        Assert.True(await service.HasEditWarningAsync(type, lostId, lostOwner, CancellationToken.None));
    }
}