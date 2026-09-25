using MatchingService.Databases;
using MatchingService.Notifications;
using MatchingService.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Notifications;

/// <summary>
/// Story 6 (email notifications). Real, disposable MySQL (Testcontainers, via ClaimServiceDbFixture) and
/// the REAL NotificationDiscoveryService/NotificationRepository/NotificationDeliveryService - only
/// IMatchEmailSender is faked (the same CI-safe boundary NotificationDeliveryServiceTests already uses),
/// but here they're driven through the real MatchNotificationWorker itself via StartAsync/StopAsync, the
/// same pattern already used for MatchConfirmationPublisher in MatchConfirmationKafkaIntegrationTests.
/// This is what closes the 0%-covered gap in the worker's own orchestration loop (the 15-second discovery
/// cadence, the claim-or-wait polling, the outer catch-and-continue) that the three dependencies' own
/// individual test files structurally cannot prove on their own.
/// </summary>
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

    /// <summary>Wraps the real connection factory but throws on the first N calls to Create() - used to
    /// force a genuine exception out of a real dependency (NotificationDiscoveryService.DiscoverAsync,
    /// whose first line calls connections.Create()), the only way to reach the worker's own
    /// catch-and-continue branch without mocking the worker's dependencies themselves.</summary>
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

            // Both parties' notifications were discovered, claimed and delivered by the real worker
            // loop itself - not by calling DiscoverAsync/ClaimNextAsync/DeliverAsync directly.
            Assert.Equal(2, await CountSentAsync(matchId));
            Assert.Equal(2, sender.Sent.Count);
        }
        finally
        {
            // Both rows are already SENT, so the worker is now sitting in its "queue empty" branch,
            // either about to or already inside its 5-second wait - StopAsync cancels that wait
            // immediately, exercising the graceful mid-delay shutdown path too.
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
            // The worker's own catch(Exception)-log-and-continue branch, plus its 5-second recovery
            // delay before the retry, so this genuinely needs a real wait past that window.
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
