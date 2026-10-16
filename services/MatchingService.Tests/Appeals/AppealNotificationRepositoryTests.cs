using MatchingService.Appeals;
using MatchingService.Databases;
using MatchingService.Tests.Integration;
using MySqlConnector;

namespace MatchingService.Tests.Appeals;

/// <summary>
/// Story LF-338 contract tests for AppealNotificationRepository against a real MySQL (Testcontainers),
/// following the exact shape of AppealServiceTests: plain classes constructed against the shared
/// ClaimServiceDbFixture, whose DbInitializer applies every app migration including 015.
///
/// The repository uses the real clock (TimeProvider.System), so deadlines are controlled from the
/// fixture side: a row is "due" when its next_attempt_at is in the past, "not yet due" when it is in
/// the future. The concurrency rule that matters for the Story LF-338 race DoD is asserted directly
/// here (ClaimNextAsync's optimistic token claim) and again end-to-end in the integration race test.
/// </summary>
[Collection("Docker Integration Tests 11")]
public sealed class AppealNotificationRepositoryTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;

    public AppealNotificationRepositoryTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    private AppealNotificationRepository BuildRepo() =>
        new(_fixture.Connections, TimeProvider.System);

    private static DateTime UtcNow() => DateTime.UtcNow;

    // ---- Seeding -------------------------------------------------------------------------------

    private async Task<Guid> SeedAppealAsync(
        Guid lostItemId,
        Guid foundItemId,
        Guid appellantId,
        string appellantEmail = "appellant@example.com",
        string status = "PENDING",
        string? rejectionReason = null)
    {
        var id = Guid.NewGuid();
        var now = UtcNow();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO match_appeals
                (id, lost_item_id, found_item_id, lost_reporter_id, finder_id, appellant_id,
                 appellant_role, appellant_email, appellant_phone, score, score_breakdown,
                 lost_snapshot, found_snapshot, rejection_reason, status, created_at, updated_at)
            VALUES
                (@id, @lost, @found, @reporter, @finder, @appellant, 'LOST', @email, '0712345678',
                 55.00, '{}', '{}', '{}', @reason, @status, @now, @now);
            """,
            connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lost", lostItemId);
        command.Parameters.AddWithValue("@found", foundItemId);
        command.Parameters.AddWithValue("@reporter", appellantId);
        command.Parameters.AddWithValue("@finder", appellantId);
        command.Parameters.AddWithValue("@appellant", appellantId);
        command.Parameters.AddWithValue("@email", appellantEmail);
        command.Parameters.AddWithValue("@reason", (object?)rejectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task SeedMatchAsync(
        Guid lostItemId,
        Guid foundItemId,
        Guid reporterId)
    {
        var now = UtcNow();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO matches
                (id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                 claimant_role, status, confidence_score, scoring_version, lost_snapshot,
                 found_snapshot, created_at, updated_at)
            VALUES
                (@id, @lost, @found, @reporter, @reporter, @reporter, 'LOST', 'PENDING',
                 55.00, 'unit-test-v1', '{}', '{}', @now, @now);
            """,
            connection);

        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@lost", lostItemId);
        command.Parameters.AddWithValue("@found", foundItemId);
        command.Parameters.AddWithValue("@reporter", reporterId);
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedNotificationAsync(
        Guid appealId,
        string notificationType,
        string recipientEmail = "appellant@example.com",
        DateTime? dueAt = null,
        string status = "PENDING")
    {
        var id = Guid.NewGuid();
        var now = UtcNow();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO appeal_notifications
                (id, appeal_id, notification_type, recipient_email, status, attempts,
                 next_attempt_at, created_at, updated_at)
            VALUES
                (@id, @appealId, @type, @email, @status, 0, @dueAt, @now, @now);
            """,
            connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@appealId", appealId);
        command.Parameters.AddWithValue("@type", notificationType);
        command.Parameters.AddWithValue("@email", recipientEmail);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@dueAt", dueAt ?? UtcNow().AddMinutes(-1));
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<(string Status, int Attempts, DateTime? NextAttemptAt)> ReadNotificationAsync(Guid id)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status, attempts, next_attempt_at FROM appeal_notifications WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    // ---- ClaimNextAsync ------------------------------------------------------------------------

    [Fact]
    public async Task ClaimNext_NoDueRows_ReturnsNothing()
    {
        var appealId = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedNotificationAsync(appealId, AppealNotificationType.Rejected, dueAt: UtcNow().AddMinutes(30));

        var job = await BuildRepo().ClaimNextAsync(CancellationToken.None);

        Assert.Null(job);
    }

    [Fact]
    public async Task ClaimNext_DueRejectedRowWithoutAMatch_IsClaimed()
    {
        /* A rejection never produces a match, so the repository must let a rejected email through
           even when no matches row exists - this is what makes the REJECTED half of the story work. */
        var appealId = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Rejected);

        var job = await BuildRepo().ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(notificationId, job.Id);
        Assert.Equal(AppealNotificationType.Rejected, job.Type);
        Assert.Equal("appellant@example.com", job.RecipientEmail);
        Assert.Equal(1, job.Attempts);
        Assert.Null(job.RejectionReason); // seeded without a reason - default value must round-trip as null
    }

    [Fact]
    public async Task ClaimNext_DueRejectedRow_CarriesTheAppealRejectionReason()
    {
        var appealId = await SeedAppealAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            rejectionReason: "Wrong bag");
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Rejected);

        var job = await BuildRepo().ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(notificationId, job.Id);
        Assert.Equal("Wrong bag", job.RejectionReason);
    }

    [Fact]
    public async Task ClaimNext_DueVerifiedRowWithoutAMatch_IsNotClaimed()
    {
        /* The verified email links to the fresh match, so it must not be sent until that match
           exists. The current pair where both reports touch might genuinely be gone by the time the
           worker polls, so a verified row can sit PENDING until a match does exist. */
        var appealId = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Verified);

        var job = await BuildRepo().ClaimNextAsync(CancellationToken.None);

        Assert.Null(job);

        var row = await ReadNotificationAsync(notificationId);
        Assert.Equal("PENDING", row.Status);
    }

    [Fact]
    public async Task ClaimNext_DueVerifiedRowWithAMatchingMatch_IsClaimedWithThatMatchId()
    {
        /* The left join in the find query pairs the appeal with the matches row on the same two item
           ids; the claimed job must expose that match so the email can link to it. */
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var appellantId = Guid.NewGuid();

        var appealId = await SeedAppealAsync(lostItemId, foundItemId, appellantId);
        await SeedMatchAsync(lostItemId, foundItemId, appellantId);
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Verified);

        var job = await BuildRepo().ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(notificationId, job.Id);
        Assert.Equal(AppealNotificationType.Verified, job.Type);
        Assert.NotNull(job.MatchId);
    }

    [Fact]
    public async Task ClaimNext_ClaimsInDeadlineOrderOldestFirst()
    {
        /* Two due rows - the earlier next_attempt_at must be returned first, so that an appeal that
           has been waiting longer is sent before one that is due a little later. */
        var oldAppeal = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var newAppeal = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var olderId = await SeedNotificationAsync(oldAppeal, AppealNotificationType.Rejected, dueAt: UtcNow().AddMinutes(-5));
        var newerId = await SeedNotificationAsync(newAppeal, AppealNotificationType.Rejected, dueAt: UtcNow().AddMinutes(-1));

        var repo = BuildRepo();

        var first = await repo.ClaimNextAsync(CancellationToken.None);
        Assert.Equal(olderId, first?.Id);

        var second = await repo.ClaimNextAsync(CancellationToken.None);
        Assert.Equal(newerId, second?.Id);

        Assert.Null(await repo.ClaimNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClaimNext_TwoConcurrentClaims_OnlyOneWins()
    {
        /* The optimistic token (WHERE next_attempt_at = @seenNextAttemptAt) means two workers
           polling at the same instant can only ever hand the row to one of them - the actual LF-338
           race DoD, asserted here at repository level and again end-to-end in
           AdminAppealsApiIntegrationTests. */
        var appealId = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedNotificationAsync(appealId, AppealNotificationType.Rejected);

        var repo = BuildRepo();
        var results = await Task.WhenAll(
            repo.ClaimNextAsync(CancellationToken.None),
            repo.ClaimNextAsync(CancellationToken.None));

        // Both workers raced the same row, but the optimistic claim hands it to exactly one of them.
        Assert.Single(results.Where(r => r is not null));
    }

    [Fact]
    public async Task ClaimNext_ALeasedRow_IsNotClaimableAgain()
    {
        /* The claim moves next_attempt_at five minutes into the future, so even a row the first
           worker has not yet finished with stays invisible to every other worker. */
        var appealId = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Rejected);

        var repo = BuildRepo();

        var first = await repo.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(first);

        Assert.Null(await repo.ClaimNextAsync(CancellationToken.None));

        // And the lease is visible: next_attempt_at moved into the future by the claim itself.
        var row = await ReadNotificationAsync(notificationId);
        Assert.Equal("PENDING", row.Status);
        Assert.True(row.NextAttemptAt > UtcNow(), "A claim must lease the row into the future.");
        Assert.Equal(1, row.Attempts);
    }

    // ---- MarkSentAsync -------------------------------------------------------------------------

    [Fact]
    public async Task MarkSent_PersistsSentStateAndTheMatchId()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var appellantId = Guid.NewGuid();
        var matchId = Guid.NewGuid();

        var appealId = await SeedAppealAsync(lostItemId, foundItemId, appellantId, status: "VERIFIED");
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Verified);

        var job = new AppealEmailJob(
            notificationId, AppealNotificationType.Verified, "appellant@example.com", 1, null, matchId);

        await BuildRepo().MarkSentAsync(job, CancellationToken.None);

        var row = await ReadNotificationAsync(notificationId);
        Assert.Equal("SENT", row.Status);
        Assert.Null(row.NextAttemptAt);

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT match_id, sent_at FROM appeal_notifications WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", notificationId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal(matchId, reader.GetGuid(0));
        Assert.False(reader.IsDBNull(1), "A SENT row must carry a sent_at timestamp.");
    }

    [Fact]
    public async Task MarkSent_WithoutAMatchId_PersistsSentState()
    {
        var appealId = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), status: "REJECTED");
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Rejected);

        var job = new AppealEmailJob(
            notificationId, AppealNotificationType.Rejected, "appellant@example.com", 1, null, null);

        await BuildRepo().MarkSentAsync(job, CancellationToken.None);

        var row = await ReadNotificationAsync(notificationId);
        Assert.Equal("SENT", row.Status);
    }

    // ---- MarkFailedAttemptAsync -----------------------------------------------------------------

    [Theory]
    [InlineData(1, 2.0)]
    [InlineData(2, 4.0)]
    [InlineData(3, 8.0)]
    [InlineData(4, 16.0)]
    public async Task MarkFailedAttempt_BelowTheMaximumAttempts_RequeuesWithExponentialBackoff(
        int attempts, double expectedMinutes)
    {
        var appealId = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Rejected);
        var job = new AppealEmailJob(
            notificationId, AppealNotificationType.Rejected, "appellant@example.com", attempts, null, null);

        var before = UtcNow();
        await BuildRepo().MarkFailedAttemptAsync(job, CancellationToken.None);
        var after = UtcNow();

        var row = await ReadNotificationAsync(notificationId);
        Assert.Equal("PENDING", row.Status);
        Assert.NotNull(row.NextAttemptAt);

        // backoff = 2^attempts minutes, capped at 60 - assert it landed in the expected window.
        var expectedDelay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, attempts)));
        Assert.InRange(
            row.NextAttemptAt.Value,
            before.Add(expectedDelay).AddSeconds(-30),
            after.Add(expectedDelay).AddSeconds(30));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task MarkFailedAttempt_OnOrAfterTheLastAllowedAttempt_MarksTheRowFailed(int attempts)
    {
        var appealId = await SeedAppealAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var notificationId = await SeedNotificationAsync(appealId, AppealNotificationType.Rejected);
        var job = new AppealEmailJob(
            notificationId, AppealNotificationType.Rejected, "appellant@example.com", attempts, null, null);

        await BuildRepo().MarkFailedAttemptAsync(job, CancellationToken.None);

        var row = await ReadNotificationAsync(notificationId);
        Assert.Equal("FAILED", row.Status);
        Assert.Null(row.NextAttemptAt);
        Assert.Equal(0, row.Attempts); // attempts is only incremented by ClaimNextAsync, never by the failure record
    }
}