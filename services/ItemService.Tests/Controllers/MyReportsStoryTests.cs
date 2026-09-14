using System.Security.Claims;
using ItemService.Configuration;
using ItemService.Controllers;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ItemService.Tests.Controllers;

/// <summary>Story 8: a history request is scoped exclusively to the JWT subject.</summary>
public sealed class MyReportsStoryTests
{
    // Story 8: owner-scoped report history, visible statuses, privacy, and JWT isolation.
    private static readonly Guid OwnerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // Verifies an owner receives all own lost rows with current status but no hidden information.
    [Fact]
    public async Task GetMyLostReports_ReturnsEveryRepositoryRowWithCurrentStatusesAndNoPrivateData()
    {
        var active = Lost("active lost", LostItemStatus.ACTIVE);
        var resolved = Lost("resolved lost", LostItemStatus.RESOLVED);
        var repo = new Mock<ILostItemsRepository>();
        repo.Setup(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([active, resolved]);

        var result = await LostController(repo, OwnerId).GetMine(CancellationToken.None);

        var response = Assert.IsType<List<LostItemResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([active.Id, resolved.Id], response.Select(x => x.Id));
        Assert.Equal(["ACTIVE", "RESOLVED"], response.Select(x => x.Status));
        var json = System.Text.Json.JsonSerializer.Serialize(response);
        Assert.DoesNotContain(active.HiddenInformation, json);
        Assert.DoesNotContain(resolved.HiddenInformation, json);
        repo.Verify(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.GetByUserIdAsync(OtherUserId, It.IsAny<CancellationToken>()), Times.Never);
    }

    // Verifies an owner receives all own found rows with current status but no hidden information.
    [Fact]
    public async Task GetMyFoundReports_ReturnsEveryRepositoryRowWithCurrentStatusesAndNoPrivateData()
    {
        var active = Found("active found", FoundItemStatus.ACTIVE);
        var resolved = Found("resolved found", FoundItemStatus.RESOLVED);
        var repo = new Mock<IFoundItemsRepository>();
        repo.Setup(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([active, resolved]);

        var result = await FoundController(repo, OwnerId).GetMine(CancellationToken.None);

        var response = Assert.IsType<List<FoundItemResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([active.Id, resolved.Id], response.Select(x => x.Id));
        Assert.Equal(["ACTIVE", "RESOLVED"], response.Select(x => x.Status));
        var json = System.Text.Json.JsonSerializer.Serialize(response);
        Assert.DoesNotContain(active.HiddenInformation, json);
        Assert.DoesNotContain(resolved.HiddenInformation, json);
        repo.Verify(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.GetByUserIdAsync(OtherUserId, It.IsAny<CancellationToken>()), Times.Never);
    }

    // Verifies an authenticated user with no reports receives a successful empty history.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task GetMine_EmptyHistoryReturnsOkWithAnEmptyCollection(string kind)
    {
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>();
            repo.Setup(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var result = await LostController(repo, OwnerId).GetMine(CancellationToken.None);
            Assert.Empty(Assert.IsType<List<LostItemResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>();
            repo.Setup(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var result = await FoundController(repo, OwnerId).GetMine(CancellationToken.None);
            Assert.Empty(Assert.IsType<List<FoundItemResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        }
    }

    // Verifies missing or malformed JWT subjects are rejected before querying history.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task GetMine_MissingOrMalformedJwtSubjectReturns401WithoutRepositoryAccess(string kind)
    {
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>();
            var controller = LostController(repo, OwnerId);
            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.IsType<UnauthorizedObjectResult>((await controller.GetMine(CancellationToken.None)).Result);
            SetSubject(controller, "not-a-guid");
            Assert.IsType<UnauthorizedObjectResult>((await controller.GetMine(CancellationToken.None)).Result);
            repo.Verify(r => r.GetByUserIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>();
            var controller = FoundController(repo, OwnerId);
            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.IsType<UnauthorizedObjectResult>((await controller.GetMine(CancellationToken.None)).Result);
            SetSubject(controller, "not-a-guid");
            Assert.IsType<UnauthorizedObjectResult>((await controller.GetMine(CancellationToken.None)).Result);
            repo.Verify(r => r.GetByUserIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies only the JWT subject, not another identity claim, scopes the history query.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task GetMine_UsesJwtSubInsteadOfAnyOtherIdentityValue(string kind)
    {
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>();
            repo.Setup(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var controller = LostController(repo, OtherUserId);
            SetSubjectAndNameIdentifier(controller, OwnerId, OtherUserId);
            await controller.GetMine(CancellationToken.None);
            repo.Verify(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>()), Times.Once);
            repo.Verify(r => r.GetByUserIdAsync(OtherUserId, It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>();
            repo.Setup(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var controller = FoundController(repo, OtherUserId);
            SetSubjectAndNameIdentifier(controller, OwnerId, OtherUserId);
            await controller.GetMine(CancellationToken.None);
            repo.Verify(r => r.GetByUserIdAsync(OwnerId, It.IsAny<CancellationToken>()), Times.Once);
            repo.Verify(r => r.GetByUserIdAsync(OtherUserId, It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    private static LostItemsController LostController(Mock<ILostItemsRepository> repo, Guid userId)
    {
        var controller = new LostItemsController(repo.Object, new Mock<IPhotoStorageService>().Object,
            new Mock<IEventPublisher>().Object, Options.Create(new ItemSettings()), Options.Create(new KafkaSettings()),
            new Mock<ILogger<LostItemsController>>().Object);
        SetSubject(controller, userId.ToString());
        return controller;
    }

    private static FoundItemsController FoundController(Mock<IFoundItemsRepository> repo, Guid userId)
    {
        var controller = new FoundItemsController(repo.Object, new Mock<IPhotoStorageService>().Object,
            new Mock<IEventPublisher>().Object, Options.Create(new ItemSettings()), Options.Create(new KafkaSettings()),
            new Mock<ILogger<FoundItemsController>>().Object);
        SetSubject(controller, userId.ToString());
        return controller;
    }

    private static void SetSubject(ControllerBase controller, string subject) =>
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "Test"))
            }
        };

    private static void SetSubjectAndNameIdentifier(ControllerBase controller, Guid subject, Guid nameIdentifier) =>
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("sub", subject.ToString()), new Claim(ClaimTypes.NameIdentifier, nameIdentifier.ToString())], "Test"))
            }
        };

    private static LostItem Lost(string title, LostItemStatus status) => new()
    {
        Id = Guid.NewGuid(), UserId = OwnerId, Title = title, Category = "Accessories", Description = "Lost item",
        DateLost = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), LastKnownLocation = "Library",
        HiddenInformation = $"private-{title}", Status = status, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static FoundItem Found(string title, FoundItemStatus status) => new()
    {
        Id = Guid.NewGuid(), UserId = OwnerId, Title = title, Category = "Accessories", Description = "Found item",
        DateFound = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), LocationFound = "Cafeteria",
        HiddenInformation = $"private-{title}", Status = status, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
}
