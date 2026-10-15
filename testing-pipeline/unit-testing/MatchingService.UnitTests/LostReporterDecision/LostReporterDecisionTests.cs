using System.Security.Claims;
using System.Text.Json;
using MatchingService.Claims;
using MatchingService.Controllers;
using MatchingService.Matches;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace MatchingService.UnitTests.LostReporterDecision;

public sealed class LostReporterDecisionTests
{
    private static readonly Guid LostReporterId = Guid.NewGuid();
    private static readonly Guid FinderId = Guid.NewGuid();

    [Fact]
    public async Task FinderConfirmed_ReviewShowsFoundItemAndLetsLostReporterAct()
    {
        var match = Match("FINDER_CONFIRMED");
        var repository = RepositoryFor(match);
        var controller = DetailsController(repository, LostReporterId);

        var result = Assert.IsType<OkObjectResult>(await controller.Details(match.Id, CancellationToken.None));
        var review = Assert.IsType<MatchListEntry>(result.Value);

        Assert.Equal("FINDER_CONFIRMED", review.Status);
        Assert.Equal("waiting-on-you", review.Section);
        Assert.Equal("LOST", review.YourRole);
        Assert.True(review.IsYourTurn);
        Assert.Equal(match.Found, review.OtherItem);
        Assert.Equal("Found blue backpack", review.OtherItem.Title);
        Assert.Equal("Bus station", review.OtherItem.Location);
        Assert.Equal(76m, review.Score);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData("LOST_REPORTER_CONFIRMED")]
    [InlineData("CONFIRMED")]
    [InlineData("REJECTED")]
    public async Task OtherDecisionStates_DoNotOfferLostReporterActions(string status)
    {
        var match = Match(status);
        var repository = RepositoryFor(match);

        var review = await new MatchReadService(repository.Object)
            .GetByIdAsync(match.Id, LostReporterId, CancellationToken.None);

        Assert.False(review.IsYourTurn);
        Assert.Equal(status, review.Status);
    }

    [Fact]
    public async Task UnrelatedUser_CannotOpenLostReporterReview()
    {
        var match = Match("FINDER_CONFIRMED");
        var controller = DetailsController(RepositoryFor(match), Guid.NewGuid());

        var result = Assert.IsType<ObjectResult>(await controller.Details(match.Id, CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task ConfirmedReview_DoesNotRevealFinderContactInDetailsResponse()
    {
        var match = Match("CONFIRMED");
        var controller = DetailsController(RepositoryFor(match), LostReporterId);

        var result = Assert.IsType<OkObjectResult>(await controller.Details(match.Id, CancellationToken.None));
        var json = JsonSerializer.Serialize(result.Value);

        Assert.DoesNotContain("Email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Found blue backpack", json);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("reject")]
    [InlineData("return-contact")]
    public async Task DecisionAndContactRoutes_RequireAValidUserId(string action)
    {
        // Authentication is checked before the database-backed repository is used.
        var controller = new LostReporterDecisionsController(null!)
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
        Guid.NewGuid(), LostReporterId, FinderId, FinderId, status, true, 76m,
        new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        new ClaimItemView(Guid.NewGuid(), "LOST", "Lost blue backpack", "Bags", "Canvas bag",
            "2026-09-28", "Library", "https://example.test/lost.jpg"),
        new ClaimItemView(Guid.NewGuid(), "FOUND", "Found blue backpack", "Bags", "Canvas bag",
            "2026-09-29", "Bus station", "https://example.test/found.jpg"));
}
