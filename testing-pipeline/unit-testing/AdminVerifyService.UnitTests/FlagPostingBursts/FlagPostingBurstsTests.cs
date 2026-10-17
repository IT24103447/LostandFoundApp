using System.Reflection;
using AdminVerifyService.Configuration;
using AdminVerifyService.Listings;
using AdminVerifyService.Spam;
using Microsoft.Extensions.Configuration;

namespace AdminVerifyService.UnitTests.FlagPostingBursts;

public sealed class FlagPostingBurstsTests
{
    [Theory]
    [InlineData("NEEDS_REVIEW", -1, 3, 0, true)]
    [InlineData("NEEDS_REVIEW", 0, 3, 0, false)]
    [InlineData("NEEDS_REVIEW", 1, 3, 0, false)]
    [InlineData("UNDER_REVIEW", -1, 3, 0, false)]
    [InlineData("PENDING_SOLVE", -1, 3, 0, false)]
    [InlineData("DISMISSED", -1, 3, 0, false)]
    [InlineData("SOLVED", -1, 3, 0, false)]
    [InlineData("NEEDS_REVIEW", -1, 3, 3, false)]
    [InlineData("NEEDS_REVIEW", -1, 3, 4, true)]
    public void CollectingRuleHonorsExactWindowStatusAndOptionalCap(
        string status, int secondsFromExpiry, int count, int cap, bool expected)
    {
        var clock = new FrozenTime(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var until = clock.GetUtcNow().UtcDateTime.AddMinutes(60);
        var record = new CollectingRecord(Guid.NewGuid(), status, until, count);
        var listing = new ListingEvent(Guid.NewGuid(), "items.lost_item.created", Guid.NewGuid(),
            ListingType.Lost, Guid.NewGuid(), until.AddSeconds(secondsFromExpiry));
        var settings = new DetectionSettings { SpamRecordCap = cap };

        Assert.Equal(expected, CanJoin(record, listing, settings));
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public void CollectingRuleAcceptsBothListingTypes(string type)
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var listing = new ListingEvent(Guid.NewGuid(), "items.test.created", Guid.NewGuid(), type, Guid.NewGuid(), now);
        Assert.True(CanJoin(new CollectingRecord(Guid.NewGuid(), "NEEDS_REVIEW", now.AddMinutes(60), 3),
            listing, new DetectionSettings()));
    }

    [Fact]
    public void ChangedConfigurationChangesTheNextCapDecision()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SpamRecordCap"] = "0", ["SpamThreshold"] = "3", ["SpamWindowMinutes"] = "60"
        }).Build();
        var now = DateTime.UtcNow;
        var record = new CollectingRecord(Guid.NewGuid(), "NEEDS_REVIEW", now.AddMinutes(60), 3);
        var listing = new ListingEvent(Guid.NewGuid(), "items.lost_item.created", Guid.NewGuid(), "LOST", Guid.NewGuid(), now);
        Assert.True(CanJoin(record, listing, config.Get<DetectionSettings>()!));
        config["SpamRecordCap"] = "3";
        Assert.False(CanJoin(record, listing, config.Get<DetectionSettings>()!));
    }

    private static bool CanJoin(CollectingRecord record, ListingEvent listing, DetectionSettings settings) =>
        (bool)typeof(SpamRule).GetMethod("CanJoin", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [record, listing, settings])!;

    private sealed class FrozenTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
