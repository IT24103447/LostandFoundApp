using MatchingService.Claims;
using MatchingService.Reevaluation;
using MatchingService.Tests.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Reevaluation;

// Story 8 tests driving the real MatchReevaluationWorker via StartAsync/StopAsync against real MySQL, proving the worker's own claim-process-complete loop.
[Collection("Docker Integration Tests 10")]
public sealed class MatchReevaluationWorkerTests : IClassFixture<ClaimServiceDbFixture>, IAsyncLifetime
{
    private readonly ClaimServiceDbFixture _fixture;
    private readonly MatchReevaluationRepository _repository;

    public MatchReevaluationWorkerTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
        _repository = new MatchReevaluationRepository(fixture.Connections, new());
    }

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

    private static string Snapshot(Guid id, string type, string title, string category, string description) =>
        $$"""{"id":"{{id}}","type":"{{type}}","title":"{{title}}","category":"{{category}}","description":"{{description}}","date":"2026-09-01","location":"l"}""";

    private async Task<Guid> InsertMatchAsync(
        string status, Guid lostItemId, Guid foundItemId, Guid lostReporterId,
        string lostSnapshot, string foundSnapshot)
    {
        var id = Guid.NewGuid();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @lostReporterId,
                'LOST', @status, 1, 75.00, 'text-v1',
                @lostSnapshot, @foundSnapshot, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);
        command.Parameters.AddWithValue("@lostReporterId", lostReporterId);
        command.Parameters.AddWithValue("@finderId", Guid.NewGuid());
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@lostSnapshot", lostSnapshot);
        command.Parameters.AddWithValue("@foundSnapshot", foundSnapshot);

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

    private async Task<(bool IsActive, string Status, string? DeactivationReason, decimal Score)>
        ReadMatchAsync(Guid matchId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            SELECT is_active, status, deactivation_reason, confidence_score FROM matches WHERE id = @id;
            """, connection);
        command.Parameters.AddWithValue("@id", matchId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetBoolean(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetDecimal(3));
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

    private MatchReevaluationWorker CreateWorker()
    {
        // Never actually called by ScoreExistingAsync; just satisfies ClaimService's constructor.
        var services = new ServiceCollection()
            .AddScoped(_ => new ClaimItemClient(
                new HttpClient { BaseAddress = new Uri("https://item-service.invalid") },
                new HttpContextAccessor()))
            .AddScoped(_ => new ClaimRepository(_fixture.Connections, new()))
            .AddScoped<ClaimService>()
            .BuildServiceProvider();

        return new MatchReevaluationWorker(
            _repository, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MatchReevaluationWorker>.Instance);
    }

    private async Task WaitForJobToCompleteAsync(Guid eventId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            await using var connection = _fixture.Connections.Create();
            await connection.OpenAsync();

            await using var command = new MySqlCommand(
                "SELECT status FROM match_reevaluation_jobs WHERE event_id = @eventId;", connection);
            command.Parameters.AddWithValue("@eventId", eventId);

            if ((string)(await command.ExecuteScalarAsync())! == "COMPLETED") return;
            await Task.Delay(200);
        }

        throw new TimeoutException("The re-evaluation job never completed.");
    }

    // Scenario 1 + Scenario 2: an item update that drops the score below 60 deactivates the match.
    [Fact]
    public async Task RealWorker_UpdateDropsScoreBelowThreshold_DeactivatesTheMatch()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var lostReporterId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "LOST_REPORTER_CONFIRMED", lostItemId, foundItemId, lostReporterId, "{}",
            Snapshot(foundItemId, "FOUND", "Brown leather wallet", "Accessories",
                "A worn brown leather wallet with a gold clasp"));
        await InsertNotificationAsync(matchId, "PENDING");

        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", lostItemId, DateTime.UtcNow, true, new ItemReport
            {
                Id = lostItemId,
                UserId = lostReporterId,
                Title = "Blue plastic umbrella",
                Category = "Other",
                Description = "A large blue golf umbrella missing two ribs",
                Status = "ACTIVE",
                DateLost = "2026-09-01",
                LastKnownLocation = "l"
            }),
            CancellationToken.None);

        var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForJobToCompleteAsync(eventId);

            var row = await ReadMatchAsync(matchId);
            Assert.False(row.IsActive);
            Assert.Equal("ITEM_UPDATED_NO_LONGER_MATCHES", row.DeactivationReason);
            Assert.True(row.Score < ClaimService.Threshold);
            Assert.Equal("CANCELLED", await ReadNotificationStatusAsync(matchId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /* Scenario 1 + Scenario 3: an item update that keeps the score at or above 60 leaves the match's
       status and active flag untouched, only refreshing its score. */
    [Fact]
    public async Task RealWorker_UpdateKeepsScoreAtOrAboveThreshold_LeavesTheMatchAtItsCurrentStatus()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var lostReporterId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "FINDER_CONFIRMED", lostItemId, foundItemId, lostReporterId, "{}",
            Snapshot(foundItemId, "FOUND", "Brown leather wallet", "Accessories",
                "A worn brown leather wallet with a gold clasp"));

        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", lostItemId, DateTime.UtcNow, true, new ItemReport
            {
                Id = lostItemId,
                UserId = lostReporterId,
                Title = "Brown leather wallet",
                Category = "Accessories",
                Description = "A worn brown leather wallet with a gold clasp",
                Status = "ACTIVE",
                DateLost = "2026-09-01",
                LastKnownLocation = "l"
            }),
            CancellationToken.None);

        var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForJobToCompleteAsync(eventId);

            var row = await ReadMatchAsync(matchId);
            Assert.True(row.IsActive);
            Assert.Equal("FINDER_CONFIRMED", row.Status);
            Assert.True(row.Score >= ClaimService.Threshold);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // Scenario 4: a Confirmed match is never re-scored or deactivated by this process.
    [Fact]
    public async Task RealWorker_ConfirmedMatch_IsNeverReScoredOrTouched()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var lostReporterId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", lostItemId, foundItemId, lostReporterId, "{}",
            Snapshot(foundItemId, "FOUND", "Brown leather wallet", "Accessories",
                "A worn brown leather wallet with a gold clasp"));

        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", lostItemId, DateTime.UtcNow, true, new ItemReport
            {
                Id = lostItemId,
                UserId = lostReporterId,
                Title = "Blue plastic umbrella",
                Category = "Other",
                Description = "A large blue golf umbrella missing two ribs",
                Status = "ACTIVE",
                DateLost = "2026-09-01",
                LastKnownLocation = "l"
            }),
            CancellationToken.None);

        var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForJobToCompleteAsync(eventId);

            var row = await ReadMatchAsync(matchId);
            Assert.True(row.IsActive);
            Assert.Equal("CONFIRMED", row.Status);
            Assert.Equal(75.00m, row.Score);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // Scenario 1: one item update can affect multiple active pairs, each re-scored independently.
    [Fact]
    public async Task RealWorker_TwoActivePairsForTheSameItem_ReScoresBothIndependently()
    {
        var lostItemId = Guid.NewGuid();
        var lostReporterId = Guid.NewGuid();
        var closeMatchFoundId = Guid.NewGuid();
        var farMatchFoundId = Guid.NewGuid();

        var closeMatchId = await InsertMatchAsync(
            "LOST_REPORTER_CONFIRMED", lostItemId, closeMatchFoundId, lostReporterId, "{}",
            Snapshot(closeMatchFoundId, "FOUND", "Blue plastic umbrella", "Other",
                "A large blue golf umbrella missing two ribs"));

        var farMatchId = await InsertMatchAsync(
            "FINDER_CONFIRMED", lostItemId, farMatchFoundId, lostReporterId, "{}",
            Snapshot(farMatchFoundId, "FOUND", "Brown leather wallet", "Accessories",
                "A worn brown leather wallet with a gold clasp"));

        var eventId = Guid.NewGuid();
        await _repository.RecordEventAsync(
            new MatchItemSnapshotEvent(eventId, "LOST", lostItemId, DateTime.UtcNow, true, new ItemReport
            {
                Id = lostItemId,
                UserId = lostReporterId,
                Title = "Blue plastic umbrella",
                Category = "Other",
                Description = "A large blue golf umbrella missing two ribs",
                Status = "ACTIVE",
                DateLost = "2026-09-01",
                LastKnownLocation = "l"
            }),
            CancellationToken.None);

        var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForJobToCompleteAsync(eventId);

            var close = await ReadMatchAsync(closeMatchId);
            var far = await ReadMatchAsync(farMatchId);

            Assert.True(close.IsActive);
            Assert.False(far.IsActive);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
