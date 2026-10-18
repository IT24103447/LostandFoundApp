using System.Text.Json;
using AdminVerifyService.Listings;

namespace AdminVerifyService.UnitTests.ReceiveListingEvents;

public sealed class ReceiveListingEventsTests
{
    [Theory]
    [InlineData("items.lost_item.created", "LOST", false)]
    [InlineData("items.found_item.created", "FOUND", false)]
    [InlineData("items.lost_item.updated", "LOST", true)]
    [InlineData("items.found_item.updated", "FOUND", true)]
    public void IntakeUsesListingTimeAndPreservesItsIdentity(string topic, string type, bool update)
    {
        var eventId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var created = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var edited = created.AddMinutes(30);
        var json = JsonSerializer.Serialize(new
        {
            eventId, userId, lostItemId = listingId, foundItemId = listingId,
            createdAt = created, updatedAt = edited,
            title = "Private title not tracked", description = "Private description not tracked"
        });

        var listing = Assert.IsType<ListingEvent>(ListingEventParser.Parse(topic, json));

        Assert.Equal(eventId, listing.EventId);
        Assert.Equal(listingId, listing.ListingId);
        Assert.Equal(userId, listing.UserId);
        Assert.Equal(type, listing.ListingType);
        Assert.Equal(update ? edited : created, listing.PostedAt);
        var tracked = JsonSerializer.Serialize(listing);
        Assert.DoesNotContain("Private title", tracked);
        Assert.DoesNotContain("Private description", tracked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    public void InvalidPayloadDoesNotProduceAListing(string? json) =>
        Assert.Null(ListingEventParser.Parse("items.lost_item.created", json));

    [Theory]
    [InlineData("eventId")]
    [InlineData("userId")]
    [InlineData("lostItemId")]
    [InlineData("createdAt")]
    public void MissingIdentityOrPostTimeIsRejected(string missing)
    {
        var payload = new Dictionary<string, object>
        {
            ["eventId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid(),
            ["lostItemId"] = Guid.NewGuid(), ["createdAt"] = DateTime.UtcNow
        };
        payload.Remove(missing);
        Assert.Null(ListingEventParser.Parse("items.lost_item.created", JsonSerializer.Serialize(payload)));
    }

    [Fact]
    public void PhotoOnlyEditUsesTheSameListingIdentity()
    {
        var id = Guid.NewGuid();
        var user = Guid.NewGuid();
        var first = ListingEventParser.Parse("items.found_item.created", JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), userId = user, foundItemId = id, createdAt = DateTime.UtcNow
        }))!;
        var edit = ListingEventParser.Parse("items.found_item.updated", JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), userId = user, foundItemId = id,
            updatedAt = DateTime.UtcNow.AddMinutes(1), photoUrls = new[] { "https://example.test/new.jpg" }
        }))!;
        Assert.Equal(first.ListingId, edit.ListingId);
        Assert.Equal(first.UserId, edit.UserId);
        Assert.NotEqual(first.EventId, edit.EventId);
    }
}
