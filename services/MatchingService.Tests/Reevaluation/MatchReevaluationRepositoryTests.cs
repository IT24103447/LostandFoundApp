using MatchingService.Claims;
using MatchingService.Reevaluation;
using MatchingService.Services;
using MatchingService.Tests.Integration;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Reevaluation;

/// <summary>
/// Story 8 tests proving MatchReevaluationRepository's own SQL directly against real, disposable
/// MySQL (running migration 011_AddMatchReevaluation.sql).
/// </summary>
[Collection("Docker Integration Tests 10")]
public sealed class MatchReevaluationRepositoryTests : IClassFixture<ClaimServiceDbFixture>, IAsyncLifetime
{
    private readonly ClaimServiceDbFixture _fixture;
    private readonly MatchReevaluationRepository _repository;

    public MatchReevaluationRepositoryTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
        _repository = new MatchReevaluationRepository(fixture.Connections, new());
    }

    /* ClaimNextAsync/GetActivePairsAsync are not scoped to one test's own item/event ids, so a leftover
       row from an earlier test in this shared container would otherwise be picked up here too. */
    public async Task InitializeAsync()
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        foreach (var table in new[]
        {
            "match_reevaluation_jobs", "matching_item_snapshots", "match_notifications",
            "match_actions", "matches", "image_descriptions"
        })
        {
            await using var command = new MySqlCommand($"DELETE FROM {table};", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static ItemReport MakeReport(
        Guid id, Guid userId, string title = "t", string category = "c",
        string description = "d", string status = "ACTIVE", List<string>? photoUrls = null) => new()
    {
        Id = id,
        UserId = userId,
        Title = title,
        Category = category,
        Description = description,
        Status = status,
        DateLost = "2026-09-01",
        LastKnownLocation = "l",
        PhotoUrls = photoUrls ?? []
    };

    private async Task<Guid> InsertMatchAsync(
        string status, Guid lostItemId, Guid foundItemId, Guid lostReporterId, Guid finderId,
        bool isActive = true)
    {
        var id = Guid.NewGuid();
        var snapshot = """{"id":"11111111-1111-1111-1111-111111111111","type":"LOST","title":"t","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @lostReporterId,
                'LOST', @status, @isActive, 75.00, 'text-v1',
                @snapshot, @snapshot, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);
        command.Parameters.AddWithValue("@lostReporterId", lostReporterId);
        command.Parameters.AddWithValue("@finderId", finderId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@isActive", isActive);
        command.Parameters.AddWithValue("@snapshot", snapshot);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task InsertNotificationAsync(Guid matchId, string status)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO match_notifications (
                id, match_id, notification_type, recipient_user_id, status,
                attempts, next_attempt_at, created_at, updated_at
            ) VALUES (
                @id, @matchId, 'COUNTERPART_ACTION', @userId, @status,
                0, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@matchId", matchId);
        command.Parameters.AddWithValue("@userId", Guid.NewGuid());
        command.Parameters.AddWithValue("@status", status);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertImageDescriptionAsync(
        Guid itemId, string itemType, string photoUrl, string processingStatus,
        string? description = null)
    {
        var (_, photoKey) = new BlobUrlPhotoKeyGenerator().Create(photoUrl);
        var now = DateTime.UtcNow;

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO image_descriptions (
                id, source_event_id, source_event_type, source_occurred_at, photo_key, item_id,
                item_type, blob_url, description, attributes_json, processing_status, attempts,
                created_at, updated_at, is_superseded
            ) VALUES (
                @id, @sourceEventId, 'CREATED', @now, @photoKey, @itemId, @itemType, @blobUrl,
                @description, NULL, @status, 1, @now, @now, 0
            );
            """, connection);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@sourceEventId", Guid.NewGuid());
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@photoKey", photoKey);
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@itemType", itemType);
        command.Parameters.AddWithValue("@blobUrl", photoUrl);
        command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", processingStatus);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(bool IsActive, string? DeactivationReason, Guid? DeactivatedItemId,
        string? DeactivatedItemType, decimal Score)> ReadMatchAsync(Guid matchId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            SELECT is_active, deactivation_reason, deactivated_item_id, deactivated_item_type,
                   confidence_score
            FROM matches WHERE id = @id;
            """, connection);
        command.Parameters.AddWithValue("@id", matchId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetDecimal(4));
    }

    private async Task<string> ReadNotificationStatusAsync(Guid matchId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status FROM match_notifications WHERE match_id = @matchId LIMIT 1;", connection);
        command.Parameters.AddWithValue("@matchId", matchId);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(string Status, int Attempts, DateTime? NextAttemptAt, string? ErrorCode)>
        ReadJobAsync(Guid eventId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            SELECT status, attempts, next_attempt_at, error_code
            FROM match_reevaluation_jobs WHERE event_id = @eventId;
            """, connection);
        command.Parameters.AddWithValue("@eventId", eventId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    // ---- RecordEventAsync -----------------------------------------------------------

    [Fact]
    public async Task RecordEventAsync_UpdateEvent_CreatesSnapshotAndReevaluationJob()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);

        var job = await ReadJobAsync(eventId);
        Assert.Equal("PENDING", job.Status);
    }

    [Fact]
    public async Task RecordEventAsync_CreatedEvent_RecordsSnapshotButCreatesNoJob()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, false,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM match_reevaluation_jobs WHERE event_id = @eventId;", connection);
        command.Parameters.AddWithValue("@eventId", eventId);

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RecordEventAsync_SameEventIdRedelivered_IsANoOpTheSecondTime()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow;

        var itemEvent = new MatchItemSnapshotEvent(eventId, "LOST", itemId, occurredAt, true,
            MakeReport(itemId, Guid.NewGuid()));

        await _repository.RecordEventAsync(itemEvent, CancellationToken.None);
        await _repository.RecordEventAsync(itemEvent, CancellationToken.None);

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM match_reevaluation_jobs WHERE item_id = @itemId;", connection);
        command.Parameters.AddWithValue("@itemId", itemId);

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RecordEventAsync_OlderEventArrivesAfterNewer_IsIgnored()
    {
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", itemId, now, true,
                MakeReport(itemId, userId, title: "newer")),
            CancellationToken.None);

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", itemId, now.AddSeconds(-30), true,
                MakeReport(itemId, userId, title: "older")),
            CancellationToken.None);

        var report = await _repository.GetLatestReportAsync(
            "LOST", itemId, userId, "{}", CancellationToken.None);

        Assert.Equal("newer", report.Title);
    }

    [Fact]
    public async Task RecordEventAsync_NewerEventArrives_OverwritesTheSnapshot()
    {
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", itemId, now, true,
                MakeReport(itemId, userId, title: "old")),
            CancellationToken.None);

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", itemId, now.AddSeconds(30), true,
                MakeReport(itemId, userId, title: "new")),
            CancellationToken.None);

        var report = await _repository.GetLatestReportAsync(
            "LOST", itemId, userId, "{}", CancellationToken.None);

        Assert.Equal("new", report.Title);
    }

    // ---- ClaimNextAsync ---------------------------------------------------------------

    [Fact]
    public async Task ClaimNextAsync_NoDueRows_ReturnsNull()
    {
        Assert.Null(await _repository.ClaimNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClaimNextAsync_DuePendingRow_ClaimsItAndIncrementsAttempts()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);

        var job = await _repository.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(eventId, job!.EventId);
        Assert.Equal(1, job.Attempts);

        var row = await ReadJobAsync(eventId);
        Assert.Equal("PROCESSING", row.Status);
    }

    [Fact]
    public async Task ClaimNextAsync_RowAlreadyLeasedByAnotherWorker_IsNotClaimed()
    {
        var itemId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);

        await _repository.ClaimNextAsync(CancellationToken.None);
        Assert.Null(await _repository.ClaimNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClaimNextAsync_MultipleDueRows_ClaimsOldestFirst()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(newer, "LOST", itemB, DateTime.UtcNow, true,
                MakeReport(itemB, Guid.NewGuid())),
            CancellationToken.None);

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(older, "LOST", itemA, DateTime.UtcNow, true,
                MakeReport(itemA, Guid.NewGuid())),
            CancellationToken.None);

        // Force a distinguishable created_at ordering (both events above land within the same tick).
        await using (var connection = _fixture.Connections.Create())
        {
            await connection.OpenAsync();
            await using var command = new MySqlCommand("""
                UPDATE match_reevaluation_jobs SET created_at = TIMESTAMPADD(MINUTE, -5, created_at)
                WHERE event_id = @eventId;
                """, connection);
            command.Parameters.AddWithValue("@eventId", older);
            await command.ExecuteNonQueryAsync();
        }

        var job = await _repository.ClaimNextAsync(CancellationToken.None);
        Assert.Equal(older, job!.EventId);
    }

    [Fact]
    public async Task ClaimNextAsync_ExpiredLeaseAtMaxAttempts_IsMarkedFailedAndNeverClaimedAgain()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);

        await using (var connection = _fixture.Connections.Create())
        {
            await connection.OpenAsync();
            await using var command = new MySqlCommand("""
                UPDATE match_reevaluation_jobs
                SET status = 'PROCESSING', attempts = 5,
                    lease_expires_at = TIMESTAMPADD(MINUTE, -1, UTC_TIMESTAMP(3))
                WHERE event_id = @eventId;
                """, connection);
            command.Parameters.AddWithValue("@eventId", eventId);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await _repository.ClaimNextAsync(CancellationToken.None));

        var row = await ReadJobAsync(eventId);
        Assert.Equal("FAILED", row.Status);
        Assert.Equal("WORKER_LEASE_EXPIRED", row.ErrorCode);
    }

    // ---- IsCurrentAsync ---------------------------------------------------------------

    [Fact]
    public async Task IsCurrentAsync_JobMatchesTheLatestSnapshot_ReturnsTrue()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);

        var job = await _repository.ClaimNextAsync(CancellationToken.None);
        Assert.True(await _repository.IsCurrentAsync(job!, CancellationToken.None));
    }

    [Fact]
    public async Task IsCurrentAsync_ANewerUpdateArrivedSince_ReturnsFalse()
    {
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, userId)),
            CancellationToken.None);

        var job = await _repository.ClaimNextAsync(CancellationToken.None);

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", itemId, DateTime.UtcNow.AddSeconds(30), true,
                MakeReport(itemId, userId)),
            CancellationToken.None);

        Assert.False(await _repository.IsCurrentAsync(job!, CancellationToken.None));
    }

    // ---- GetActivePairsAsync -----------------------------------------------------------

    [Theory]
    [InlineData("LOST_REPORTER_CONFIRMED")]
    [InlineData("FINDER_CONFIRMED")]
    public async Task GetActivePairsAsync_HalfConfirmedMatch_IsReturned(string status)
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(status, lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid());

        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", lostItemId, DateTime.UtcNow, true,
                MakeReport(lostItemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;

        var pairs = await _repository.GetActivePairsAsync(job, CancellationToken.None);

        Assert.Single(pairs);
        Assert.Equal(matchId, pairs[0].MatchId);
    }

    // Scenario 4: a Confirmed match is never re-scored by this process.
    [Theory]
    [InlineData("CONFIRMED")]
    [InlineData("REJECTED")]
    [InlineData("AUTO_REJECTED_LOW_CONFIDENCE")]
    public async Task GetActivePairsAsync_MatchNotHalfConfirmed_IsExcluded(string status)
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        await InsertMatchAsync(status, lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid());

        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", lostItemId, DateTime.UtcNow, true,
                MakeReport(lostItemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;

        Assert.Empty(await _repository.GetActivePairsAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task GetActivePairsAsync_AlreadyDeactivatedMatch_IsExcluded()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        await InsertMatchAsync(
            "LOST_REPORTER_CONFIRMED", lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid(),
            isActive: false);

        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", lostItemId, DateTime.UtcNow, true,
                MakeReport(lostItemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;

        Assert.Empty(await _repository.GetActivePairsAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task GetActivePairsAsync_UpdatedItemIsOnTheFoundSide_StillMatches()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "FINDER_CONFIRMED", lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid());

        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "FOUND", foundItemId, DateTime.UtcNow, true,
                MakeReport(foundItemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;

        var pairs = await _repository.GetActivePairsAsync(job, CancellationToken.None);
        Assert.Equal(matchId, Assert.Single(pairs).MatchId);
    }

    // ---- GetLatestReportAsync -----------------------------------------------------------

    [Fact]
    public async Task GetLatestReportAsync_NoSnapshotRecorded_FallsBackToTheMatchesOwnSnapshot()
    {
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var fallback = $$"""{"id":"{{itemId}}","type":"LOST","title":"fallback title","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        var report = await _repository.GetLatestReportAsync("LOST", itemId, userId, fallback, CancellationToken.None);

        Assert.Equal("fallback title", report.Title);
        Assert.Equal("ACTIVE", report.Status);
    }

    [Fact]
    public async Task GetLatestReportAsync_OnlyACreatedSnapshotExists_FallsBackToTheMatchesOwnSnapshot()
    {
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", itemId, DateTime.UtcNow, false,
                MakeReport(itemId, userId, title: "created snapshot")),
            CancellationToken.None);

        var fallback = $$"""{"id":"{{itemId}}","type":"LOST","title":"fallback title","category":"c","description":"d","date":"2026-09-01","location":"l"}""";
        var report = await _repository.GetLatestReportAsync("LOST", itemId, userId, fallback, CancellationToken.None);

        Assert.Equal("fallback title", report.Title);
    }

    [Fact]
    public async Task GetLatestReportAsync_AnUpdatedSnapshotExists_ReturnsItsLatestFields()
    {
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, userId, title: "updated title")),
            CancellationToken.None);

        var report = await _repository.GetLatestReportAsync("LOST", itemId, userId, "{}", CancellationToken.None);
        Assert.Equal("updated title", report.Title);
    }

    // ---- GetPhotoReadinessAsync -----------------------------------------------------------

    [Fact]
    public async Task GetPhotoReadinessAsync_NoPhotos_IsReady()
    {
        var report = MakeReport(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(PhotoReadiness.Ready, await _repository.GetPhotoReadinessAsync(report, "LOST", CancellationToken.None));
    }

    [Fact]
    public async Task GetPhotoReadinessAsync_CompletedDescriptionWithText_IsReady()
    {
        var itemId = Guid.NewGuid();
        const string url = "https://blob.example.com/ready.jpg";
        await InsertImageDescriptionAsync(itemId, "LOST", url, "COMPLETED", description: "A red bag.");

        var report = MakeReport(itemId, Guid.NewGuid(), photoUrls: [url]);
        Assert.Equal(PhotoReadiness.Ready, await _repository.GetPhotoReadinessAsync(report, "LOST", CancellationToken.None));
    }

    [Fact]
    public async Task GetPhotoReadinessAsync_NoImageDescriptionRowYet_IsPending()
    {
        var report = MakeReport(Guid.NewGuid(), Guid.NewGuid(), photoUrls: ["https://blob.example.com/none.jpg"]);
        Assert.Equal(PhotoReadiness.Pending, await _repository.GetPhotoReadinessAsync(report, "LOST", CancellationToken.None));
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("PROCESSING")]
    public async Task GetPhotoReadinessAsync_StillBeingAnalysed_IsPending(string status)
    {
        var itemId = Guid.NewGuid();
        var url = $"https://blob.example.com/{Guid.NewGuid()}.jpg";
        await InsertImageDescriptionAsync(itemId, "LOST", url, status);

        var report = MakeReport(itemId, Guid.NewGuid(), photoUrls: [url]);
        Assert.Equal(PhotoReadiness.Pending, await _repository.GetPhotoReadinessAsync(report, "LOST", CancellationToken.None));
    }

    [Fact]
    public async Task GetPhotoReadinessAsync_AnalysisFailed_IsFailed()
    {
        var itemId = Guid.NewGuid();
        const string url = "https://blob.example.com/failed.jpg";
        await InsertImageDescriptionAsync(itemId, "LOST", url, "FAILED");

        var report = MakeReport(itemId, Guid.NewGuid(), photoUrls: [url]);
        Assert.Equal(PhotoReadiness.Failed, await _repository.GetPhotoReadinessAsync(report, "LOST", CancellationToken.None));
    }

    [Fact]
    public async Task GetPhotoReadinessAsync_MoreThanOnePhoto_IsFailed()
    {
        var report = MakeReport(Guid.NewGuid(), Guid.NewGuid(),
            photoUrls: ["https://blob.example.com/a.jpg", "https://blob.example.com/b.jpg"]);
        Assert.Equal(PhotoReadiness.Failed, await _repository.GetPhotoReadinessAsync(report, "LOST", CancellationToken.None));
    }

    // ---- ApplyScoreAsync -----------------------------------------------------------

    /* Scenario 2: a re-scored active match that now falls below the threshold is deactivated,
       with a timestamp, and not hard-deleted. */
    [Fact]
    public async Task ApplyScoreAsync_ScoreBelowThreshold_DeactivatesWithReasonAndAuditFields()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "LOST_REPORTER_CONFIRMED", lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid());

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", lostItemId, DateTime.UtcNow, true,
                MakeReport(lostItemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;
        var pair = Assert.Single(await _repository.GetActivePairsAsync(job, CancellationToken.None));

        await _repository.ApplyScoreAsync(
            job, pair, MakeReport(lostItemId, Guid.NewGuid()), MakeReport(foundItemId, Guid.NewGuid()),
            45m, CancellationToken.None);

        var row = await ReadMatchAsync(matchId);
        Assert.False(row.IsActive);
        Assert.Equal("ITEM_UPDATED_NO_LONGER_MATCHES", row.DeactivationReason);
        Assert.Equal(lostItemId, row.DeactivatedItemId);
        Assert.Equal("LOST", row.DeactivatedItemType);
        Assert.Equal(45m, row.Score);
    }

    /* Scenario 3: a re-scored active match that stays at or above the threshold is left at its
       current status - only its score/snapshots are refreshed to the latest values. */
    [Fact]
    public async Task ApplyScoreAsync_ScoreAtOrAboveThreshold_LeavesStatusAndActiveFlagUntouched()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "FINDER_CONFIRMED", lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid());

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "FOUND", foundItemId, DateTime.UtcNow, true,
                MakeReport(foundItemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;
        var pair = Assert.Single(await _repository.GetActivePairsAsync(job, CancellationToken.None));

        await _repository.ApplyScoreAsync(
            job, pair, MakeReport(lostItemId, Guid.NewGuid()), MakeReport(foundItemId, Guid.NewGuid()),
            82m, CancellationToken.None);

        var row = await ReadMatchAsync(matchId);
        Assert.True(row.IsActive);
        Assert.Null(row.DeactivationReason);
        Assert.Equal(82m, row.Score);
    }

    [Fact]
    public async Task ApplyScoreAsync_BelowThreshold_CancelsPendingAndFailedNotifications()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "LOST_REPORTER_CONFIRMED", lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid());
        await InsertNotificationAsync(matchId, "PENDING");

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", lostItemId, DateTime.UtcNow, true,
                MakeReport(lostItemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;
        var pair = Assert.Single(await _repository.GetActivePairsAsync(job, CancellationToken.None));

        await _repository.ApplyScoreAsync(
            job, pair, MakeReport(lostItemId, Guid.NewGuid()), MakeReport(foundItemId, Guid.NewGuid()),
            10m, CancellationToken.None);

        Assert.Equal("CANCELLED", await ReadNotificationStatusAsync(matchId));
    }

    [Fact]
    public async Task ApplyScoreAsync_StaleLease_DoesNotTouchTheMatch()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "LOST_REPORTER_CONFIRMED", lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid());

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", lostItemId, DateTime.UtcNow, true,
                MakeReport(lostItemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;
        var pair = Assert.Single(await _repository.GetActivePairsAsync(job, CancellationToken.None));

        var staleJob = job with { LeaseToken = Guid.NewGuid() };

        await _repository.ApplyScoreAsync(
            staleJob, pair, MakeReport(lostItemId, Guid.NewGuid()), MakeReport(foundItemId, Guid.NewGuid()),
            10m, CancellationToken.None);

        var row = await ReadMatchAsync(matchId);
        Assert.True(row.IsActive);
        Assert.Equal(75.00m, row.Score);
    }

    [Fact]
    public async Task ApplyScoreAsync_ANewerUpdateArrivedMidProcessing_DoesNotTouchTheMatch()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "LOST_REPORTER_CONFIRMED", lostItemId, foundItemId, Guid.NewGuid(), Guid.NewGuid());

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", lostItemId, DateTime.UtcNow, true,
                MakeReport(lostItemId, userId)),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;
        var pair = Assert.Single(await _repository.GetActivePairsAsync(job, CancellationToken.None));

        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(Guid.NewGuid(), "LOST", lostItemId, DateTime.UtcNow.AddSeconds(30), true,
                MakeReport(lostItemId, userId)),
            CancellationToken.None);

        await _repository.ApplyScoreAsync(
            job, pair, MakeReport(lostItemId, userId), MakeReport(foundItemId, Guid.NewGuid()),
            10m, CancellationToken.None);

        var row = await ReadMatchAsync(matchId);
        Assert.True(row.IsActive);
    }

    // ---- Defer/Fail/Complete -----------------------------------------------------------

    [Fact]
    public async Task DeferAsync_SetsPendingWithAShortRetryAndRestoresTheAttempt()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;

        await _repository.DeferAsync(job, CancellationToken.None);

        var row = await ReadJobAsync(eventId);
        Assert.Equal("PENDING", row.Status);
        Assert.Equal("IMAGE_DESCRIPTION_PENDING", row.ErrorCode);
        Assert.Equal(0, row.Attempts);
        Assert.NotNull(row.NextAttemptAt);
    }

    [Fact]
    public async Task FailAsync_RetryableBelowMaxAttempts_StaysPendingWithoutRestoringTheAttempt()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;

        await _repository.FailAsync(job, "IMAGE_DESCRIPTION_UNAVAILABLE", retryable: true, CancellationToken.None);

        var row = await ReadJobAsync(eventId);
        Assert.Equal("PENDING", row.Status);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task FailAsync_NonRetryable_IsFailedImmediately()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;

        await _repository.FailAsync(job, "IMAGE_DESCRIPTION_FAILED", retryable: false, CancellationToken.None);

        var row = await ReadJobAsync(eventId);
        Assert.Equal("FAILED", row.Status);
        Assert.Null(row.NextAttemptAt);
    }

    [Fact]
    public async Task CompleteAsync_SetsCompleted()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", itemId, DateTime.UtcNow, true,
                MakeReport(itemId, Guid.NewGuid())),
            CancellationToken.None);
        var job = (await _repository.ClaimNextAsync(CancellationToken.None))!;

        await _repository.CompleteAsync(job, CancellationToken.None);

        Assert.Equal("COMPLETED", (await ReadJobAsync(eventId)).Status);
    }
}
