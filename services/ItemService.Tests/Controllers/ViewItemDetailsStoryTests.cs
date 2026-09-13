using System.Security.Claims;
using System.Text.Json;
using ItemService.Controllers;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.Tests.Controllers;

/// <summary>Story 6: public detail retrieval and privacy/ownership boundaries.</summary>
public sealed class ViewItemDetailsStoryTests
{
    [Fact]
    public async Task Details_ValidLostItem_ReturnsAllPublicFieldsAndNeverThePrivateFields()
    {
        var ownerId = Guid.NewGuid();
        const string secret = "lost-details-private-marker";
        var item = new LostItem
        {
            Id = Guid.NewGuid(), UserId = ownerId, Title = "Lost camera", Category = "Electronics",
            Description = "Black camera with strap", LastKnownLocation = "Library", DateLost = new DateOnly(2026, 9, 10),
            Status = LostItemStatus.ACTIVE, HiddenInformation = secret, CreatedAt = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            Photos = [new LostItemPhoto { Id = Guid.NewGuid(), Url = "/photos/lost-one.jpg" }, new LostItemPhoto { Id = Guid.NewGuid(), Url = "/photos/lost-two.jpg" }]
        };
        var lost = new Mock<ILostItemsRepository>();
        lost.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Create(ownerId, lost).GetById(item.Id, CancellationToken.None);

        var dto = Ok(result);
        Assert.Equal(item.Id, dto.Id);
        Assert.Equal("LOST", dto.Type);
        Assert.Equal(item.Title, dto.Title);
        Assert.Equal(item.Description, dto.Description);
        Assert.Equal(item.Category, dto.Category);
        Assert.Equal(item.LastKnownLocation, dto.Location);
        Assert.Equal("2026-09-10", dto.Date);
        Assert.Equal("ACTIVE", dto.Status);
        Assert.Equal(["/photos/lost-one.jpg", "/photos/lost-two.jpg"], dto.PhotoUrls);
        Assert.Equal("/photos/lost-one.jpg", dto.Photo!.Url);
        AssertPublicJsonNeverLeaks(dto, secret);
    }

    [Fact]
    public async Task Details_ValidFoundItem_ReturnsAllPublicFieldsAndNeverThePrivateFields()
    {
        var ownerId = Guid.NewGuid();
        const string secret = "found-details-private-marker";
        var item = new FoundItem
        {
            Id = Guid.NewGuid(), UserId = ownerId, Title = "Found keys", Category = "Keys",
            Description = "Keys with a blue tag", LocationFound = "Food court", DateFound = new DateOnly(2026, 9, 11),
            Status = FoundItemStatus.ACTIVE, HiddenInformation = secret, CreatedAt = DateTime.UtcNow
        };
        var lost = MissingLost();
        var found = new Mock<IFoundItemsRepository>();
        found.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Create(ownerId, lost, found).GetById(item.Id, CancellationToken.None);

        var dto = Ok(result);
        Assert.Equal("FOUND", dto.Type);
        Assert.Equal(item.Title, dto.Title);
        Assert.Equal(item.LocationFound, dto.Location);
        Assert.Equal("2026-09-11", dto.Date);
        Assert.Equal("ACTIVE", dto.Status);
        Assert.Empty(dto.PhotoUrls);
        Assert.Null(dto.Photo);
        AssertPublicJsonNeverLeaks(dto, secret);
    }

    [Fact]
    public async Task Details_MissingOrRepositoryFilteredSoftDeletedItem_Returns404()
    {
        var id = Guid.NewGuid();
        var lost = MissingLost();
        var found = MissingFound();

        var result = await Create(Guid.NewGuid(), lost, found).GetById(id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        lost.Verify(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        found.Verify(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Details_ResolvedLostItem_Is404ForANonOwner()
    {
        var item = new LostItem { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = LostItemStatus.RESOLVED };
        var lost = new Mock<ILostItemsRepository>();
        lost.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Create(Guid.NewGuid(), lost).GetById(item.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Details_ResolvedFoundItem_Is404ForANonOwner()
    {
        var item = new FoundItem { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = FoundItemStatus.RESOLVED };
        var found = new Mock<IFoundItemsRepository>();
        found.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Create(Guid.NewGuid(), MissingLost(), found).GetById(item.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Details_ResolvedOriginalReporterCanStillRetrieveTheirOwnItem(bool isLost)
    {
        var ownerId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var lost = MissingLost();
        var found = MissingFound();
        if (isLost)
            lost.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(new LostItem { Id = id, UserId = ownerId, Status = LostItemStatus.RESOLVED });
        else
            found.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(new FoundItem { Id = id, UserId = ownerId, Status = FoundItemStatus.RESOLVED });

        var result = await Create(ownerId, lost, found).GetById(id, CancellationToken.None);

        Assert.Equal("RESOLVED", Ok(result).Status);
    }

    [Fact]
    public async Task Details_MissingOrInvalidAuthenticatedSubject_Returns401WithoutRepositoryLookup()
    {
        var lost = MissingLost();
        var found = MissingFound();
        var controller = Create("not-a-guid", lost, found);

        var result = await controller.GetById(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        lost.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        found.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void DetailsDto_ContainsNeitherHiddenInformationNorUserId()
    {
        var publicProperties = typeof(ItemDetailDto).GetProperties().Select(x => x.Name);
        Assert.DoesNotContain(publicProperties, x => x.Contains("hidden", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(publicProperties, x => x.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    private static ItemsController Create(Guid userId, Mock<ILostItemsRepository>? lost = null, Mock<IFoundItemsRepository>? found = null) =>
        Create(userId.ToString(), lost, found);

    private static ItemsController Create(string subject, Mock<ILostItemsRepository>? lost = null, Mock<IFoundItemsRepository>? found = null)
    {
        var controller = new ItemsController(new Mock<IItemsSearchRepository>().Object, (lost ?? MissingLost()).Object, (found ?? MissingFound()).Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test"))
            }
        };
        return controller;
    }

    private static Mock<ILostItemsRepository> MissingLost()
    {
        var repo = new Mock<ILostItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
        return repo;
    }

    private static Mock<IFoundItemsRepository> MissingFound()
    {
        var repo = new Mock<IFoundItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((FoundItem?)null);
        return repo;
    }

    private static ItemDetailDto Ok(ActionResult<ItemDetailDto> result) =>
        Assert.IsType<ItemDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static void AssertPublicJsonNeverLeaks(ItemDetailDto dto, string secret)
    {
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("hiddenInformation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
    }
}
