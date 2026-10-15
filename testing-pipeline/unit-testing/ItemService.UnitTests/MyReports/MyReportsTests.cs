using System.Security.Claims;
using System.Text.Json;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.UnitTests.MyReports;

public class MyReportsTests
{
    [Fact]
    public async Task OwnLostAndFoundReports_AreReturnedWithCurrentStatusesAndWithoutPrivateDetails()
    {
        var owner = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        var ct = new CancellationTokenSource().Token;
        var lost = new List<LostItem>
        {
            new() { Id = Guid.NewGuid(), UserId = owner, Title = "Wallet", Status = LostItemStatus.ACTIVE,
                HiddenInformation = "lost-private-secret", CreatedAt = DateTime.UtcNow.AddDays(-2) },
            new() { Id = Guid.NewGuid(), UserId = owner, Title = "Phone", Status = LostItemStatus.RESOLVED,
                HiddenInformation = "another-private-secret", CreatedAt = DateTime.UtcNow.AddDays(-1) }
        };
        var found = new List<FoundItem>
        {
            new() { Id = Guid.NewGuid(), UserId = owner, Title = "Keys", Status = FoundItemStatus.ACTIVE,
                HiddenInformation = "found-private-secret", CreatedAt = DateTime.UtcNow.AddDays(-3) },
            new() { Id = Guid.NewGuid(), UserId = owner, Title = "Watch", Status = FoundItemStatus.RESOLVED,
                HiddenInformation = "other-found-secret", CreatedAt = DateTime.UtcNow.AddDays(-1) }
        };
        context.LostItems.Setup(x => x.GetByUserIdAsync(owner, ct)).ReturnsAsync(lost);
        context.FoundItems.Setup(x => x.GetByUserIdAsync(owner, ct)).ReturnsAsync(found);

        var lostResult = await context.LostController(owner).GetMine(ct);
        var foundResult = await context.FoundController(owner).GetMine(ct);

        var lostDtos = Assert.IsType<List<LostItemResponseDto>>(Assert.IsType<OkObjectResult>(lostResult.Result).Value);
        var foundDtos = Assert.IsType<List<FoundItemResponseDto>>(Assert.IsType<OkObjectResult>(foundResult.Result).Value);
        Assert.Equal(lost.Select(x => x.Id), lostDtos.Select(x => x.Id));
        Assert.Equal(found.Select(x => x.Id), foundDtos.Select(x => x.Id));
        Assert.All(lostDtos, x => Assert.Equal(owner, x.UserId));
        Assert.All(foundDtos, x => Assert.Equal(owner, x.UserId));
        Assert.Equal(new[] { "ACTIVE", "RESOLVED" }, lostDtos.Select(x => x.Status));
        Assert.Equal(new[] { "ACTIVE", "RESOLVED" }, foundDtos.Select(x => x.Status));
        Assert.Equal(lost.Select(x => x.CreatedAt), lostDtos.Select(x => x.CreatedAt));
        Assert.Equal(found.Select(x => x.CreatedAt), foundDtos.Select(x => x.CreatedAt));
        var responseJson = JsonSerializer.Serialize(new { Lost = lostDtos, Found = foundDtos });
        foreach (var secret in lost.Select(x => x.HiddenInformation).Concat(found.Select(x => x.HiddenInformation)))
            Assert.DoesNotContain(secret, responseJson);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task NoReports_ReturnsAnEmptyList(string type)
    {
        var owner = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        if (type == "LOST")
        {
            context.LostItems.Setup(x => x.GetByUserIdAsync(owner, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<LostItem>());
            var result = await context.LostController(owner).GetMine(CancellationToken.None);
            Assert.Empty(Assert.IsType<List<LostItemResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByUserIdAsync(owner, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<FoundItem>());
            var result = await context.FoundController(owner).GetMine(CancellationToken.None);
            Assert.Empty(Assert.IsType<List<FoundItemResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        }
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task MissingIdentity_CannotReadAnyReportHistory(string type)
    {
        var context = new ItemControllerTestContext();
        if (type == "LOST")
            Assert.IsType<UnauthorizedObjectResult>((await context.LostController(null).GetMine(CancellationToken.None)).Result);
        else
            Assert.IsType<UnauthorizedObjectResult>((await context.FoundController(null).GetMine(CancellationToken.None)).Result);

        context.LostItems.VerifyNoOtherCalls();
        context.FoundItems.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task JwtSubjectScopesHistoryToCaller_EvenIfNameIdentifierDiffers(string type)
    {
        var caller = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", caller.ToString()),
            new Claim(ClaimTypes.NameIdentifier, otherUser.ToString())
        ], "UnitTest"));

        if (type == "LOST")
        {
            context.LostItems.Setup(x => x.GetByUserIdAsync(caller, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<LostItem>());
            var controller = context.LostController(otherUser);
            controller.ControllerContext.HttpContext.User = principal;
            Assert.IsType<OkObjectResult>((await controller.GetMine(CancellationToken.None)).Result);
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByUserIdAsync(caller, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<FoundItem>());
            var controller = context.FoundController(otherUser);
            controller.ControllerContext.HttpContext.User = principal;
            Assert.IsType<OkObjectResult>((await controller.GetMine(CancellationToken.None)).Result);
        }

        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
    }
}
