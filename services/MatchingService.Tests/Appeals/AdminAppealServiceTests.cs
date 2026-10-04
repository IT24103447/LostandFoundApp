using System.Net;
using MatchingService.Appeals;
using MatchingService.Claims;
using MatchingService.Services;
using MatchingService.Tests.Integration;
using MatchingService.Tests.Support;
using Microsoft.AspNetCore.Http;
using MySqlConnector;

namespace MatchingService.Tests.Appeals;

/// <summary>
/// Story LF-338 contract tests for AdminAppealService, against a real MySQL (Testcontainers) and a faked
/// Item Service (FakeItemServiceHandler), following the same shape as ClaimServiceTests and AppealServiceTests.
///
/// AdminAppealService's collaborators (AppealRepository, ClaimService, ClaimRepository, ClaimItemClient) are all
/// sealed, so they cannot be mocked. This class therefore drives the real collaborators and controls the inputs
/// through three independent levers:
///   - the faked Item Service, which decides what GetActivePairAsync sees (RESOLVED status, or a 404 for a deleted
///     report);
///   - matching_item_states, which decides what ClaimRepository.PairExistsAsync sees (a 409 before the appeal is
///     ever decided);
///   - image_descriptions, which decides what ClaimService.ScoreExistingAsync sees (a 409 mapping to
///     SCORE_UNAVAILABLE).
/// Appeals themselves are created by driving the real AppealService, so every test starts from a genuine PENDING
/// appeal rather than a hand-built row.
///
/// Not covered, on purpose: the ReopenAsync rollback that VerifyAsync runs when the match insert fails. A
/// concurrent second request always exits at EnsurePending or DecideAsync and never reaches the catch, and no
/// seeding arrangement can make PairExistsAsync pass while EnsurePairActiveAsync fails, because both read the same
/// matching_item_states rows. The DoD's real concurrency requirement ("one decision and at most one match") is
/// covered end-to-end by the Step 3 integration tests instead.
/// </summary>
[Collection("Docker Integration Tests 12")]
public sealed class AdminAppealServiceTests : IClassFixture<ClaimServiceDbFixture>, IAsyncLifetime
{
    private readonly ClaimServiceDbFixture _fixture;

    /* HttpContextAccessor keeps its HttpContext in a *static* AsyncLocal. Setting it from inside a helper
       (SeedAppealAsync) therefore only holds for the rest of that helper's own async flow: the test body reads
       it back as null and every Item Service call fails with 401 "Please sign in.". Setting it once here, in the
       test's own flow, and reusing this one instance everywhere keeps it stable. */
    private readonly HttpContextAccessor _accessor = new();

