using MatchingService.Databases;
using MatchingService.Notifications;
using MatchingService.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Notifications;

/// <summary>Story 6 tests driving the real MatchNotificationWorker via StartAsync/StopAsync against real MySQL — only IMatchEmailSender is faked. Closes the orchestration-loop gap (discovery cadence, claim-or-wait polling) the dependencies' own test files can't prove alone.</summary>
[Collection("Docker Integration Tests 7")]
public sealed class MatchNotificationWorkerTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;

    public MatchNotificationWorkerTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FakeMatchEmailSender : IMatchEmailSender
    {
        public List<(NotificationJob Job, string RecipientEmail)> Sent { get; } = new();

        public Task SendAsync(NotificationJob job, string recipientEmail, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add((job, recipientEmail));
            }
            return Task.CompletedTask;
        }
    }

    // Wraps the real connection factory but throws on the first N calls, to reach the worker's catch-and-continue branch without mocking its dependencies.
    private sealed class FailOnDemandConnectionFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public int RemainingFailures { get; set; }

        public MySqlConnection Create()
        {
            if (RemainingFailures > 0)
            {
                RemainingFailures--;
                throw new InvalidOperationException(
                    "Simulated connection failure for MatchNotificationWorker resilience test.");
            }

            return inner.Create();
        }
    }

    private async Task<(Guid MatchId, Guid LostReporterId, Guid FinderId)> InsertConfirmedMatchWithBothContactsAsync()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        var snapshot = """{"id":"11111111-1111-1111-1111-111111111111","type":"LOST","title":"t","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using (var command = new MySqlCommand("""
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @lostReporterId,
                'LOST', 'CONFIRMED', 1, 75.00, 'text-v1',
                @snapshot, @snapshot, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection))
        {
            command.Parameters.AddWithValue("@id", matchId);
            command.Parameters.AddWithValue("@lostItemId", Guid.NewGuid());
            command.Parameters.AddWithValue("@foundItemId", Guid.NewGuid());
            command.Parameters.AddWithValue("@lostReporterId", lostReporterId);
            command.Parameters.AddWithValue("@finderId", finderId);
            command.Parameters.AddWithValue("@snapshot", snapshot);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new MySqlCommand("""
            INSERT INTO match_actions (
                id, match_id, actor_user_id, actor_role, action, previous_status, new_status, created_at
            ) VALUES (
                @id, @matchId, @actorUserId, 'FOUND', 'CONFIRM', 'LOST_REPORTER_CONFIRMED', 'CONFIRMED', UTC_TIMESTAMP(3)
            );
            """, connection))
        {
            command.Parameters.AddWithValue("@id", Guid.NewGuid());
            command.Parameters.AddWithValue("@matchId", matchId);
            command.Parameters.AddWithValue("@actorUserId", finderId);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var userId in new[] { lostReporterId, finderId })
        {
            await using var command = new MySqlCommand("""
                INSERT INTO notification_contacts (
                    user_id, email, is_active, is_deleted, last_event_at, updated_at
                ) VALUES (
                    @userId, @email, 1, 0, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
                );
                """, connection);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@email", $"{userId:N}@example.com");
            await command.ExecuteNonQueryAsync();
        }

        return (matchId, lostReporterId, finderId);
    }

    private async Task<int> CountSentAsync(Guid matchId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM match_notifications WHERE match_id = @matchId AND status = 'SENT';",
            connection);
        command.Parameters.AddWithValue("@matchId", matchId);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RealWorker_ConfirmedMatch_DiscoversClaimsAndDeliversBothPartiesThenIdlesCleanly()
    {
        var (matchId, _, _) = await InsertConfirmedMatchWithBothContactsAsync();

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender();
        var delivery = new NotificationDeliveryService(repository, sender, NullLogger<NotificationDeliveryService>.Instance);
        var worker = new MatchNotificationWorker(
            discovery, repository, delivery, NullLogger<MatchNotificationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && await CountSentAsync(matchId) < 2)
            {
                await Task.Delay(200);
            }

            // Both parties were discovered, claimed and delivered by the real worker loop itself.
            Assert.Equal(2, await CountSentAsync(matchId));
            Assert.Equal(2, sender.Sent.Count);
        }
        finally
        {
            // The worker is now idling in its "queue empty" wait; StopAsync exercises the graceful mid-delay shutdown path too.
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealWorker_DiscoveryFailsOnce_LogsAndContinuesThenSucceedsOnTheNextPass()
    {
        var (matchId, _, _) = await InsertConfirmedMatchWithBothContactsAsync();

        var failingConnections = new FailOnDemandConnectionFactory(_fixture.Connections) { RemainingFailures = 1 };
        var discovery = new NotificationDiscoveryService(failingConnections);
        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender();
        var delivery = new NotificationDeliveryService(repository, sender, NullLogger<NotificationDeliveryService>.Instance);
        var worker = new MatchNotificationWorker(
            discovery, repository, delivery, NullLogger<MatchNotificationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Exercises the worker's catch-and-continue branch plus its recovery delay before retrying.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && await CountSentAsync(matchId) < 2)
            {
                await Task.Delay(500);
            }

            Assert.Equal(0, failingConnections.RemainingFailures);
            Assert.Equal(2, await CountSentAsync(matchId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
