using AdminVerifyService.Configuration;
using AdminVerifyService.Databases;
using AdminVerifyService.Listings;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// LF-82 regression tests for the DATETIME(3) rounding gap found by DevOps in deployed testing,
/// against a real MySQL (Testcontainers, the app's real migrations, real transactions).
///
/// The setup that produces the bug: Item Service stamps listings with DateTime.UtcNow, whose value
/// carries 7 decimal places, and that exact instant travels in the Kafka event. The intake stores
/// it in tracked_listings.posted_at, a DATETIME(3) column, so MySQL rounds it to the millisecond -
/// about half the time rounding UP, up to 0.5 ms LATER than the event time. The burst rule then
/// counts the user's free listings with posted_at &lt;= the un-rounded event time, so a listing
/// whose stored time rounded up failed to count ITSELF: the 3rd listing of a burst was counted as
/// 2 and no record was created, and the listing that did open a record was left out of it and
/// never joined afterwards (DevOps saw a score of 5 instead of 6).
///
/// The fix (commit b9a34ee) moves the query's upper bound by 1 ms, so the stored time is never
/// later than the bound and every listing counts itself. The query, its index range and its row
/// locks keep exactly the same shape - which is why the concurrency case below still passes; the
/// earlier "count the current listing by id" alternative removed the upper bound from the index
/// range and deadlocked when several users were processed at once.
///
/// Each test drives the real ListingEventHandler (intake + rule in one transaction), exactly as
/// SpamRuleTests and ListingEventHandlerTests do, and asserts only on BEHAVIOUR - how many records
/// exist, their status and score, and which listings are linked. Nothing here asserts on the 1 ms
/// value itself, so the tests keep passing if a future fix changes the bound by other means.
///
/// Each test uses a fresh user id, so no table reset is needed and no test sees another's data.
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamThresholdRoundingTests : IClassFixture<AdminVerifyDbFixture>
{
    /// <summary>Whole-millisecond base instant; the only sub-millisecond part of any event time is
    /// the deliberate rounding offset added on top of it, so MySQL's DATETIME(3) rounding is what
    /// decides which way a stored time moves.</summary>
    private static readonly DateTime BaseTime = new(2026, 10, 14, 9, 0, 0, 123, DateTimeKind.Utc);

    /// <summary>+0.6 ms: rounds UP when stored - the case that lost a listing.</summary>
    private const long RoundsUpTicks = 6_000;

    /// <summary>+0.2 ms: rounds DOWN when stored - the case that always worked.</summary>
    private const long RoundsDownTicks = 2_000;

    private const string LostTopic = "items.lost_item.created";
    private const string FoundTopic = "items.found_item.created";

    private record SpamRecordRow(Guid Id, int ScoreA, string Status);

    /// <summary>One listing this test will post: when it was posted, which way its stored time
    /// rounds, what kind of listing it is, and the id it is delivered under.</summary>
    private sealed record PlannedListing(double Minutes, long RoundingTicks, string ListingKind, Guid ListingId)
    {
        public static PlannedListing Up(double minutes, string kind) =>
            new(minutes, RoundsUpTicks, kind, Guid.NewGuid());

        public static PlannedListing Down(double minutes, string kind) =>
            new(minutes, RoundsDownTicks, kind, Guid.NewGuid());

        public static PlannedListing Exact(double minutes, string kind) =>
            new(minutes, 0, kind, Guid.NewGuid());

        public static PlannedListing At(double minutes, string kind, long ticks) =>
            new(minutes, ticks, kind, Guid.NewGuid());
    }

    /// <summary>Minimal monitor over a fixed DetectionSettings instance - production binding and
    /// validation are pinned by DetectionSettingsTests, not repeated here.</summary>
    private sealed class SettingsMonitor(DetectionSettings value) : IOptionsMonitor<DetectionSettings>
    {
        public DetectionSettings CurrentValue { get; } = value;

        public DetectionSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<DetectionSettings, string?> listener) => null;
    }

    /// <summary>The deployed detection policy: 3 listings in 60 minutes, no per-record cap.</summary>
    private static DetectionSettings Settings() => new()
    {
        SpamThreshold = 3,
        SpamWindowMinutes = 60,
        SpamRecordCap = 0
    };

    private readonly AdminVerifyDbFixture _fixture;

    public SpamThresholdRoundingTests(AdminVerifyDbFixture fixture) => _fixture = fixture;

    private ListingEventHandler Handler() =>
        new(
            _fixture.Connections,
            new TrackedListingRepository(),
            new SpamRule(
                new SpamRecordRepository(),
                new SettingsMonitor(Settings()),
                NullLogger<SpamRule>.Instance),
            TimeProvider.System);

    /// <summary>Posts one user's listings in order through the real handler; every one of them must
    /// be a brand new post (none is a replay, none is already tracked).</summary>
    private async Task PostAsync(Guid userId, params PlannedListing[] listings)
    {
        var handler = Handler();

        foreach (var listing in listings)
        {
            var evt = new ListingEvent(
                Guid.NewGuid(),
                listing.ListingKind == ListingType.Found ? FoundTopic : LostTopic,
                listing.ListingId,
                listing.ListingKind,
                userId,
                BaseTime.AddMinutes(listing.Minutes).AddTicks(listing.RoundingTicks));

            Assert.Equal(ListingEventOutcome.NewPost, await handler.HandleAsync(evt, CancellationToken.None));
        }
    }

    // ---- read helpers ----

    private async Task<IReadOnlyList<SpamRecordRow>> ReadRecordsAsync(Guid userId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT id, score_a, status FROM spam_records WHERE user_id = @userId ORDER BY id;",
            connection);
        command.Parameters.AddWithValue("@userId", userId);

        var rows = new List<SpamRecordRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SpamRecordRow(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>Every listing linked to any record belonging to this user, ordered by id.</summary>
    private async Task<IReadOnlyList<Guid>> ReadLinkedListingIdsAsync(Guid userId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            SELECT s.listing_id
            FROM spam_record_listings AS s
            INNER JOIN spam_records AS r ON r.id = s.spam_record_id
            WHERE r.user_id = @userId
            ORDER BY s.listing_id;
            """,
            connection);
        command.Parameters.AddWithValue("@userId", userId);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Asserts the burst produced exactly one NEEDS_REVIEW record whose score matches the
    /// number of links, and whose linked listings are EXACTLY the expected set - no listing dropped
    /// because its stored time rounded up, and none pulled in from outside the window.</summary>
    private async Task AssertOneRecordLinkedToAsync(Guid userId, params Guid[] expectedListingIds)
    {
        var record = Assert.Single(await ReadRecordsAsync(userId));

        Assert.Equal(SpamRecordStatus.NeedsReview, record.Status);
        Assert.Equal(expectedListingIds.Length, record.ScoreA);

        var linked = await ReadLinkedListingIdsAsync(userId);
        Assert.True(
            expectedListingIds.OrderBy(id => id).SequenceEqual(linked),
            $"Expected the record to link exactly [{string.Join(", ", expectedListingIds)}], "
            + $"but it linked [{string.Join(", ", linked)}].");
    }

    private async Task AssertNoRecordAsync(Guid userId)
    {
        Assert.Empty(await ReadRecordsAsync(userId));
        Assert.Empty(await ReadLinkedListingIdsAsync(userId));
    }

    // ---- regression tests ----

    [Fact] // Section 4 row 1: only the third listing rounds up - it must still count itself.
    public async Task STR01_BurstEndingOnARoundingUpListing_CreatesOneRecordWithAllThree()
    {
        var userId = Guid.NewGuid();

        var first = PlannedListing.Down(0, ListingType.Lost);
        var second = PlannedListing.Down(10, ListingType.Lost);
        var third = PlannedListing.Up(20, ListingType.Lost);

        await PostAsync(userId, first, second, third);

        await AssertOneRecordLinkedToAsync(userId, first.ListingId, second.ListingId, third.ListingId);
    }

    [Fact] // Section 4 row 2: three listings inside 48 seconds, every one of them rounding up.
    public async Task STR02_TightBurstOfRoundingUpListings_CreatesOneRecordWithAllThree()
    {
        var userId = Guid.NewGuid();

        var first = PlannedListing.Up(0, ListingType.Lost);
        var second = PlannedListing.Up(0.3, ListingType.Lost);
        var third = PlannedListing.Up(0.8, ListingType.Found);

        await PostAsync(userId, first, second, third);

        await AssertOneRecordLinkedToAsync(userId, first.ListingId, second.ListingId, third.ListingId);
    }

    [Fact] // Section 4 row 3 (control): every stored time rounds down, so this always worked and
    // must keep working - it guards the rounding path from regressing the other direction.
    public async Task STR03_BurstOfRoundingDownListings_CreatesOneRecordWithAllThree()
    {
        var userId = Guid.NewGuid();

        var first = PlannedListing.Down(0, ListingType.Found);
        var second = PlannedListing.Down(5, ListingType.Lost);
        var third = PlannedListing.Down(9, ListingType.Found);

        await PostAsync(userId, first, second, third);

        await AssertOneRecordLinkedToAsync(userId, first.ListingId, second.ListingId, third.ListingId);
    }

    [Fact] // Section 4 row 4 (control): two listings are below the threshold, rounding up or not.
    public async Task STR04_TwoListings_BelowThreshold_CreatesNoRecord()
    {
        var userId = Guid.NewGuid();

        await PostAsync(
            userId,
            PlannedListing.Up(0, ListingType.Lost),
            PlannedListing.Up(15, ListingType.Found));

        await AssertNoRecordAsync(userId);
    }

    [Fact] // Section 4 row 5: DevOps' reported case - a six-listing burst, every stored time
    // rounding up, must end as ONE record carrying all six listings.
    public async Task STR05_SixListingBurstOfRoundingUpListings_CreatesOneRecordWithAllSix()
    {
        var userId = Guid.NewGuid();

        var first = PlannedListing.Up(0, ListingType.Lost);
        var second = PlannedListing.Up(1, ListingType.Found);
        var third = PlannedListing.Up(2, ListingType.Lost);
        var fourth = PlannedListing.Up(3, ListingType.Found);
        var fifth = PlannedListing.Up(4, ListingType.Lost);
        var sixth = PlannedListing.Up(5, ListingType.Found);

        await PostAsync(userId, first, second, third, fourth, fifth, sixth);

        await AssertOneRecordLinkedToAsync(
            userId,
            first.ListingId,
            second.ListingId,
            third.ListingId,
            fourth.ListingId,
            fifth.ListingId,
            sixth.ListingId);
    }

    [Fact] // Section 4 row 6: the listing at 0 minutes has fallen out of the window by the time the
    // burst forms, so the record must link the 30/61/62 listings and NOT the 0 one.
    public async Task STR06_BurstFormingAfterTheWindowMoved_LinksOnlyTheListingsStillInWindow()
    {
        var userId = Guid.NewGuid();

        var agedOut = PlannedListing.Down(0, ListingType.Lost);
        var inWindow = PlannedListing.Down(30, ListingType.Lost);
        var roundsUpFirst = PlannedListing.Up(61, ListingType.Lost);
        var roundsUpSecond = PlannedListing.Up(62, ListingType.Found);

        await PostAsync(userId, agedOut, inWindow, roundsUpFirst);

        // Three listings are not enough here: 61 - 60 leaves only two inside the window.
        await AssertNoRecordAsync(userId);

        await PostAsync(userId, roundsUpSecond);

        await AssertOneRecordLinkedToAsync(
            userId,
            inWindow.ListingId,
            roundsUpFirst.ListingId,
            roundsUpSecond.ListingId);
    }

    [Fact] // Section 4 row 7 (control): the window edge is inclusive, so a listing posted exactly
    // 60 minutes before still counts even though nothing here rounds at all.
    public async Task STR07_ListingExactlySixtyMinutesBefore_CountsTowardsTheBurst()
    {
        var userId = Guid.NewGuid();

        var first = PlannedListing.Exact(0, ListingType.Lost);
        var second = PlannedListing.Down(30, ListingType.Found);
        var third = PlannedListing.Exact(60, ListingType.Lost);

        await PostAsync(userId, first, second, third);

        await AssertOneRecordLinkedToAsync(userId, first.ListingId, second.ListingId, third.ListingId);
    }

    [Fact] // Section 4 row 8: five users processed at the same moment. Moving the bound by 1 ms
    // keeps the index range and the row locks the same shape, so this must complete without a
    // deadlock - it is the case that rejects counting the current listing by id instead.
    public async Task STR08_FiveUsersAtTheSameMoment_EachGetsTheirOwnRecord_WithNoDeadlock()
    {
        var users = Enumerable.Range(0, 5)
            .Select(_ => new
            {
                UserId = Guid.NewGuid(),
                Listings = new[]
                {
                    PlannedListing.Down(0, ListingType.Lost),
                    PlannedListing.Up(2, ListingType.Found),
                    PlannedListing.Up(4, ListingType.Lost),
                },
            })
            .ToArray();

        // Each user posts its own three listings in order; the five users run concurrently.
        await Task.WhenAll(users.Select(user => PostAsync(user.UserId, user.Listings)));

        foreach (var user in users)
        {
            await AssertOneRecordLinkedToAsync(
                user.UserId,
                user.Listings[0].ListingId,
                user.Listings[1].ListingId,
                user.Listings[2].ListingId);
        }
    }

    [Theory] // Section 4 row 9: the same three-listing burst at 0/7/14 minutes under all four
    // rounding combinations - whichever way each stored time moves, the burst is still detected.
    [InlineData(RoundsUpTicks, RoundsDownTicks, RoundsUpTicks)]
    [InlineData(RoundsDownTicks, RoundsUpTicks, RoundsUpTicks)]
    [InlineData(RoundsUpTicks, RoundsUpTicks, RoundsUpTicks)]
    [InlineData(RoundsDownTicks, RoundsDownTicks, RoundsUpTicks)]
    public async Task STR09_AnyMixOfRoundingDirections_CreatesOneRecordWithAllThree(
        long firstTicks,
        long secondTicks,
        long thirdTicks)
    {
        var userId = Guid.NewGuid();

        var first = PlannedListing.At(0, ListingType.Lost, firstTicks);
        var second = PlannedListing.At(7, ListingType.Found, secondTicks);
        var third = PlannedListing.At(14, ListingType.Lost, thirdTicks);

        await PostAsync(userId, first, second, third);

        await AssertOneRecordLinkedToAsync(userId, first.ListingId, second.ListingId, third.ListingId);
    }
}