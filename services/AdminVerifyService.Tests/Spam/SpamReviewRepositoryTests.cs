using AdminVerifyService.Databases;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using MySqlConnector;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// Story LF-87 contract tests for SpamReviewRepository.ListAsync against a real MySQL
/// (Testcontainers, the app's real migrations). Pins the list query: status-tab filter, score/date
/// ordering with their tie-breaks, the from-inclusive/to-exclusive date range, user-id filtering,
/// LIMIT 21 has-more pagination, and the UTC normalisation of FlaggedAt.
///
/// The test project's Docker collection runs these classes sequentially, so each test seeds its own
/// fixture rows after clearing the tables (FK child tables first).
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamReviewRepositoryTests : IClassFixture<AdminVerifyDbFixture>
{
    private static readonly Guid User1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid User2 = Guid.Parse("20000000-0000-0000-0000-000000000002");

    private static readonly IReadOnlyList<string> ActiveStatuses =
        new[] { SpamRecordStatus.NeedsReview, SpamRecordStatus.UnderReview, SpamRecordStatus.PendingSolve };

    private readonly AdminVerifyDbFixture _fixture;

    public SpamReviewRepositoryTests(AdminVerifyDbFixture fixture) => _fixture = fixture;

    private SpamReviewRepository Repository => new(_fixture.Connections);

    // ---- seeding helpers ----

    private static async Task ResetAsync(IDbConnectionFactory connections)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        // FK child tables first: notifications -> record_listings -> spam_records.
        await using var command = new MySqlCommand(
            """
            DELETE FROM spam_record_notifications;
            DELETE FROM spam_record_listings;
            DELETE FROM spam_records;
            """,
            connection);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAsync(
        IDbConnectionFactory connections,
        Guid id,
        Guid userId,
        int scoreA,
        string status,
        DateTime createdUtc)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO spam_records
                (id, user_id, collecting_user_id, score_a, status, collecting_until, created_at, updated_at)
            VALUES
                (@id, @userId, NULL, @scoreA, @status, @collectingUntil, @createdAt, @createdAt);
            """,
            connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@scoreA", scoreA);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@collectingUntil", createdUtc.AddHours(1));
        command.Parameters.AddWithValue("@createdAt", createdUtc);

        await command.ExecuteNonQueryAsync();
    }

    private static SpamRecordListQuery Query(
        IReadOnlyList<string>? statuses = null,
        bool sortByScore = true,
        DateTime? from = null,
        DateTime? to = null,
        IReadOnlyList<Guid>? userIds = null,
        int page = 1) =>
        new(
            statuses ?? ActiveStatuses,
            sortByScore,
            from,
            to,
            userIds ?? Array.Empty<Guid>(),
            page);

    // ---- status tab ----

    [Fact]
    public async Task ActiveTab_ReturnsOnlyTheThreeCollectingStatuses()
    {
        await ResetAsync(_fixture.Connections);
        await SeedAsync(_fixture.Connections, Guid.Parse("a0000000-0000-0000-0000-000000000001"), User1, 4, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, Guid.Parse("a0000000-0000-0000-0000-000000000002"), User1, 2, SpamRecordStatus.UnderReview, new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, Guid.Parse("a0000000-0000-0000-0000-000000000003"), User2, 6, SpamRecordStatus.PendingSolve, new DateTime(2026, 5, 2, 8, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, Guid.Parse("a0000000-0000-0000-0000-000000000004"), User1, 9, SpamRecordStatus.Solved, new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, Guid.Parse("a0000000-0000-0000-0000-000000000005"), User2, 1, SpamRecordStatus.Dismissed, new DateTime(2026, 5, 2, 7, 0, 0, DateTimeKind.Utc));

        var page = await Repository.ListAsync(Query(), CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.Equal(
            new[] { SpamRecordStatus.PendingSolve, SpamRecordStatus.NeedsReview, SpamRecordStatus.UnderReview },
            page.Items.Select(row => row.Status).ToArray());
        Assert.DoesNotContain(page.Items, row => row.Status is SpamRecordStatus.Solved or SpamRecordStatus.Dismissed);
    }

    // ---- ordering ----

    [Fact]
    public async Task ScoreOrder_SortsByScoreDesc_ThenCreatedDesc_ThenId()
    {
        await ResetAsync(_fixture.Connections);
        var high = Guid.Parse("b0000000-0000-0000-0000-000000000001");
        var lateTie = Guid.Parse("b0000000-0000-0000-0000-000000000002");
        var earlyTie = Guid.Parse("b0000000-0000-0000-0000-000000000003");
        var finalTie = Guid.Parse("b0000000-0000-0000-0000-000000000004");
        await SeedAsync(_fixture.Connections, high, User1, 5, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, earlyTie, User1, 3, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, lateTie, User1, 3, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, finalTie, User1, 3, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        var page = await Repository.ListAsync(Query(), CancellationToken.None);

        // 5 first; then the three score-3 rows newest-first (created 12:00 before 11:00),
        // then the id tie-break ascending among the two 12:00 rows.
        Assert.Equal(
            new[] { high, lateTie, finalTie, earlyTie },
            page.Items.Select(row => row.Id).ToArray());
    }

    [Fact]
    public async Task DateOrder_SortsByCreatedDesc_ThenId_IgnoringScore()
    {
        await ResetAsync(_fixture.Connections);
        var newest = Guid.Parse("c0000000-0000-0000-0000-000000000001");
        var middle = Guid.Parse("c0000000-0000-0000-0000-000000000002");
        var oldest = Guid.Parse("c0000000-0000-0000-0000-000000000003");
        // Scores inverted against the date order on purpose.
        await SeedAsync(_fixture.Connections, oldest, User1, 9, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, middle, User1, 5, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, newest, User1, 1, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc));

        var page = await Repository.ListAsync(
            Query(sortByScore: false),
            CancellationToken.None);

        Assert.Equal(new[] { newest, middle, oldest }, page.Items.Select(row => row.Id).ToArray());
    }

    // ---- date range (from inclusive, to exclusive) ----

    [Fact]
    public async Task DateRange_FromInclusive_AndToExclusive()
    {
        await ResetAsync(_fixture.Connections);
        var before = Guid.Parse("d0000000-0000-0000-0000-000000000001");
        var atFrom = Guid.Parse("d0000000-0000-0000-0000-000000000002");
        var inside = Guid.Parse("d0000000-0000-0000-0000-000000000003");
        var atTo = Guid.Parse("d0000000-0000-0000-0000-000000000004");
        await SeedAsync(_fixture.Connections, before, User1, 1, SpamRecordStatus.NeedsReview, new DateTime(2026, 4, 30, 23, 59, 59, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, atFrom, User1, 1, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, inside, User1, 1, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 3, 23, 59, 59, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, atTo, User1, 1, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc));

        // What SpamReviewQueryParser hands in: FlaggedFrom = from, FlaggedBefore = to + 1 day.
        var page = await Repository.ListAsync(
            Query(from: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                  to: new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        Assert.Equal(new[] { inside, atFrom }, page.Items.Select(row => row.Id).ToArray());
    }

    [Fact]
    public async Task DateRange_NoFilters_ReturnsEveryMatchingStatusRegardlessOfAge()
    {
        await ResetAsync(_fixture.Connections);
        var old = Guid.Parse("d0000000-0000-0000-0000-000000000010");
        var newRow = Guid.Parse("d0000000-0000-0000-0000-000000000011");
        await SeedAsync(_fixture.Connections, old, User1, 1, SpamRecordStatus.NeedsReview, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, newRow, User1, 1, SpamRecordStatus.NeedsReview, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var page = await Repository.ListAsync(Query(), CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
    }

    // ---- user-id filter ----

    [Fact]
    public async Task UserFilter_ReturnsOnlyTheRequestedUser()
    {
        await ResetAsync(_fixture.Connections);
        var user1A = Guid.Parse("e0000000-0000-0000-0000-000000000001");
        var user1B = Guid.Parse("e0000000-0000-0000-0000-000000000002");
        var user2Row = Guid.Parse("e0000000-0000-0000-0000-000000000003");
        await SeedAsync(_fixture.Connections, user1A, User1, 3, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, user1B, User1, 2, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, user2Row, User2, 7, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 2, 8, 0, 0, DateTimeKind.Utc));

        var page = await Repository.ListAsync(
            Query(userIds: new[] { User1 }),
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, row => Assert.Equal(User1, row.UserId));
    }

    [Fact]
    public async Task UserFilter_WithSeveralUserIds_ReturnsTheUnion()
    {
        await ResetAsync(_fixture.Connections);
        var user1Row = Guid.Parse("e0000000-0000-0000-0000-000000000010");
        var user2Row = Guid.Parse("e0000000-0000-0000-0000-000000000011");
        await SeedAsync(_fixture.Connections, user1Row, User1, 3, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));
        await SeedAsync(_fixture.Connections, user2Row, User2, 4, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));

        var page = await Repository.ListAsync(
            Query(userIds: new[] { User1, User2 }),
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new[] { user2Row, user1Row }, page.Items.Select(row => row.Id).ToArray());
    }

    // ---- pagination (LIMIT 21 semantics) ----

    [Fact]
    public async Task FirstPage_HasMore_ThenSecondPage_ExhaustAndThirdIsEmpty()
    {
        await ResetAsync(_fixture.Connections);

        // 21 rows, newest hour labelled 20 -> oldest 0; created desc puts 20 first.
        var ids = Enumerable.Range(0, 21)
            .Select(i => Guid.Parse($"f0000000-0000-0000-0000-{i:D12}"))
            .ToArray();

        for (var i = 0; i < ids.Length; i++)
        {
            await SeedAsync(
                _fixture.Connections,
                ids[i], User1, 1, SpamRecordStatus.NeedsReview,
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i));
        }

        var first = await Repository.ListAsync(Query(page: 1), CancellationToken.None);
        var second = await Repository.ListAsync(Query(page: 2), CancellationToken.None);
        var third = await Repository.ListAsync(Query(page: 3), CancellationToken.None);

        Assert.Equal(20, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal(ids.Reverse().Take(20), first.Items.Select(row => row.Id));

        Assert.Single(second.Items, row => row.Id == ids[0]);
        Assert.False(second.HasMore);

        Assert.Empty(third.Items);
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task ExactlyTwentyRows_HasMoreFalse()
    {
        await ResetAsync(_fixture.Connections);

        for (var i = 0; i < 20; i++)
        {
            await SeedAsync(
                _fixture.Connections,
                Guid.NewGuid(), User1, 1, SpamRecordStatus.NeedsReview,
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }

        var page = await Repository.ListAsync(Query(page: 1), CancellationToken.None);

        Assert.Equal(20, page.Items.Count);
        Assert.False(page.HasMore);
    }

    // ---- row shape ----

    [Fact]
    public async Task NoMatchingRows_ReturnsAnEmptyPage()
    {
        await ResetAsync(_fixture.Connections);

        var page = await Repository.ListAsync(Query(), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task FlaggedAt_ComesBackAsUtcKind()
    {
        await ResetAsync(_fixture.Connections);
        var seeded = new DateTime(2026, 5, 1, 12, 30, 0, DateTimeKind.Utc);
        var id = Guid.Parse("90000000-0000-0000-0000-000000000001");
        await SeedAsync(_fixture.Connections, id, User1, 1, SpamRecordStatus.NeedsReview, seeded);

        var page = await Repository.ListAsync(Query(), CancellationToken.None);
        var row = Assert.Single(page.Items);

        Assert.Equal(DateTimeKind.Utc, row.FlaggedAt.Kind);
        Assert.Equal(seeded, row.FlaggedAt);
    }

    [Fact]
    public async Task ListingCount_And_ScoreA_AreReportedFromTheScoreAColumn()
    {
        await ResetAsync(_fixture.Connections);
        var id = Guid.Parse("80000000-0000-0000-0000-000000000001");
        await SeedAsync(_fixture.Connections, id, User1, 4, SpamRecordStatus.NeedsReview, new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        var page = await Repository.ListAsync(Query(), CancellationToken.None);
        var row = Assert.Single(page.Items);

        Assert.Equal(4, row.ScoreA);
        Assert.Equal(4, row.ListingCount);

        // Current contract: listingCount is fed from score_a, which today always equals the number of
        // collected listings (SpamRule adds exactly +1 per listing - scoring is not weighted). The
        // fragility (a real COUNT from spam_record_listings would decouple the two) is in known-gaps.
    }
}