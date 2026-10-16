using AdminVerifyService.Databases;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using Microsoft.AspNetCore.Http;
using MySqlConnector;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// Story LF-267 + LF-268 contract tests for SpamCaseRepository against a real MySQL
/// (Testcontainers, the app's real migrations including 007 kick_status). Pins the case-handling
/// semantics the API layer (AdminSpamRecordsController) delegates to:
///
/// LF-267 (open):
///  - GET detail: the record's own fields (score, status, kick status, flagged-at UTC) plus its
///    listings ordered by posted_at then id, with lowercase listing types - the popup contract;
///  - POST open: NEEDS_REVIEW -> UNDER_REVIEW and the collecting claim is released ("stops
///    collecting"); an already-open / pending-solve / closed record is a NO-OP that returns the
///    detail with its status unchanged (never a refusal - Scenario 3 read-only flows);
///  - two concurrent opens: both succeed, only one transition (status changed once).
///
/// LF-268 (dismiss):
///  - POST dismiss: UNDER_REVIEW -> DISMISSED, claim released, detail returned;
///  - any other status (including never-opened NEEDS_REVIEW and terminal states) is refused with
///    409 "This record is already being handled." - "A record that is not Under review cannot be
///    dismissed";
///  - race: two concurrent dismisses - exactly one succeeds, one gets the 409.
///
/// Unknown records throw 404 "Spam record not found." from every verb.
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamCaseRepositoryTests : IClassFixture<AdminVerifyDbFixture>
{
    private static readonly Guid UserId = Guid.Parse("10000000-0000-0000-0000-00000000BEEF");
    private static readonly DateTime RefNow = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly AdminVerifyDbFixture _fixture;

    public SpamCaseRepositoryTests(AdminVerifyDbFixture fixture) => _fixture = fixture;

    private SpamCaseRepository Repository(DateTime now) =>
        new(_fixture.Connections, new MutableTimeProvider(now));

    /// <summary>A TimeProvider whose clock QA drives explicitly (mirrors the LF-82 tests).</summary>
    private sealed class MutableTimeProvider(DateTime start) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTime _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return new DateTimeOffset(_now, TimeSpan.Zero);
            }
        }
    }

    private record SpamRecordRow(string Status, Guid? CollectingUserId, string KickStatus);

    private record DismissOutcome(SpamRecordDetail? Detail, SpamReviewException? Error);

    // ---- seeding / read helpers ----

    private static async Task ResetAsync(IDbConnectionFactory connections)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        // FK child tables first, then tracked_listings (referenced by spam_record_listings).
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
        IDbConnectionFactory connections,
        Guid recordId,
        string status,
        int scoreA = 3,
        string kickStatus = KickStatus.NotRequested,
        Guid? collectingUserId = null)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var record = new MySqlCommand(
            """
            INSERT INTO spam_records
                (id, user_id, collecting_user_id, score_a, status, collecting_until,
                 kick_status, created_at, updated_at)
            VALUES
                (@id, @userId, @collectingUserId, @scoreA, @status, @until,
                 @kickStatus, @createdAt, @createdAt);
            """,
            connection);

        record.Parameters.AddWithValue("@id", recordId);
        record.Parameters.AddWithValue("@userId", UserId);
        record.Parameters.AddWithValue("@collectingUserId", (object?)collectingUserId ?? DBNull.Value);
        record.Parameters.AddWithValue("@scoreA", scoreA);
        record.Parameters.AddWithValue("@status", status);
        record.Parameters.AddWithValue("@until", RefNow.AddHours(1));
        record.Parameters.AddWithValue("@kickStatus", kickStatus);
        record.Parameters.AddWithValue("@createdAt", RefNow);

        await record.ExecuteNonQueryAsync();
    }

    private static async Task LinkListingAsync(
        IDbConnectionFactory connections,
        Guid recordId,
        Guid listingId,
        string listingType,
        DateTime postedAt,
        string? solveResult = null)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var tracked = new MySqlCommand(
            """
            INSERT INTO tracked_listings (listing_id, listing_type, user_id, posted_at, created_at)
            VALUES (@listingId, @listingType, @userId, @postedAt, @now);
            """,
            connection);
        tracked.Parameters.AddWithValue("@listingId", listingId);
        tracked.Parameters.AddWithValue("@listingType", listingType);
        tracked.Parameters.AddWithValue("@userId", UserId);
        tracked.Parameters.AddWithValue("@postedAt", postedAt);
        tracked.Parameters.AddWithValue("@now", RefNow);
        await tracked.ExecuteNonQueryAsync();

        await using var link = new MySqlCommand(
            """
            INSERT INTO spam_record_listings (spam_record_id, listing_id, solve_result, added_at)
            VALUES (@recordId, @listingId, @solveResult, @now);
            """,
            connection);
        link.Parameters.AddWithValue("@recordId", recordId);
        link.Parameters.AddWithValue("@listingId", listingId);
        link.Parameters.AddWithValue("@solveResult", (object?)solveResult ?? DBNull.Value);
        link.Parameters.AddWithValue("@now", RefNow);
        await link.ExecuteNonQueryAsync();
    }

    private static async Task<SpamRecordRow> ReadRecordAsync(IDbConnectionFactory connections, Guid recordId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status, collecting_user_id, kick_status FROM spam_records WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("@id", recordId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new Xunit.Sdk.XunitException($"No spam_records row for record {recordId}");
        }

        var collectingUserId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);

        return new SpamRecordRow(reader.GetString(0), collectingUserId, reader.GetString(2));
    }

    /// <summary>A fixed, readable record id per status so the refusal loops stay deterministic.</summary>
    private static Guid StatusId(string status) => status switch
    {
        SpamRecordStatus.NeedsReview => Guid.Parse("30000000-0000-0000-0000-000000000101"),
        SpamRecordStatus.UnderReview => Guid.Parse("30000000-0000-0000-0000-000000000102"),
        SpamRecordStatus.PendingSolve => Guid.Parse("30000000-0000-0000-0000-000000000103"),
        SpamRecordStatus.Solved => Guid.Parse("30000000-0000-0000-0000-000000000104"),
        SpamRecordStatus.Dismissed => Guid.Parse("30000000-0000-0000-0000-000000000105"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown spam record status.")
    };

    // ---- LF-267: detail ----

    [Fact] // LF-267 DoD: a missing record yields the shared 404, never a 403.
    public async Task Detail_UnknownRecord_Throws404_RecordNotFound()
    {
        await ResetAsync(_fixture.Connections);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).GetDetailAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Spam record not found.", exception.Message);
    }

    [Fact] // LF-267 Scenario 2: the popup contract - record fields plus listings ordered by
    // posted_at then id, lowercase types, UTC dates, and the kick status.
    public async Task Detail_ReturnsRecordFields_AndListingsInPostingOrder()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C001");
        var early = Guid.Parse("10000000-0000-0000-0000-00000000C001");
        var earlyTie = Guid.Parse("10000000-0000-0000-0000-00000000C002");
        var late = Guid.Parse("10000000-0000-0000-0000-00000000C003");

        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.UnderReview, scoreA: 6, kickStatus: KickStatus.Pending);
        await LinkListingAsync(_fixture.Connections, recordId, early, "LOST", RefNow.AddHours(-3));
        await LinkListingAsync(_fixture.Connections, recordId, earlyTie, "LOST", RefNow.AddHours(-3), solveResult: "DELETED");
        await LinkListingAsync(_fixture.Connections, recordId, late, "FOUND", RefNow.AddHours(-1));

        var detail = await Repository(RefNow).GetDetailAsync(recordId, CancellationToken.None);

        Assert.Equal(recordId, detail.Id);
        Assert.Equal(UserId, detail.UserId);
        Assert.Equal(6, detail.ScoreA);
        Assert.Equal(SpamRecordStatus.UnderReview, detail.Status);
        Assert.Equal(KickStatus.Pending, detail.KickStatus);
        Assert.Equal(DateTimeKind.Utc, detail.FlaggedAt.Kind);
        Assert.Equal(RefNow, detail.FlaggedAt);

        // Oldest first, id tie-break ascending: early, earlyTie (same posted_at), then late.
        Assert.Equal([early, earlyTie, late], detail.Listings.Select(l => l.ListingId).ToArray());
        // Listing types are lowercased for the frontend contract.
        Assert.Equal("lost", detail.Listings[0].ListingType);
        Assert.Equal("found", detail.Listings[2].ListingType);
        // Solve results pass through when a Solve has already run (LF-269 resume view).
        Assert.Null(detail.Listings[0].SolveResult);
        Assert.Equal("DELETED", detail.Listings[1].SolveResult);
    }

    [Fact] // LF-267 Scenario 2 + LF-269 Scenario 5: a solve's per-listing results and the kick
    // result are carried by the detail once a solve has run.
    public async Task Detail_SolveAlreadyRun_CarriesListingResultsAndKickStatus()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C007");
        var deleted = Guid.Parse("10000000-0000-0000-0000-00000000C007");
        var failed = Guid.Parse("10000000-0000-0000-0000-00000000C008");

        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.Solved, kickStatus: KickStatus.Failed);
        await LinkListingAsync(_fixture.Connections, recordId, deleted, "LOST", RefNow.AddHours(-2), solveResult: "DELETED");
        await LinkListingAsync(_fixture.Connections, recordId, failed, "FOUND", RefNow.AddHours(-1), solveResult: "FAILED");

        var detail = await Repository(RefNow).GetDetailAsync(recordId, CancellationToken.None);

        Assert.Equal(KickStatus.Failed, detail.KickStatus);
        Assert.Equal(2, detail.Listings.Count);
        Assert.Equal("DELETED", detail.Listings.Single(l => l.ListingId == deleted).SolveResult);
        Assert.Equal("FAILED", detail.Listings.Single(l => l.ListingId == failed).SolveResult);
    }

    // ---- LF-267: open ----

    [Fact] // LF-267 S1: opening a NEEDS_REVIEW record sets UNDER_REVIEW and stops collecting.
    public async Task Open_NeedsReview_BecomesUnderReview_AndClearsCollectingClaim()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C101");
        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.NeedsReview, collectingUserId: UserId);

        var detail = await Repository(RefNow).OpenAsync(recordId, CancellationToken.None);

        Assert.Equal(SpamRecordStatus.UnderReview, detail.Status);

        var row = await ReadRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(SpamRecordStatus.UnderReview, row.Status);
        Assert.Null(row.CollectingUserId); // collecting stops from that moment
    }

    [Fact] // LF-267 S3: re-opening an already-open record returns the detail with no status change.
    public async Task Open_AlreadyOpen_ReturnsDetail_WithoutChangingStatus()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C102");
        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.UnderReview);

        var detail = await Repository(RefNow).OpenAsync(recordId, CancellationToken.None);

        Assert.Equal(SpamRecordStatus.UnderReview, detail.Status);
        Assert.Equal(SpamRecordStatus.UnderReview, (await ReadRecordAsync(_fixture.Connections, recordId)).Status);
    }

    [Fact] // LF-267 S3: a pending-solve record opens straight away, without the warning or a change.
    public async Task Open_PendingSolve_ReturnsDetail_WithoutChangingStatus()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C103");
        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.PendingSolve);

        var detail = await Repository(RefNow).OpenAsync(recordId, CancellationToken.None);

        Assert.Equal(SpamRecordStatus.PendingSolve, detail.Status);
        Assert.Equal(SpamRecordStatus.PendingSolve, (await ReadRecordAsync(_fixture.Connections, recordId)).Status);
    }

    [Fact] // LF-267 S7: a dismissed record opens as a no-op - read-only detail, status unchanged.
    public async Task Open_Dismissed_ReturnsDetail_WithoutChangingStatus()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C104");
        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.Dismissed);

        var detail = await Repository(RefNow).OpenAsync(recordId, CancellationToken.None);

        Assert.Equal(SpamRecordStatus.Dismissed, detail.Status);
        Assert.Equal(SpamRecordStatus.Dismissed, (await ReadRecordAsync(_fixture.Connections, recordId)).Status);
    }

    [Fact] // LF-267 S7: a solved record opens as a no-op - read-only detail, status unchanged.
    public async Task Open_Solved_ReturnsDetail_WithoutChangingStatus()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C105");
        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.Solved);

        var detail = await Repository(RefNow).OpenAsync(recordId, CancellationToken.None);

        Assert.Equal(SpamRecordStatus.Solved, detail.Status);
        Assert.Equal(SpamRecordStatus.Solved, (await ReadRecordAsync(_fixture.Connections, recordId)).Status);
    }

    [Fact] // LF-267 DoD: a missing record gives the shared 404.
    public async Task Open_UnknownRecord_Throws404_RecordNotFound()
    {
        await ResetAsync(_fixture.Connections);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).OpenAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Spam record not found.", exception.Message);
    }

    [Fact] // LF-267 DoD: two concurrent opens for one NEEDS_REVIEW record both succeed and the
    // status lands on UNDER_REVIEW exactly once (the second UPDATE matches no row but still
    // reports the current detail - never a refusal).
    public async Task Open_TwoConcurrent_BothSucceed_ExactlyOneTransition()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C106");
        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.NeedsReview, collectingUserId: UserId);

        var repo = Repository(RefNow);

        var first = repo.OpenAsync(recordId, CancellationToken.None);
        var second = repo.OpenAsync(recordId, CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        Assert.All(results, detail => Assert.Equal(SpamRecordStatus.UnderReview, detail.Status));
        Assert.Equal(SpamRecordStatus.UnderReview, (await ReadRecordAsync(_fixture.Connections, recordId)).Status);
    }

    // ---- LF-268: dismiss ----

    [Fact] // LF-268 S1: dismissing an UNDER_REVIEW record sets DISMISSED and releases the claim.
    public async Task Dismiss_UnderReview_BecomesDismissed_AndClearsCollectingClaim()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C201");
        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.UnderReview, collectingUserId: UserId);

        var detail = await Repository(RefNow).DismissAsync(recordId, CancellationToken.None);

        Assert.Equal(SpamRecordStatus.Dismissed, detail.Status);

        var row = await ReadRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(SpamRecordStatus.Dismissed, row.Status);
        Assert.Null(row.CollectingUserId);
    }

    [Fact] // LF-268 DoD: "A record that is not Under review cannot be dismissed." - every other
    // status (including never-opened NEEDS_REVIEW and terminal states) is refused, never moved.
    public async Task Dismiss_NotUnderReview_Throws409_AlreadyBeingHandled()
    {
        var statuses = new[]
        {
            SpamRecordStatus.NeedsReview,
            SpamRecordStatus.PendingSolve,
            SpamRecordStatus.Solved,
            SpamRecordStatus.Dismissed
        };

        foreach (var status in statuses)
        {
            await ResetAsync(_fixture.Connections);
            var id = StatusId(status);

            await SeedRecordAsync(_fixture.Connections, id, status);

            var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
                Repository(RefNow).DismissAsync(id, CancellationToken.None));

            Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
            Assert.Equal("This record is already being handled.", exception.Message);
            Assert.Equal(status, (await ReadRecordAsync(_fixture.Connections, id)).Status);
        }
    }

    [Fact] // LF-268: unknown record - the shared 404.
    public async Task Dismiss_UnknownRecord_Throws404_RecordNotFound()
    {
        await ResetAsync(_fixture.Connections);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).DismissAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Spam record not found.", exception.Message);
    }

    [Fact] // LF-268 DoD: two concurrent dismisses - exactly one succeeds, the other is refused 409.
    public async Task Dismiss_TwoConcurrent_ExactlyOneSucceeds()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("30000000-0000-0000-0000-00000000C205");
        await SeedRecordAsync(_fixture.Connections, recordId, SpamRecordStatus.UnderReview, collectingUserId: UserId);

        var repo = Repository(RefNow);

        // Capture the 409 refusal explicitly - the failing task's exception is observed here.
        var attempts = new[]
        {
            CaptureAsync(), CaptureAsync()
        };

        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, o => o.Detail is not null);
        Assert.Single(outcomes, o => o.Error is not null);

        var refusal = outcomes.Single(o => o.Error is not null).Error!;
        Assert.Equal(StatusCodes.Status409Conflict, refusal.StatusCode);
        Assert.Equal("This record is already being handled.", refusal.Message);

        Assert.Equal(SpamRecordStatus.Dismissed, (await ReadRecordAsync(_fixture.Connections, recordId)).Status);

        async Task<DismissOutcome> CaptureAsync()
        {
            try
            {
                var detail = await repo.DismissAsync(recordId, CancellationToken.None);
                return new DismissOutcome(detail, null);
            }
            catch (SpamReviewException exception)
            {
                return new DismissOutcome(null, exception);
            }
        }
    }
}