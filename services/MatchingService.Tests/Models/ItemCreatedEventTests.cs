using MatchingService.Models;

namespace MatchingService.Tests.Models;

/// <summary>
/// Story 1, Scenario 5 contract tests for ItemCreatedEvent.GetItemId. The same throw path is also
/// exercised indirectly through ImageDescriptionEventHandlerTests, but is tested here in isolation
/// too, matching ItemService.Tests's own precedent of a dedicated Models/ test file for model classes
/// that carry real logic rather than just being plain data.
/// </summary>
public sealed class ItemCreatedEventTests
{
    // Scenario 5: a lost-type lookup against a payload that actually carries LostItemId succeeds.
    [Fact]
    public void GetItemId_LostTypeWithLostItemId_ReturnsLostItemId()
    {
        var itemId = Guid.NewGuid();
        var itemEvent = new ItemCreatedEvent { EventId = Guid.NewGuid(), LostItemId = itemId };

        Assert.Equal(itemId, itemEvent.GetItemId(ItemType.Lost));
    }

    // Scenario 5: same for found-type against FoundItemId, confirming both types resolve correctly from the same event shape. 
    [Fact]
    public void GetItemId_FoundTypeWithFoundItemId_ReturnsFoundItemId()
    {
        var itemId = Guid.NewGuid();
        var itemEvent = new ItemCreatedEvent { EventId = Guid.NewGuid(), FoundItemId = itemId };

        Assert.Equal(itemId, itemEvent.GetItemId(ItemType.Found));
    }

    /* A lost-type lookup against a payload with no LostItemId at all must fail rather than default
       to Guid.Empty or fall through to FoundItemId. */
    [Fact]
    public void GetItemId_LostTypeWithoutLostItemId_ThrowsInvalidDataException()
    {
        var itemEvent = new ItemCreatedEvent { EventId = Guid.NewGuid() };

        Assert.Throws<InvalidDataException>(() => itemEvent.GetItemId(ItemType.Lost));
    }

    /* A lost-type lookup must not accidentally succeed using FoundItemId if LostItemId is empty.
       The two are not interchangeable even if both happen to be present. */
    [Fact]
    public void GetItemId_LostTypeWithOnlyFoundItemId_ThrowsInvalidDataException()
    {
        var itemEvent = new ItemCreatedEvent { EventId = Guid.NewGuid(), FoundItemId = Guid.NewGuid() };

        Assert.Throws<InvalidDataException>(() => itemEvent.GetItemId(ItemType.Lost));
    }

    // An explicit Guid.Empty for the relevant ID is treated the same as it being entirely absent.
    [Fact]
    public void GetItemId_ExplicitEmptyGuidForRelevantId_ThrowsInvalidDataException()
    {
        var itemEvent = new ItemCreatedEvent { EventId = Guid.NewGuid(), LostItemId = Guid.Empty };

        Assert.Throws<InvalidDataException>(() => itemEvent.GetItemId(ItemType.Lost));
    }
}
