using ItemService.Models;

public class ItemPhotoModelsTests
{
    [Fact]
    public void FoundItemPhoto_PreservesAssociationUrlAndCreationTime()
    {
        var id = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;
        var photo = new FoundItemPhoto
        {
            Id = id,
            FoundItemId = itemId,
            Url = "https://example.test/found-photo.jpg",
            CreatedAt = createdAt
        };

        Assert.Equal(id, photo.Id);
        Assert.Equal(itemId, photo.FoundItemId);
        Assert.Equal("https://example.test/found-photo.jpg", photo.Url);
        Assert.Equal(createdAt, photo.CreatedAt);
    }

    [Fact]
    public void LostItemPhoto_PreservesAssociationUrlAndCreationTime()
    {
        var id = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;
        var photo = new LostItemPhoto
        {
            Id = id,
            LostItemId = itemId,
            Url = "https://example.test/lost-photo.jpg",
            CreatedAt = createdAt
        };

        Assert.Equal(id, photo.Id);
        Assert.Equal(itemId, photo.LostItemId);
        Assert.Equal("https://example.test/lost-photo.jpg", photo.Url);
        Assert.Equal(createdAt, photo.CreatedAt);
    }
}
