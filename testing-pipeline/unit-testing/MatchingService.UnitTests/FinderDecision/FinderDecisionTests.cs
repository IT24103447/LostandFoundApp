using System.Security.Claims;
using System.Text.Json;
using MatchingService.Claims;
using MatchingService.Controllers;
using MatchingService.Matches;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace MatchingService.UnitTests.FinderDecision;

public sealed class FinderDecisionTests
{
    private static readonly Guid LostReporterId = Guid.NewGuid();
    private static readonly Guid FinderId = Guid.NewGuid();

    [Fact]
    public async Task LostReporterConfirmed_ReviewShowsLostItemAndLetsFinderAct()
    {
        var match = Match("LOST_REPORTER_CONFIRMED");
        var repository = RepositoryFor(match);
        var controller = DetailsController(repository, FinderId);

        var result = Assert.IsType<OkObjectResult>(await controller.Details(match.Id, CancellationToken.None));
        var review = Assert.IsType<MatchListEntry>(result.Value);

        Assert.Equal("LOST_REPORTER_CONFIRMED", review.Status);
        Assert.Equal("waiting-on-you", review.Section);
        Assert.Equal("FOUND", review.YourRole);
        Assert.True(review.IsYourTurn);
        Assert.Equal(match.Lost, review.OtherItem);
        Assert.Equal("Lost blue backpack", review.OtherItem.Title);
        Assert.Equal("Library", review.OtherItem.Location);
        Assert.Equal(76m, review.Score);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData("FINDER_CONFIRMED")]
    [InlineData("CONFIRMED")]
    [InlineData("REJECTED")]
    public async Task OtherDecisionStates_DoNotOfferFinderActions(string status)
    {
        var match = Match(status);
        var repository = RepositoryFor(match);

        var review = await new MatchReadService(repository.Object)
            .GetByIdAsync(match.Id, FinderId, CancellationToken.None);

        Assert.False(review.IsYourTurn);
        Assert.Equal(status, review.Status);
    }

    [Fact]
    public async Task UnrelatedUser_CannotOpenFinderReview()
    {
        var match = Match("LOST_REPORTER_CONFIRMED");
        var controller = DetailsController(RepositoryFor(match), Guid.NewGuid());

        var result = Assert.IsType<ObjectResult>(await controller.Details(match.Id, CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task ConfirmedReview_DoesNotRevealLostReporterContactInDetailsResponse()
    {
        var match = Match("CONFIRMED");
        var controller = DetailsController(RepositoryFor(match), FinderId);

        var result = Assert.IsType<OkObjectResult>(await controller.Details(match.Id, CancellationToken.None));
        var json = JsonSerializer.Serialize(result.Value);

        Assert.DoesNotContain("Email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Lost blue backpack", json);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("reject")]
    [InlineData("return-contact")]
    public async Task DecisionAndContactRoutes_RequireAValidUserId(string action)
    {
        // Authentication is checked before the database-backed repository is used.
        var controller = new FinderDecisionsController(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = action switch
        {
            "confirm" => await controller.Confirm(Guid.NewGuid(), CancellationToken.None),
            "reject" => await controller.Reject(Guid.NewGuid(), CancellationToken.None),
            _ => await controller.ReturnContact(Guid.NewGuid(), CancellationToken.None)
        };

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    private static Mock<IMatchReadRepository> RepositoryFor(StoredMatch match)
    {
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(match.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);
        return repository;
    }

    private static MatchQueriesController DetailsController(Mock<IMatchReadRepository> repository, Guid viewerId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, viewerId.ToString())], "test"));

        return new MatchQueriesController(new MatchReadService(repository.Object))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static StoredMatch Match(string status) => new(
        Guid.NewGuid(), LostReporterId, FinderId, LostReporterId, status, true, 76m,
        new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        new ClaimItemView(Guid.NewGuid(), "LOST", "Lost blue backpack", "Bags", "Canvas bag",
            "2026-09-28", "Library", "https://example.test/lost.jpg"),
        new ClaimItemView(Guid.NewGuid(), "FOUND", "Found blue backpack", "Bags", "Canvas bag",
            "2026-09-29", "Bus station", "https://example.test/found.jpg"));
}