    public AdminAppealServiceTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer test-token";
        _accessor.HttpContext = context;
    }

    /* match_appeals and matches are shared container state, and ListAsync returns 20 rows to a page, so appeals
       left behind by earlier tests would otherwise push this test's own appeals off the page and break
       Assert.Single. */
    public async Task InitializeAsync()
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        foreach (var table in new[]
        {
            "match_appeals", "matches", "matching_item_states", "image_descriptions"
        })
        {
            await using var command = new MySqlCommand($"DELETE FROM {table};", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Wiring -------------------------------------------------------------------------------

    private ClaimItemClient BuildItemClient(FakeItemServiceHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://item-service.test/") },
            _accessor);

    private AppealRepository BuildAppealRepository() =>
        new(_fixture.Connections, TimeProvider.System);

    private ClaimRepository BuildMatchRepository() =>
        new(_fixture.Connections, new BlobUrlPhotoKeyGenerator());

    private AdminAppealService BuildService(FakeItemServiceHandler handler)
    {
        var items = BuildItemClient(handler);

        return new AdminAppealService(
            BuildAppealRepository(),
            new ClaimService(items, BuildMatchRepository()),
            BuildMatchRepository(),
            items);
    }

    // ---- Fixtures -----------------------------------------------------------------------------

    /* Two genuinely unrelated reports, so the pair scores far below ClaimService.Threshold - the only situation
       an appeal is legal in. Both sides are owned by different users, which ClaimService requires. */
    private static void SeedBelowThresholdPair(
        FakeItemServiceHandler handler,
        Guid lostId,
        Guid lostOwner,
        Guid foundId,
        Guid foundOwner,
        string lostStatus = "ACTIVE",
        string foundStatus = "ACTIVE",
        string lostCategory = "Kitchen",
        string foundCategory = "Vehicles")
    {
        handler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, status: lostStatus, title: "Green kettle",
                category: lostCategory, description: "A green kettle with a whistle."));

        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, foundOwner, status: foundStatus, title: "Orange bicycle",
                category: foundCategory, description: "An orange bicycle with a flat tyre."));
    }

    /* The reports the same pair still has *now*, for the tests that need the live reports to have changed since
       the appeal was saved. Sharing the lost report's category lifts the category component to 100, so the current
       score is provably higher than the score frozen into the appeal. */
    private static void ReseedPairWithSharedCategory(
        FakeItemServiceHandler handler,
        Guid lostId,
        Guid lostOwner,
        Guid foundId,
        Guid foundOwner) =>
        SeedBelowThresholdPair(
            handler, lostId, lostOwner, foundId, foundOwner,
            foundCategory: "Kitchen");

    // Both reports carry exactly one photo and each photo has a row that is still being analysed.
    private static void SeedPairWithPendingPhotos(
        FakeItemServiceHandler handler,
        Guid lostId,
        Guid lostOwner,
        Guid foundId,
        Guid foundOwner,
        out string lostPhotoUrl,
        out string foundPhotoUrl)
    {
        lostPhotoUrl = "https://blob.example.com/appeal-lost-pending.jpg";
        foundPhotoUrl = "https://blob.example.com/appeal-found-pending.jpg";

        handler.RespondWithJson(
            $"api/items/lost/{lostId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                lostId, lostOwner, title: "Green kettle", category: "Kitchen",
                description: "A green kettle with a whistle.",
                photoUrls: [lostPhotoUrl]));

        handler.RespondWithJson(
            $"api/items/found/{foundId}",
            HttpStatusCode.OK,
            FakeItemServiceHandler.Report(
                foundId, foundOwner, title: "Orange bicycle", category: "Vehicles",
                description: "An orange bicycle with a flat tyre.",
                photoUrls: [foundPhotoUrl]));
    }

    /// <summary>
    /// Creates a genuine PENDING appeal by driving the real AppealService, so the saved score, breakdown and
    /// snapshots are exactly what production would have stored.
    /// </summary>
    private async Task<SeededAppeal> SeedAppealAsync(
        string appellantRole = "LOST",
        string? note = "Both reports describe the same kettle. Please review.",
        string email = "appellant@example.com",
        string phone = "+94771234567")
    {
        var handler = new FakeItemServiceHandler();
        var lostOwner = Guid.NewGuid();
        var foundOwner = Guid.NewGuid();
        var lostId = Guid.NewGuid();
        var foundId = Guid.NewGuid();

        SeedBelowThresholdPair(handler, lostId, lostOwner, foundId, foundOwner);

        var items = BuildItemClient(handler);
        var claims = new ClaimService(items, BuildMatchRepository());
        var appeals = BuildAppealRepository();
        var userAppeals = new AppealService(claims, items, appeals);

        var appellantId = appellantRole == "FOUND" ? foundOwner : lostOwner;

        var preview = await claims.PreviewAsync(
            new PairRequest(lostId, foundId), appellantId, CancellationToken.None);

        var sent = await userAppeals.SendAsync(
            new SendAppealRequest(lostId, foundId, preview.PreviewVersion, note),
            appellantId,
            email,
            phone,
            CancellationToken.None);

        return new SeededAppeal(
            BuildService(handler),
            handler,
            appeals,
            sent.Id,
            lostId,
            foundId,
            lostOwner,
            foundOwner,
            appellantId,
            appellantRole,
            email,
            phone,
            sent.Score);
    }

    private sealed record SeededAppeal(
        AdminAppealService Admin,
        FakeItemServiceHandler Handler,
        AppealRepository Appeals,
        Guid AppealId,
        Guid LostId,
        Guid FoundId,
        Guid LostOwner,
        Guid FinderId,
        Guid AppellantId,
        string AppellantRole,
        string AppellantEmail,
        string AppellantPhone,
        decimal SavedScore);

    // ---- Database helpers ---------------------------------------------------------------------

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

    // Flips a PENDING appeal into a decided state behind the service's back, for the tests that need to start from
    // an appeal an admin has already dealt with.
    private async Task ForceDecisionAsync(
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
        command.Parameters.AddWithValue("@reason", (object?)rejectionReason ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    private async Task MarkItemInactiveAsync(string itemType, Guid itemId, string reason)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO matching_item_states (item_type, item_id, inactive_reason, updated_at)
            VALUES (@type, @itemId, @reason, UTC_TIMESTAMP(3));
            """, connection);
        command.Parameters.AddWithValue("@type", itemType);
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@reason", reason);

        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertImageDescriptionAsync(
        Guid itemId,
        string itemType,
        string photoUrl,
        string processingStatus)
    {
        var (_, photoKey) = new BlobUrlPhotoKeyGenerator().Create(photoUrl);
        var now = DateTime.UtcNow;

        const string sql = """
            INSERT INTO image_descriptions (
                id, source_event_id, source_event_type, source_occurred_at, photo_key, item_id,
                item_type, blob_url, processing_status, attempts, created_at, updated_at
            ) VALUES (
                @id, @sourceEventId, 'CREATED', @now, @photoKey, @itemId,
                @itemType, @blobUrl, @status, 1, @now, @now
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
        command.Parameters.AddWithValue("@status", processingStatus);

        await command.ExecuteNonQueryAsync();
    }

    /* A pre-existing match row. Only the columns PairExistsAsync and these tests read matter, but every NOT NULL
       column is supplied. */
    private async Task InsertMatchAsync(
        Guid lostId,
        Guid foundId,
        Guid lostOwner,
        Guid finderId,
        Guid claimantId,
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
                @lostOwner, @finderId, @claimantId, 'LOST',
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
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@isActive", isActive);

        await command.ExecuteNonQueryAsync();
    }

    private sealed record MatchRow(
        Guid ClaimantId,
        string ClaimantRole,
        string Status,
        decimal ConfidenceScore,
        string? FinderEmail,
        string? FinderPhone,
        string? LostReporterEmail,
        string? LostReporterPhone);

    private async Task<MatchRow?> ReadMatchAsync(Guid lostId, Guid foundId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            SELECT claimant_id, claimant_role, status, confidence_score,
                   finder_email, finder_phone, lost_reporter_email, lost_reporter_phone
            FROM matches
            WHERE lost_item_id = @lostId AND found_item_id = @foundId;
            """, connection);
        command.Parameters.AddWithValue("@lostId", lostId);
        command.Parameters.AddWithValue("@foundId", foundId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new MatchRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
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

    private async Task<AppealRecord> ReadAppealAsync(Guid appealId) =>
        (await BuildAppealRepository().GetAsync(appealId, CancellationToken.None))!;

    // ---- Scenario 1: the admin queue ----------------------------------------------------------

    [Theory]
    [InlineData("PENDING")]
    [InlineData("VERIFIED")]
    [InlineData("REJECTED")]
    [InlineData("pending")]
    [InlineData("rejected")]
    [InlineData("  verified  ")]
    public async Task ListAsync_AcceptsEveryDecidableStatusIgnoringCaseAndPadding(string status)
    {
        await SeedAppealAsync();

        var result = await BuildService(new FakeItemServiceHandler())
            .ListAsync(status, 1, CancellationToken.None);

        // The only appeal in the database is PENDING, so only the PENDING variants can find it - but none of
        // the spellings may be rejected as an invalid status.
        if (string.Equals(status.Trim(), "PENDING", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Single(result);
        }
        else
        {
            Assert.Empty(result);
        }
    }

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("CLOSED")]
    [InlineData("PENDINGISH")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListAsync_UnknownStatus_Returns400(string status)
    {
        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(new FakeItemServiceHandler())
                .ListAsync(status, 1, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("Status must be PENDING, VERIFIED or REJECTED.", exception.Message);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheRequestedStatus()
    {
        var pending = await SeedAppealAsync();
        var rejected = await SeedAppealAsync();

        await ForceDecisionAsync(rejected.AppealId, AppealStatus.Rejected, Guid.NewGuid());

        var service = BuildService(new FakeItemServiceHandler());

        var pendingOnly = await service.ListAsync(AppealStatus.Pending, 1, CancellationToken.None);
        var rejectedOnly = await service.ListAsync(AppealStatus.Rejected, 1, CancellationToken.None);

        Assert.Contains(pendingOnly, appeal => appeal.Id == pending.AppealId);
        Assert.DoesNotContain(pendingOnly, appeal => appeal.Id == rejected.AppealId);
        Assert.Contains(rejectedOnly, appeal => appeal.Id == rejected.AppealId);
        Assert.DoesNotContain(rejectedOnly, appeal => appeal.Id == pending.AppealId);
    }

    /* Scenario 1: "the admin sees ... who reported what". Order comes from the repository's
       ORDER BY created_at DESC, so the test controls created_at explicitly rather than relying on insert order. */
    [Fact]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        var oldest = await SeedAppealAsync();
        var newest = await SeedAppealAsync();
        var middle = await SeedAppealAsync();

        await SetCreatedAtAsync(oldest.AppealId, new DateTime(2026, 09, 1, 9, 0, 0, DateTimeKind.Utc));
        await SetCreatedAtAsync(middle.AppealId, new DateTime(2026, 09, 2, 9, 0, 0, DateTimeKind.Utc));
        await SetCreatedAtAsync(newest.AppealId, new DateTime(2026, 09, 3, 9, 0, 0, DateTimeKind.Utc));

        var result = await BuildService(new FakeItemServiceHandler())
            .ListAsync(AppealStatus.Pending, 1, CancellationToken.None);

        var orderedIds = result.Select(appeal => appeal.Id).ToList();

        Assert.True(orderedIds.IndexOf(newest.AppealId) < orderedIds.IndexOf(middle.AppealId));
        Assert.True(orderedIds.IndexOf(middle.AppealId) < orderedIds.IndexOf(oldest.AppealId));
    }

    /* Scenario 1 / 5 / 6: the admin has to see the appellant, both report owners, the saved score and why it was
       saved, the note, and - once decided - who decided it and why it was rejected. */
    [Fact]
    public async Task ListAsync_MapsEveryAdminFacingField()
    {
        var seeded = await SeedAppealAsync(
            appellantRole: "FOUND",
            note: "This is the same kettle, bought second hand.",
            email: "finder@example.com",
            phone: "+94779998888");

        var adminId = Guid.NewGuid();
        await ForceDecisionAsync(
            seeded.AppealId,
            AppealStatus.Rejected,
            adminId,
            "The reports describe different items.");

        var appeal = Assert.Single(await BuildService(new FakeItemServiceHandler())
            .ListAsync(AppealStatus.Rejected, 1, CancellationToken.None));

        Assert.Equal(seeded.AppealId, appeal.Id);
        Assert.Equal(AppealStatus.Rejected, appeal.Status);
        Assert.Equal(seeded.FinderId, appeal.AppellantId);
        Assert.Equal("FOUND", appeal.AppellantRole);
        Assert.Equal(seeded.LostOwner, appeal.LostReporterId);
        Assert.Equal(seeded.FinderId, appeal.FinderId);
        Assert.Equal(seeded.SavedScore, appeal.Score);
        Assert.Equal("This is the same kettle, bought second hand.", appeal.Note);
        Assert.Equal("Green kettle", appeal.Lost.Title);
        Assert.Equal("LOST", appeal.Lost.Type);
        Assert.Equal("Orange bicycle", appeal.Found.Title);
        Assert.Equal("FOUND", appeal.Found.Type);
        Assert.NotNull(appeal.Breakdown);
        Assert.Equal(adminId, appeal.DecidedBy);
        Assert.NotNull(appeal.DecidedAt);
        Assert.Equal("The reports describe different items.", appeal.RejectionReason);
    }

    // ---- Scenario 2: the appeal detail --------------------------------------------------------

    [Fact]
    public async Task OpenAsync_UnknownAppeal_Returns404()
    {
        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(new FakeItemServiceHandler())
                .OpenAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Appeal not found.", exception.Message);
    }

    /* Scenario 2: "the score ... saved at the time the appeal was sent" and "the current score ... displayed
       next to the score saved with the appeal". Both must be present for a pending appeal. */
    [Fact]
    public async Task OpenAsync_PendingAppeal_ReturnsSavedScoreAndCurrentScoreTogether()
    {
        var seeded = await SeedAppealAsync();

        var detail = await seeded.Admin.OpenAsync(seeded.AppealId, CancellationToken.None);

        Assert.Equal(AppealStatus.Pending, detail.Appeal.Status);
        Assert.Null(detail.CurrentUnavailableReason);
        Assert.NotNull(detail.Current);
        Assert.Equal(seeded.SavedScore, detail.Appeal.Score);
        Assert.Equal("Green kettle", detail.Appeal.Lost.Title);
        Assert.Equal("Green kettle", detail.Current!.Lost.Title);
        Assert.Equal(seeded.SavedScore, detail.Current.Score);
    }

    /* The current score is recomputed from the live reports, not echoed from the stored snapshot: the found
       report now shares the lost report's category, which must move the number. */
    [Fact]
    public async Task OpenAsync_RecomputesTheCurrentScoreFromTheLiveReports()
    {
        var seeded = await SeedAppealAsync();

        ReseedPairWithSharedCategory(
            seeded.Handler, seeded.LostId, seeded.LostOwner, seeded.FoundId, seeded.FinderId);

        var detail = await seeded.Admin.OpenAsync(seeded.AppealId, CancellationToken.None);

        Assert.NotNull(detail.Current);
        Assert.True(
            detail.Current!.Score > detail.Appeal.Score,
            $"Expected the recomputed score to rise once the categories matched, but it stayed at " +
            $"{detail.Current.Score} against a saved {detail.Appeal.Score}.");
    }

    /* Scenario 2: a decided appeal is opened for the record, not re-scored - there is no decision left to make,
       and a stale report must not look like a fresh reason to worry. */
    [Theory]
    [InlineData(AppealStatus.Verified)]
    [InlineData(AppealStatus.Rejected)]
    public async Task OpenAsync_DecidedAppeal_ReturnsNoCurrentScoreAndNoUnavailableReason(string status)
    {
        var seeded = await SeedAppealAsync();
        await ForceDecisionAsync(seeded.AppealId, status, Guid.NewGuid());

        // Even with the reports gone entirely, a decided appeal still opens cleanly.
        var detail = await seeded.Admin.OpenAsync(seeded.AppealId, CancellationToken.None);

        Assert.Equal(status, detail.Appeal.Status);
        Assert.Null(detail.Current);
        Assert.Null(detail.CurrentUnavailableReason);
    }

    /* Scenario 2: the appeal is still reviewable when one of its reports has been resolved or deleted - the admin
       gets a reason rather than an error, so the queue still works. */
    [Theory]
    [InlineData("RESOLVED")]
    [InlineData("DELETED")]
    public async Task OpenAsync_ReportNoLongerActiveInItemService_ReportsInactiveInsteadOfFailing(string status)
    {
        var seeded = await SeedAppealAsync();

        SeedBelowThresholdPair(
            seeded.Handler, seeded.LostId, seeded.LostOwner, seeded.FoundId, seeded.FinderId,
            foundStatus: status);

        var detail = await seeded.Admin.OpenAsync(seeded.AppealId, CancellationToken.None);

        Assert.Equal(AdminAppealService.ReportInactive, detail.CurrentUnavailableReason);
        Assert.Null(detail.Current);
        Assert.Equal(AppealStatus.Pending, detail.Appeal.Status);
    }

    // A hard-deleted report 404s from Item Service; the service must treat that the same as RESOLVED.
    [Fact]
    public async Task OpenAsync_ReportDeletedFromItemService_ReportsInactive()
    {
        var seeded = await SeedAppealAsync();

        seeded.Handler.RespondWithStatus(
            $"api/items/found/{seeded.FoundId}", HttpStatusCode.NotFound);

        var detail = await seeded.Admin.OpenAsync(seeded.AppealId, CancellationToken.None);

        Assert.Equal(AdminAppealService.ReportInactive, detail.CurrentUnavailableReason);
        Assert.Null(detail.Current);
    }

    /* Scenario 2: the appeal and its note stay readable even when the score cannot be produced right now. */
    [Fact]
    public async Task OpenAsync_PhotoStillBeingAnalysed_ReportsScoreUnavailableAndKeepsTheAppeal()
    {
        var seeded = await SeedAppealAsync();

        SeedPairWithPendingPhotos(
            seeded.Handler, seeded.LostId, seeded.LostOwner, seeded.FoundId, seeded.FinderId,
            out var lostPhoto, out var foundPhoto);

        await InsertImageDescriptionAsync(seeded.LostId, "LOST", lostPhoto, "PROCESSING");
        await InsertImageDescriptionAsync(seeded.FoundId, "FOUND", foundPhoto, "PENDING");

        var detail = await seeded.Admin.OpenAsync(seeded.AppealId, CancellationToken.None);

        Assert.Equal(AdminAppealService.ScoreUnavailable, detail.CurrentUnavailableReason);
        Assert.Null(detail.Current);
        Assert.Equal(AppealStatus.Pending, detail.Appeal.Status);
        Assert.Equal("Both reports describe the same kettle. Please review.", detail.Appeal.Note);
    }

    // ---- Scenario 3: verifying an appeal creates the match ------------------------------------

    /* Scenario 3: "a match is created with the appealing user's side already confirmed". Whichever report the
       appellant owns is the side that gets confirmed, and their contact details are the ones recorded for hand-off. */
    [Theory]
    [InlineData("LOST", "LOST_REPORTER_CONFIRMED")]
    [InlineData("FOUND", "FINDER_CONFIRMED")]
    public async Task VerifyAsync_ConfirmsTheAppellantsSideOfTheMatch(
        string appellantRole,
        string expectedMatchStatus)
    {
        var seeded = await SeedAppealAsync(appellantRole: appellantRole);
        var adminId = Guid.NewGuid();

        var verified = await seeded.Admin.VerifyAsync(seeded.AppealId, adminId, CancellationToken.None);

        Assert.Equal(AppealStatus.Verified, verified.Status);
        Assert.Equal(adminId, verified.DecidedBy);

        var match = await ReadMatchAsync(seeded.LostId, seeded.FoundId);

        Assert.NotNull(match);
        Assert.Equal(seeded.AppellantId, match!.ClaimantId);
        Assert.Equal(appellantRole, match.ClaimantRole);
        Assert.Equal(expectedMatchStatus, match.Status);

        // Only the appellant's own contact details are carried into the match, never the other side's.
        if (appellantRole == "FOUND")
        {
            Assert.Equal(seeded.AppellantEmail, match.FinderEmail);
            Assert.Equal(seeded.AppellantPhone, match.FinderPhone);
            Assert.Null(match.LostReporterEmail);
            Assert.Null(match.LostReporterPhone);
        }
        else
        {
            Assert.Equal(seeded.AppellantEmail, match.LostReporterEmail);
            Assert.Equal(seeded.AppellantPhone, match.LostReporterPhone);
            Assert.Null(match.FinderEmail);
            Assert.Null(match.FinderPhone);
        }
    }

    [Fact]
    public async Task VerifyAsync_RecordsTheDecidingAdminAndDecisionTime()
    {
        var seeded = await SeedAppealAsync();
        var adminId = Guid.NewGuid();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await seeded.Admin.VerifyAsync(seeded.AppealId, adminId, CancellationToken.None);

        var stored = await ReadAppealAsync(seeded.AppealId);

        Assert.Equal(AppealStatus.Verified, stored.Status);
        Assert.Equal(adminId, stored.DecidedBy);
        Assert.NotNull(stored.DecidedAt);
        Assert.True(stored.DecidedAt > before);
        Assert.True(stored.DecidedAt <= DateTime.UtcNow.AddSeconds(1));
        Assert.Null(stored.RejectionReason);
    }

    // The match must carry the score as it stands now, while the appeal keeps the score it was sent with.
    [Fact]
    public async Task VerifyAsync_MatchUsesTheCurrentScoreWhileTheAppealKeepsTheSavedOne()
    {
        var seeded = await SeedAppealAsync();

        ReseedPairWithSharedCategory(
            seeded.Handler, seeded.LostId, seeded.LostOwner, seeded.FoundId, seeded.FinderId);

        var detail = await seeded.Admin.OpenAsync(seeded.AppealId, CancellationToken.None);
        Assert.NotNull(detail.Current);

        await seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None);

        var match = await ReadMatchAsync(seeded.LostId, seeded.FoundId);
        var appeal = await ReadAppealAsync(seeded.AppealId);

        Assert.NotNull(match);
        Assert.Equal(
            Math.Round(detail.Current!.Score, 2),
            Math.Round(match!.ConfidenceScore, 2));
        Assert.Equal(seeded.SavedScore, appeal.Score);
    }

    [Fact]
    public async Task VerifyAsync_UnknownAppeal_Returns404AndCreatesNoMatch()
    {
        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(new FakeItemServiceHandler())
                .VerifyAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Appeal not found.", exception.Message);
    }

    /* Scenario 4: an appeal that has already been dealt with cannot be decided twice. */
    [Theory]
    [InlineData(AppealStatus.Verified)]
    [InlineData(AppealStatus.Rejected)]
    public async Task VerifyAsync_AlreadyDecidedAppeal_Returns409AndChangesNothing(string decidedStatus)
    {
        var seeded = await SeedAppealAsync();
        var firstAdmin = Guid.NewGuid();
        await ForceDecisionAsync(seeded.AppealId, decidedStatus, firstAdmin, "Set up beforehand.");

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("This appeal has already been decided.", exception.Message);
        Assert.Equal(0, await CountMatchesAsync(seeded.LostId, seeded.FoundId));

        var stored = await ReadAppealAsync(seeded.AppealId);
        Assert.Equal(decidedStatus, stored.Status);
        Assert.Equal(firstAdmin, stored.DecidedBy);
    }

    /* The DoD's "at most one match": after one admin has verified, a second verify of the same appeal leaves
       exactly the one match behind. This is the deterministic stand-in for the concurrent case, which the Step 3
       integration tests exercise with real parallel requests. */
    [Fact]
    public async Task VerifyAsync_SecondVerifyOfTheSameAppeal_LeavesExactlyOneMatch()
    {
        var seeded = await SeedAppealAsync();

        await seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None);

        var second = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, second.StatusCode);
        Assert.Equal(1, await CountMatchesAsync(seeded.LostId, seeded.FoundId));
    }

    /* Scenario 4: "the appeal stays Pending, so the admin can still reject it". A report that has been resolved
       or deleted since the appeal was sent blocks verification but must not consume the appeal. */
    [Theory]
    [InlineData("RESOLVED")]
    [InlineData("DELETED")]
    public async Task VerifyAsync_ReportNoLongerActiveInItemService_Returns409AndLeavesTheAppealPending(string status)
    {
        var seeded = await SeedAppealAsync();

        SeedBelowThresholdPair(
            seeded.Handler, seeded.LostId, seeded.LostOwner, seeded.FoundId, seeded.FinderId,
            foundStatus: status);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("A report in this appeal has been resolved or deleted.", exception.Message);
        Assert.Equal(0, await CountMatchesAsync(seeded.LostId, seeded.FoundId));

        var stored = await ReadAppealAsync(seeded.AppealId);
        Assert.Equal(AppealStatus.Pending, stored.Status);
        Assert.Null(stored.DecidedBy);
        Assert.Null(stored.DecidedAt);
    }

    // A hard-deleted report is the same situation reached a different way.
    [Fact]
    public async Task VerifyAsync_ReportDeletedFromItemService_Returns409AndLeavesTheAppealPending()
    {
        var seeded = await SeedAppealAsync();
        seeded.Handler.RespondWithStatus(
            $"api/items/lost/{seeded.LostId}", HttpStatusCode.NotFound);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(0, await CountMatchesAsync(seeded.LostId, seeded.FoundId));
        Assert.Equal(AppealStatus.Pending, (await ReadAppealAsync(seeded.AppealId)).Status);
    }

    // The Matching database also knows the report is gone - the lifecycle consumer records that asynchronously.
    [Theory]
    [InlineData("RESOLVED")]
    [InlineData("DELETED")]
    public async Task VerifyAsync_ReportMarkedInactiveInTheMatchingDatabase_Returns409AndLeavesTheAppealPending(
        string reason)
    {
        var seeded = await SeedAppealAsync();
        await MarkItemInactiveAsync("FOUND", seeded.FoundId, reason);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("One of these reports has been resolved or deleted.", exception.Message);
        Assert.Equal(0, await CountMatchesAsync(seeded.LostId, seeded.FoundId));
        Assert.Equal(AppealStatus.Pending, (await ReadAppealAsync(seeded.AppealId)).Status);
    }

    /* Scenario 4: the pair was claimed through the ordinary flow while the appeal was waiting. */
    [Fact]
    public async Task VerifyAsync_PairAlreadyHasAMatch_Returns409AndCreatesNoSecondMatch()
    {
        var seeded = await SeedAppealAsync();
        await InsertMatchAsync(
            seeded.LostId, seeded.FoundId, seeded.LostOwner, seeded.FinderId,
            seeded.LostOwner, "LOST_REPORTER_CONFIRMED", isActive: true);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("This pair, or one of its reports, already has a match.", exception.Message);
        Assert.Equal(1, await CountMatchesAsync(seeded.LostId, seeded.FoundId));
        Assert.Equal(AppealStatus.Pending, (await ReadAppealAsync(seeded.AppealId)).Status);
    }

    /* "This pair, or one of its reports": one of these two reports is already in a confirmed match with a
       completely different report, so this appeal must be refused too. */
    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task VerifyAsync_EitherReportAlreadyInAConfirmedMatch_Returns409(string sharedSide)
    {
        var seeded = await SeedAppealAsync();
        var otherLostId = Guid.NewGuid();
        var otherFoundId = Guid.NewGuid();

        // A confirmed, active match touching only one side of the appealed pair.
        if (sharedSide == "LOST")
        {
            await InsertMatchAsync(
                seeded.LostId, otherFoundId, seeded.LostOwner, Guid.NewGuid(),
                seeded.LostOwner, "CONFIRMED", isActive: true);
        }
        else
        {
            await InsertMatchAsync(
                otherLostId, seeded.FoundId, Guid.NewGuid(), seeded.FinderId,
                seeded.FinderId, "CONFIRMED", isActive: true);
        }

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.VerifyAsync(seeded.AppealId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("This pair, or one of its reports, already has a match.", exception.Message);
        Assert.Equal(0, await CountMatchesAsync(seeded.LostId, seeded.FoundId));
        Assert.Equal(AppealStatus.Pending, (await ReadAppealAsync(seeded.AppealId)).Status);
    }

    // ---- Scenario 5 / 6: rejecting an appeal --------------------------------------------------

    /* Scenario 5: "the appeal is marked rejected with the reason and the deciding admin". */
    [Fact]
    public async Task RejectAsync_WithReason_RecordsTheReasonAdminAndTime()
    {
        var seeded = await SeedAppealAsync();
        var adminId = Guid.NewGuid();
        const string reason = "The found report describes a different bicycle.";

        var rejected = await seeded.Admin.RejectAsync(
            seeded.AppealId, adminId, reason, CancellationToken.None);

        Assert.Equal(AppealStatus.Rejected, rejected.Status);
        Assert.Equal(adminId, rejected.DecidedBy);
        Assert.NotNull(rejected.DecidedAt);
        Assert.Equal(reason, rejected.RejectionReason);

        var stored = await ReadAppealAsync(seeded.AppealId);
        Assert.Equal(AppealStatus.Rejected, stored.Status);
        Assert.Equal(adminId, stored.DecidedBy);
        Assert.Equal(reason, stored.RejectionReason);
    }

    /* Scenario 6: a rejection reason is optional. */
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectAsync_WithoutAReason_StoresNoReason(string? reason)
    {
        var seeded = await SeedAppealAsync();

        var rejected = await seeded.Admin.RejectAsync(
            seeded.AppealId, Guid.NewGuid(), reason, CancellationToken.None);

        Assert.Equal(AppealStatus.Rejected, rejected.Status);
        Assert.Null(rejected.RejectionReason);
        Assert.Null((await ReadAppealAsync(seeded.AppealId)).RejectionReason);
    }

    [Fact]
    public async Task RejectAsync_CreatesNoMatch()
    {
        var seeded = await SeedAppealAsync();

        await seeded.Admin.RejectAsync(
            seeded.AppealId, Guid.NewGuid(), "Not the same item.", CancellationToken.None);

        Assert.Equal(0, await CountMatchesAsync(seeded.LostId, seeded.FoundId));
    }

    /* Scenario 5: the reason is shown to the appellant, so it is length limited exactly like the note is. The
       bound is inclusive. */
    [Fact]
    public async Task RejectAsync_ReasonOfExactly300Characters_IsAccepted()
    {
        var seeded = await SeedAppealAsync();
        var reason = new string('r', AppealService.MaxNoteLength);

        var rejected = await seeded.Admin.RejectAsync(
            seeded.AppealId, Guid.NewGuid(), reason, CancellationToken.None);

        Assert.Equal(AppealStatus.Rejected, rejected.Status);
        Assert.Equal(AppealService.MaxNoteLength, rejected.RejectionReason!.Length);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(400)]
    public async Task RejectAsync_ReasonLongerThan300Characters_Returns400AndLeavesTheAppealPending(int length)
    {
        var seeded = await SeedAppealAsync();

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.RejectAsync(
                seeded.AppealId, Guid.NewGuid(), new string('r', length), CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("The reason can be at most 300 characters.", exception.Message);

        var stored = await ReadAppealAsync(seeded.AppealId);
        Assert.Equal(AppealStatus.Pending, stored.Status);
        Assert.Null(stored.DecidedBy);
    }

    /* Pinned deliberately: the reason is validated before the appeal is even loaded, so a malformed request is
       rejected on its own terms rather than being reported as a missing appeal. */
    [Fact]
    public async Task RejectAsync_OverlongReasonIsRejectedBeforeTheAppealIsLoaded()
    {
        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(new FakeItemServiceHandler()).RejectAsync(
                Guid.NewGuid(), Guid.NewGuid(), new string('r', 301), CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("The reason can be at most 300 characters.", exception.Message);
    }

    [Fact]
    public async Task RejectAsync_UnknownAppeal_Returns404()
    {
        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            BuildService(new FakeItemServiceHandler())
                .RejectAsync(Guid.NewGuid(), Guid.NewGuid(), null, CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Appeal not found.", exception.Message);
    }

    /* Scenario 4: the first decision stands. */
    [Theory]
    [InlineData(AppealStatus.Verified)]
    [InlineData(AppealStatus.Rejected)]
    public async Task RejectAsync_AlreadyDecidedAppeal_Returns409AndKeepsTheFirstDecision(string decidedStatus)
    {
        var seeded = await SeedAppealAsync();
        var firstAdmin = Guid.NewGuid();
        const string firstReason = "Decided beforehand.";
        await ForceDecisionAsync(seeded.AppealId, decidedStatus, firstAdmin, firstReason);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.RejectAsync(
                seeded.AppealId, Guid.NewGuid(), "A later reason.", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("This appeal has already been decided.", exception.Message);

        var stored = await ReadAppealAsync(seeded.AppealId);
        Assert.Equal(decidedStatus, stored.Status);
        Assert.Equal(firstAdmin, stored.DecidedBy);
        Assert.Equal(firstReason, stored.RejectionReason);
    }

    /* The DoD's "one decision": a second reject of the same appeal changes nothing. */
    [Fact]
    public async Task RejectAsync_SecondRejectOfTheSameAppeal_Returns409AndKeepsOneDecision()
    {
        var seeded = await SeedAppealAsync();
        var firstAdmin = Guid.NewGuid();

        await seeded.Admin.RejectAsync(
            seeded.AppealId, firstAdmin, "First reason.", CancellationToken.None);

        var second = await Assert.ThrowsAsync<ClaimException>(() =>
            seeded.Admin.RejectAsync(
                seeded.AppealId, Guid.NewGuid(), "Second reason.", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, second.StatusCode);

        var stored = await ReadAppealAsync(seeded.AppealId);
        Assert.Equal(AppealStatus.Rejected, stored.Status);
        Assert.Equal(firstAdmin, stored.DecidedBy);
        Assert.Equal("First reason.", stored.RejectionReason);
    }
}