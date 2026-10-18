using AdminVerifyService.UnitTests.Support;
using ItemService.Models;
using ItemService.Models.Dtos;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AdminVerifyService.UnitTests.OpenSpamRecord;

public sealed class OpenSpamRecordTests
{
    [Theory]
    [InlineData(AdminItemType.LOST, 0)]
    [InlineData(AdminItemType.LOST, 2)]
    [InlineData(AdminItemType.FOUND, 0)]
    [InlineData(AdminItemType.FOUND, 2)]
    public async Task AdminPhotosReturnRepositoryOrderIncludingAnEmptyCollection(AdminItemType type, int count)
    {
        var ctx = new AdminItemsContext();
        var item = AdminItemsContext.Record(type);
        var photos = Enumerable.Range(1, count).Select(x => $"https://photos.example.test/{x}.jpg").ToArray();
        ctx.Items.Setup(x => x.GetByIdAsync(type, item.Id, CancellationToken.None)).ReturnsAsync(item);
        ctx.Items.Setup(x => x.GetPhotoUrlsAsync(type, item.Id, CancellationToken.None)).ReturnsAsync(photos);

        var result = await ctx.Controller.GetItemPhotos(type.ToString().ToLowerInvariant(), item.Id, CancellationToken.None);

        Assert.Equal(photos, Assert.IsType<AdminItemPhotosDto>(Assert.IsType<OkObjectResult>(result.Result).Value).PhotoUrls);
        ctx.Items.VerifyAll();
    }

    [Fact]
    public async Task UnknownListingReturns404WithoutFetchingPhotos()
    {
        var ctx = new AdminItemsContext();
        var id = Guid.NewGuid();
        ctx.Items.Setup(x => x.GetByIdAsync(AdminItemType.LOST, id, CancellationToken.None)).ReturnsAsync((AdminItemRecord?)null);
        Assert.IsType<NotFoundObjectResult>((await ctx.Controller.GetItemPhotos("lost", id, CancellationToken.None)).Result);
        ctx.Items.Verify(x => x.GetPhotoUrlsAsync(It.IsAny<AdminItemType>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "1", true)]
    public Task OpenRequiresAdmin(bool authenticated, string? claim, bool expected) =>
        AdminPolicy.AssertAccessAsync("Open", authenticated, claim, expected);
}
