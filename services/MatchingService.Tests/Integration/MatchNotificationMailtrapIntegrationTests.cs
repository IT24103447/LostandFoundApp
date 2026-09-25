using MatchingService.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story 6 (email notifications). The one real-SMTP test in this pipeline: a real match confirmed in
/// real, disposable MySQL (Testcontainers, via ClaimServiceDbFixture) is discovered, claimed, and
/// delivered by the REAL MatchEmailSender/NotificationDeliveryService/NotificationRepository - no fakes
/// anywhere - over real SMTP to a real Mailtrap sandbox, then verified by reading the actually-captured
/// message back out via Mailtrap's own REST API (MailtrapVerificationClient). This is the one thing the
/// fake-swap tests (NotificationDeliveryServiceTests) structurally cannot prove: that a real subject and
/// a real matched-items link actually survive a real SMTP round trip.
///
/// Self-skips (a vacuous pass, not a failure) whenever the required config/credentials aren't present -
/// this always runs locally (Smtp:* comes from MatchingService's own user-secrets, the same ones the
/// real app uses; MAILTRAP_API_TOKEN/ACCOUNT_ID/INBOX_ID are plain env vars) and always skips in CI,
/// where neither exists (see matching-service-ci-cd.yml - no Smtp:*/Mailtrap secrets are wired in
/// anywhere). Deliberately kept that way: a real third-party network call is inherently
/// non-deterministic, and this project's own precedent (AuthService's Mailtrap-backed Selenium tests)
/// already keeps real-SMTP checks local-only rather than gating CI on them.
/// </summary>
public sealed class MatchNotificationMailtrapIntegrationTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;
    private readonly MailtrapVerificationClient _mailtrap = new();

    public MatchNotificationMailtrapIntegrationTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "MatchingService.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration BuildRealConfiguration()
    {
        // The same user-secrets store the real MatchingService app reads (AddUserSecrets<Program>
        // points at its UserSecretsId, "matching-service-local-development"), so setting Smtp:* once
        // via `dotnet user-secrets set ... --project services/MatchingService` is picked up here too -
        // no separate test-only config to maintain. Environment variables layer on top, so CI secrets
        // (if ever added - see the class doc comment) would work with zero code changes.
        return new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private static bool SmtpIsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Smtp:Host"]) &&
        !string.IsNullOrWhiteSpace(configuration["Smtp:User"]) &&
        !string.IsNullOrWhiteSpace(configuration["Smtp:Password"]) &&
        !string.IsNullOrWhiteSpace(configuration["Smtp:FromAddress"]) &&
        !string.IsNullOrWhiteSpace(configuration["Frontend:BaseUrl"]);

    private async Task<(Guid MatchId, Guid LostReporterId, string LostReporterEmail, Guid FinderId, string FinderEmail)>
        InsertConfirmedMatchWithBothContactsAsync()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        // Scenario 3 notifies BOTH parties on Confirmed, and ClaimNextAsync claims whichever of the
        // two resulting rows sorts first (both share the same created_at, inserted by the same
        // INSERT...SELECT) - so both need a real contact row, not just one, or whichever gets claimed
        // first non-deterministically fails with RECIPIENT_EMAIL_UNAVAILABLE.
        var lostReporterEmail = $"story6-test-lost-{Guid.NewGuid():N}@example.com";
        var finderEmail = $"story6-test-found-{Guid.NewGuid():N}@example.com";
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

        foreach (var (userId, email) in new[] { (lostReporterId, lostReporterEmail), (finderId, finderEmail) })
        {
            await using var command = new MySqlCommand("""
                INSERT INTO notification_contacts (
                    user_id, email, is_active, is_deleted, last_event_at, updated_at
                ) VALUES (
                    @userId, @email, 1, 0, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
                );
                """, connection);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@email", email);
            await command.ExecuteNonQueryAsync();
        }

        return (matchId, lostReporterId, lostReporterEmail, finderId, finderEmail);
    }

    [Fact]
    public async Task RealConfirmedMatch_IsDeliveredByTheRealSenderAndArrivesInMailtrapWithTheCorrectContent()
    {
        var configuration = BuildRealConfiguration();

        if (!SmtpIsConfigured(configuration) || !_mailtrap.IsConfigured)
        {
            // No real SMTP config and/or no Mailtrap REST credentials - this test can't prove
            // anything about a real send without them, so it skips itself rather than failing the
            // whole suite. See the class doc comment: this always skips in CI by design.
            return;
        }

        var (matchId, lostReporterId, lostReporterEmail, finderId, finderEmail) =
            await InsertConfirmedMatchWithBothContactsAsync();

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);

        var repository = new NotificationRepository(_fixture.Connections);
        var job = await repository.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(matchId, job!.MatchId);

        // Scenario 3 queues a row for both parties with identical created_at - whichever one
        // ClaimNextAsync happens to claim first is the one this run actually verifies.
        var recipientEmail = job.RecipientUserId == lostReporterId ? lostReporterEmail : finderEmail;
        Assert.True(
            job.RecipientUserId == lostReporterId || job.RecipientUserId == finderId,
            "Claimed job's recipient was neither party of the match it belongs to.");

        var sender = new MatchEmailSender(configuration, new StubHostEnvironment());
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        await delivery.DeliverAsync(job, CancellationToken.None);

        await using (var diagnosticConnection = _fixture.Connections.Create())
        {
            await diagnosticConnection.OpenAsync();
            await using var diagnosticCommand = new MySqlCommand(
                "SELECT status, error_code FROM match_notifications WHERE id = @id;", diagnosticConnection);
            diagnosticCommand.Parameters.AddWithValue("@id", job.Id);
            await using var reader = await diagnosticCommand.ExecuteReaderAsync();
            await reader.ReadAsync();
            var diagnosticStatus = reader.GetString(0);
            var diagnosticErrorCode = reader.IsDBNull(1) ? "(none)" : reader.GetString(1);
            Assert.True(
                diagnosticStatus == "SENT",
                $"DIAGNOSTIC: delivery status was '{diagnosticStatus}', error_code '{diagnosticErrorCode}' - the send itself did not succeed.");
        }

        var (subject, body) = await _mailtrap.WaitForMessageAsync(recipientEmail, TimeSpan.FromSeconds(30));

        Assert.Equal("Your match was confirmed", subject);
        Assert.Contains($"matched-items/{matchId:D}", body, StringComparison.Ordinal);
        // DoD: no contact information, no hidden matching information - only the app link.
        Assert.DoesNotContain("@", body.Replace($"matched-items/{matchId:D}", ""), StringComparison.Ordinal);

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();
        await using var statusCommand = new MySqlCommand(
            "SELECT status FROM match_notifications WHERE id = @id;", connection);
        statusCommand.Parameters.AddWithValue("@id", job.Id);
        Assert.Equal("SENT", (string)(await statusCommand.ExecuteScalarAsync())!);
    }
}
