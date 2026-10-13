using AdminVerifyService.Databases;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using Microsoft.AspNetCore.Http;
using MySqlConnector;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// Story LF-269 contract tests for SpamSolveRepository against a real MySQL (Testcontainers, the
/// app's real migrations including 007 kick_status). Pins the solve state machine the frontend
/// runner drives:
///
/// start:      UNDER_REVIEW -> PENDING_SOLVE (claim released), kick choice saved to kick_status;
///             anything else with resume=false is refused 409 "already being handled"; a resume on
///             PENDING_SOLVE is a read-only continuation regardless of the request's kick value;
///             resume on any other status is refused; exactly one of two concurrent starts succeeds.
/// listing:    DELETED/SKIPPED/FAILED accepted, trimmed + case-normalised; anything else 400;
///             only PENDING_SOLVE records accept results (else 409); a listing outside the record
///             is 404; "already deleted" can never be overwritten (silent no-op).
/// kick:       PENDING -> KICKED/FAILED (one shot), anything else 409; invalid result 400; unknown
///             record 404.
/// finish:     SOLVED only when every listing has DELETED/SKIPPED and the kick is no longer
///             PENDING; otherwise the exact blocking reason ("The kick has not been attempted
///             yet." / "Some listings have not been deleted or skipped yet." / already handled).
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamSolveRepositoryTests : IClassFixture<AdminVerifyDbFixture>
{
    private static readonly Guid UserId = Guid.Parse("10000000-0000-0000-0000-00000000B0B0");
    private static readonly DateTime RefNow = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid RecordA = Guid.Parse("30000000-0000-0000-0000-00000000D001");
    private static readonly Guid ListingA = Guid.Parse("10000000-0000-0000-0000-00000000D001");
    private static readonly Guid ListingB = Guid.Parse("10000000-0000-0000-0000-00000000D002");

    private readonly AdminVerifyDbFixture _fixture;

    public SpamSolveRepositoryTests(AdminVerifyDbFixture fixture) => _fixture = fixture;

    private SpamSolveRepository Repository(DateTime now) =>
        new(_fixture.Connections, new MutableTimeProvider(now));

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

    private record SolveOutcome(SpamRecordDetail? Detail, SpamReviewException? Error);

    private record SpamRecordRow(string Status, Guid? CollectingUserId, string KickStatus);

    /// <summary>A fixed, readable record id per status so the refusal loops stay deterministic.</summary>
    private static Guid StatusId(string status) => status switch
    {
        SpamRecordStatus.NeedsReview => Guid.Parse("30000000-0000-0000-0000-000000000201"),
        SpamRecordStatus.UnderReview => Guid.Parse("30000000-0000-0000-0000-000000000202"),
        SpamRecordStatus.PendingSolve => Guid.Parse("30000000-0000-0000-0000-000000000203"),
        SpamRecordStatus.Solved => Guid.Parse("30000000-0000-0000-0000-000000000204"),
        SpamRecordStatus.Dismissed => Guid.Parse("30000000-0000-0000-0000-000000000205"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown spam record status.")
    };

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
        record.Parameters.AddWithValue("@scoreA", 2);
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
        DateTime postedAt,
        string? solveResult = null)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var tracked = new MySqlCommand(
            """
            INSERT INTO tracked_listings (listing_id, listing_type, user_id, posted_at, created_at)
            VALUES (@listingId, 'LOST', @userId, @postedAt, @now);
            """,
            connection);
        tracked.Parameters.AddWithValue("@listingId", listingId);
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

    private static async Task<IReadOnlyList<string?>> ReadSolveResultsAsync(
        IDbConnectionFactory connections,
        Guid recordId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            SELECT solve_result FROM spam_record_listings
            WHERE spam_record_id = @recordId
            ORDER BY listing_id;
            """,
            connection);
        command.Parameters.AddWithValue("@recordId", recordId);

        var results = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return results;
    }

    // ---- start ----

    [Fact] // LF-269 S2: confirming with the kick checked sets PENDING_SOLVE + kick PENDING.
    public async Task Start_UnderReview_KickChecked_SetsPendingSolveAndKickPending_ClearsClaim()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.UnderReview, collectingUserId: UserId);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2));

        var detail = await Repository(RefNow).StartAsync(RecordA, new SolveRequest(Kick: true, Resume: false), CancellationToken.None);

        Assert.Equal(SpamRecordStatus.PendingSolve, detail.Status);
        Assert.Equal(KickStatus.Pending, detail.KickStatus);

        var row = await ReadRecordAsync(_fixture.Connections, RecordA);
        Assert.Equal(SpamRecordStatus.PendingSolve, row.Status);
        Assert.Equal(KickStatus.Pending, row.KickStatus);
        Assert.Null(row.CollectingUserId);
    }

    [Fact] // LF-269 DoD: kick unchecked -> NOT_REQUESTED is saved on the server.
    public async Task Start_UnderReview_KickUnchecked_SetsKickNotRequested()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.UnderReview);

        var detail = await Repository(RefNow).StartAsync(RecordA, new SolveRequest(Kick: false, Resume: false), CancellationToken.None);

        Assert.Equal(SpamRecordStatus.PendingSolve, detail.Status);
        Assert.Equal(KickStatus.NotRequested, detail.KickStatus);
        Assert.Equal(KickStatus.NotRequested, (await ReadRecordAsync(_fixture.Connections, RecordA)).KickStatus);
    }

    [Fact] // LF-269 S6/DoD: a record that is not UNDER_REVIEW cannot start a solve.
    public async Task Start_NotUnderReview_Throws409_AlreadyBeingHandled()
    {
        var statuses = new[] { SpamRecordStatus.NeedsReview, SpamRecordStatus.PendingSolve, SpamRecordStatus.Solved, SpamRecordStatus.Dismissed };

        foreach (var status in statuses)
        {
            await ResetAsync(_fixture.Connections);
            var id = StatusId(status);
            await SeedRecordAsync(_fixture.Connections, id, status);

            var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
                Repository(RefNow).StartAsync(id, new SolveRequest(Kick: false, Resume: false), CancellationToken.None));

            Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
            Assert.Equal("This record is already being handled.", exception.Message);
            Assert.Equal(status, (await ReadRecordAsync(_fixture.Connections, id)).Status);
        }
    }

    [Fact] // LF-269 S5: resuming a PENDING_SOLVE record returns its state and keeps the saved kick choice.
    public async Task Start_ResumeOnPendingSolve_ReturnsDetail_PreservingKickChoice()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve, kickStatus: KickStatus.Pending);

        var detail = await Repository(RefNow).StartAsync(RecordA, new SolveRequest(Kick: true, Resume: true), CancellationToken.None);

        Assert.Equal(SpamRecordStatus.PendingSolve, detail.Status);
        Assert.Equal(KickStatus.Pending, detail.KickStatus);
        Assert.Equal(KickStatus.Pending, (await ReadRecordAsync(_fixture.Connections, RecordA)).KickStatus);
    }

    [Fact] // LF-269 DoD: resume only works on PENDING_SOLVE - anywhere else it is refused.
    public async Task Start_ResumeOnNonPendingSolve_Throws409_AlreadyBeingHandled()
    {
        var statuses = new[] { SpamRecordStatus.NeedsReview, SpamRecordStatus.UnderReview, SpamRecordStatus.Solved, SpamRecordStatus.Dismissed };

        foreach (var status in statuses)
        {
            await ResetAsync(_fixture.Connections);
            var id = StatusId(status);
            await SeedRecordAsync(_fixture.Connections, id, status);

            var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
                Repository(RefNow).StartAsync(id, new SolveRequest(Kick: false, Resume: true), CancellationToken.None));

            Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
            Assert.Equal("This record is already being handled.", exception.Message);
        }
    }

    [Fact] // Unknown record: the shared 404.
    public async Task Start_UnknownRecord_Throws404_RecordNotFound()
    {
        await ResetAsync(_fixture.Connections);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).StartAsync(Guid.NewGuid(), new SolveRequest(Kick: false, Resume: false), CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Spam record not found.", exception.Message);
    }

    [Fact] // LF-269 DoD: two admins starting at the same moment - exactly one succeeds.
    public async Task Start_TwoConcurrent_ExactlyOneSucceeds()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.UnderReview);

        var repo = Repository(RefNow);

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

        Assert.Equal(SpamRecordStatus.PendingSolve, (await ReadRecordAsync(_fixture.Connections, RecordA)).Status);

        async Task<SolveOutcome> CaptureAsync()
        {
            try
            {
                var detail = await repo.StartAsync(RecordA, new SolveRequest(Kick: false, Resume: false), CancellationToken.None);
                return new SolveOutcome(detail, null);
            }
            catch (SpamReviewException exception)
            {
                return new SolveOutcome(null, exception);
            }
        }
    }

    // ---- listing results ----

    [Theory] // LF-269 S2/S5: per-listing results persist and show in later details.
    [InlineData("DELETED")]
    [InlineData("SKIPPED")]
    [InlineData("FAILED")]
    public async Task ListingResult_WritesTheAcceptedResult(string result)
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2));

        await Repository(RefNow).SaveListingResultAsync(RecordA, ListingA, result, CancellationToken.None);

        Assert.Equal([result], await ReadSolveResultsAsync(_fixture.Connections, RecordA));
    }

    [Fact] // LF-269 DoD: results are trimmed and case-normalised before persisting.
    public async Task ListingResult_TrimsAndNormalisesCase()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2));

        await Repository(RefNow).SaveListingResultAsync(RecordA, ListingA, "  failed  ", CancellationToken.None);

        Assert.Equal(["FAILED"], await ReadSolveResultsAsync(_fixture.Connections, RecordA));
    }

    [Theory] // LF-269 DoD: anything outside DELETED/SKIPPED/FAILED is a 400 with the exact message.
    [InlineData("KICKED")]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ListingResult_Invalid_Throws400(string? result)
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2));

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).SaveListingResultAsync(RecordA, ListingA, result, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("Result must be DELETED, SKIPPED or FAILED.", exception.Message);
        Assert.Null((await ReadSolveResultsAsync(_fixture.Connections, RecordA))[0]);
    }

    [Fact] // LF-269 DoD: only a PENDING_SOLVE record accepts listing results.
    public async Task ListingResult_RecordNotPendingSolve_Throws409_NotBeingSolved()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.UnderReview);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2));

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).SaveListingResultAsync(RecordA, ListingA, "DELETED", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("This record is not being solved.", exception.Message);
    }

    [Fact] // LF-269 DoD: a listing that is not in the record is refused with 404.
    public async Task ListingResult_ListingNotInRecord_Throws404_NotInRecord()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2));

        var unrelated = Guid.Parse("10000000-0000-0000-0000-00000000D00F");

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).SaveListingResultAsync(RecordA, unrelated, "DELETED", CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Listing is not in this record.", exception.Message);
    }

    [Fact] // LF-269 DoD: a DELETED result can never be overwritten - a resubmit is a silent no-op.
    public async Task ListingResult_AlreadyDeleted_IsSilentNoOp()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2), solveResult: "DELETED");

        await Repository(RefNow).SaveListingResultAsync(RecordA, ListingA, "FAILED", CancellationToken.None);

        Assert.Equal(["DELETED"], await ReadSolveResultsAsync(_fixture.Connections, RecordA));
    }

    // ---- kick result ----

    [Fact] // LF-269 S3: a successful kick persists KICKED.
    public async Task KickResult_KickedFromPending_PersistsAndReturnsDetail()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve, kickStatus: KickStatus.Pending);

        var detail = await Repository(RefNow).SaveKickResultAsync(RecordA, "KICKED", CancellationToken.None);

        Assert.Equal(KickStatus.Kicked, detail.KickStatus);
        Assert.Equal(KickStatus.Kicked, (await ReadRecordAsync(_fixture.Connections, RecordA)).KickStatus);
    }

    [Fact] // LF-269 S3: a refused/failed kick persists FAILED and is shown on the record.
    public async Task KickResult_FailedFromPending_PersistsAndReturnsDetail()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve, kickStatus: KickStatus.Pending);

        var detail = await Repository(RefNow).SaveKickResultAsync(RecordA, "failed", CancellationToken.None);

        Assert.Equal(KickStatus.Failed, detail.KickStatus);
        Assert.Equal(KickStatus.Failed, (await ReadRecordAsync(_fixture.Connections, RecordA)).KickStatus);
    }

    [Theory] // LF-269 DoD: only KICKED/FAILED are accepted.
    [InlineData("DELETED")]
    [InlineData("banana")]
    [InlineData("")]
    public async Task KickResult_Invalid_Throws400(string result)
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve, kickStatus: KickStatus.Pending);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).SaveKickResultAsync(RecordA, result, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("Result must be KICKED or FAILED.", exception.Message);
        Assert.Equal(KickStatus.Pending, (await ReadRecordAsync(_fixture.Connections, RecordA)).KickStatus);
    }

    [Fact] // LF-269 DoD: no kick requested - saving a kick result is refused.
    public async Task KickResult_NotRequested_Throws409_NotRequestedOrAlreadyAttempted()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve, kickStatus: KickStatus.NotRequested);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).SaveKickResultAsync(RecordA, "KICKED", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("The kick was not requested or has already been attempted.", exception.Message);
    }

    [Theory] // LF-269 DoD: the kick result is one-shot - a settled kick cannot be re-saved.
    [InlineData(KickStatus.Kicked)]
    [InlineData(KickStatus.Failed)]
    public async Task KickResult_AlreadyAttempted_Throws409(string settled)
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve, kickStatus: settled);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).SaveKickResultAsync(RecordA, "KICKED", CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("The kick was not requested or has already been attempted.", exception.Message);
    }

    [Fact] // Unknown record: the shared 404.
    public async Task KickResult_UnknownRecord_Throws404_RecordNotFound()
    {
        await ResetAsync(_fixture.Connections);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).SaveKickResultAsync(Guid.NewGuid(), "KICKED", CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Spam record not found.", exception.Message);
    }

    // ---- finish ----

    [Theory] // LF-269 S4: every listing deleted or skipped + a settled kick -> SOLVED.
    [InlineData(KickStatus.NotRequested)]
    [InlineData(KickStatus.Kicked)]
    public async Task Finish_AllListingsDone_KickSettled_Solves(string settledKick)
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve, kickStatus: settledKick);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2), solveResult: "DELETED");
        await LinkListingAsync(_fixture.Connections, RecordA, ListingB, RefNow.AddHours(-1), solveResult: "SKIPPED");

        var detail = await Repository(RefNow).FinishAsync(RecordA, CancellationToken.None);

        Assert.Equal(SpamRecordStatus.Solved, detail.Status);
        Assert.Equal(SpamRecordStatus.Solved, (await ReadRecordAsync(_fixture.Connections, RecordA)).Status);
    }

    [Fact] // LF-269 S3/DoD: the kick must be attempted before finishing.
    public async Task Finish_KickStillPending_Throws409_KickNotAttemptedYet()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve, kickStatus: KickStatus.Pending);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2), solveResult: "DELETED");

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).FinishAsync(RecordA, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("The kick has not been attempted yet.", exception.Message);
        Assert.Equal(SpamRecordStatus.PendingSolve, (await ReadRecordAsync(_fixture.Connections, RecordA)).Status);
    }

    [Fact] // LF-269 S4/DoD: a listing with no result yet blocks SOLVED.
    public async Task Finish_ListingWithoutResult_Throws409_ListingsNotDone()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2), solveResult: "DELETED");
        await LinkListingAsync(_fixture.Connections, RecordA, ListingB, RefNow.AddHours(-1));

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).FinishAsync(RecordA, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("Some listings have not been deleted or skipped yet.", exception.Message);
        Assert.Equal(SpamRecordStatus.PendingSolve, (await ReadRecordAsync(_fixture.Connections, RecordA)).Status);
    }

    [Fact] // LF-269 S2/DoD: a FAILED listing also blocks SOLVED - the record stays Pending solve.
    public async Task Finish_ListingFailed_Throws409_ListingsNotDone()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecordAsync(_fixture.Connections, RecordA, SpamRecordStatus.PendingSolve);
        await LinkListingAsync(_fixture.Connections, RecordA, ListingA, RefNow.AddHours(-2), solveResult: "DELETED");
        await LinkListingAsync(_fixture.Connections, RecordA, ListingB, RefNow.AddHours(-1), solveResult: "FAILED");

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).FinishAsync(RecordA, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("Some listings have not been deleted or skipped yet.", exception.Message);
    }

    [Fact] // LF-269 S6/DoD: finishing a record that is not Pending solve is already handled.
    public async Task Finish_RecordNotPendingSolve_Throws409_AlreadyBeingHandled()
    {
        var statuses = new[] { SpamRecordStatus.UnderReview, SpamRecordStatus.Solved };

        foreach (var status in statuses)
        {
            await ResetAsync(_fixture.Connections);
            var id = StatusId(status);
            await SeedRecordAsync(_fixture.Connections, id, status);

            var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
                Repository(RefNow).FinishAsync(id, CancellationToken.None));

            Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
            Assert.Equal("This record is already being handled.", exception.Message);
        }
    }

    [Fact] // Unknown record: the shared 404.
    public async Task Finish_UnknownRecord_Throws404_RecordNotFound()
    {
        await ResetAsync(_fixture.Connections);

        var exception = await Assert.ThrowsAsync<SpamReviewException>(() =>
            Repository(RefNow).FinishAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal("Spam record not found.", exception.Message);
    }
}