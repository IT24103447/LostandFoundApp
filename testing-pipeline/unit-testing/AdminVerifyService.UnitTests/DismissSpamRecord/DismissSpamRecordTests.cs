using AdminVerifyService.Configuration;
using AdminVerifyService.Listings;
using AdminVerifyService.Spam;
using AdminVerifyService.UnitTests.Support;
using System.Reflection;

namespace AdminVerifyService.UnitTests.DismissSpamRecord;

public sealed class DismissSpamRecordTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "1", true)]
    public Task DismissRequiresAdmin(bool authenticated, string? claim, bool expected) =>
        AdminPolicy.AssertAccessAsync("Dismiss", authenticated, claim, expected);

    [Fact]
    public void DismissedRecordCannotCollectMoreListings()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var dismissed = new CollectingRecord(Guid.NewGuid(), "DISMISSED", now.AddMinutes(60), 3);
        var listing = new ListingEvent(Guid.NewGuid(), "items.lost_item.created", Guid.NewGuid(), "LOST", Guid.NewGuid(), now);
        var joins = (bool)typeof(SpamRule).GetMethod("CanJoin", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [dismissed, listing, new DetectionSettings()])!;
        Assert.False(joins);
    }
}
